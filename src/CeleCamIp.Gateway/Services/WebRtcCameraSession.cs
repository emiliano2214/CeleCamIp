using CeleCamIp.Shared.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using System.Diagnostics;
using System.Reflection;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Una sesion WebRTC 1 a 1 entre este Gateway y un viewer puntual, para una
/// camara puntual. El Gateway es siempre el "offerer" (tiene el video); el
/// viewer solo responde con su answer SDP.
///
/// El video sale de FFmpeg leyendo directamente la URL RTSP de la camara
/// (SIPSorceryMedia.FFmpeg delega en libav/ffmpeg, que sabe hablar RTSP
/// nativamente). Las muestras ya codificadas (H264) que entrega FFmpeg se
/// mandan tal cual por RTP via RTCPeerConnection.SendVideo: no hay
/// transcodificacion adicional de nuestro lado.
///
/// IMPORTANTE: antes de usar esta clase hay que haber llamado una sola vez
/// SIPSorceryMedia.FFmpeg.FFmpegInit.EnsureBinariesRegistered() en el arranque
/// del proceso (se hace en Program.cs), o FFmpegFileSource no va a poder
/// cargar las librerias nativas de ffmpeg.
///
/// NOTA sobre diagnostico: SIPSorcery 10.0.16 no expone ninguna API publica
/// para conectar su logging interno (ICE/STUN/TURN/DTLS) a Microsoft.Extensions.Logging
/// (se verifico por reflexion, no existe SIPSorcery.LogFactory en esta version).
/// Por eso esta clase expone sus propios eventos de diagnostico
/// (OnLocalIceCandidate, OnConnectionStateChanged) para que quien la use
/// pueda loguear lo que esta pasando del lado ICE - es la unica ventana que
/// tenemos a ese proceso sin logging interno de la libreria.
/// </summary>
public sealed class WebRtcCameraSession : IAsyncDisposable
{
    private readonly RTCPeerConnection _peerConnection;
    private readonly FFmpegFileSource _videoSource;
    private readonly DateTime _creationTime;
    private int _frameCount;
    private bool _firstFrameCaptured;
    private DateTime? _firstFrameTime;
    private bool _disposed;
    private readonly object _lock = new object();
    private long _totalBytesSent;
    private DateTime _lastBitrateCheck;
    private int _lastFrameCountForBitrate;
    private readonly ILogger? _logger;

    public string ViewerConnectionId { get; }
    public string CameraId { get; }

    /// <summary>Se dispara cada vez que el Gateway junta un candidato ICE local, para mandarselo al viewer via el Server.</summary>
    public event Action<IceCandidateDto>? OnLocalIceCandidate;

    /// <summary>Se dispara en cada cambio de estado de la RTCPeerConnection (connecting/connected/failed/etc), para diagnostico.</summary>
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    /// <summary>Se dispara cuando la conexion se cae definitivamente (cerrada, fallida o desconectada), para que quien la creo la saque de su diccionario y la disponga.</summary>
    public event Action? OnClosed;

    /// <summary>Se dispara cuando el video comienza a transmitirse o falla al iniciar.</summary>
    public event Action<Exception?>? OnVideoStartFailed;

    /// <summary>Se dispara cada vez que se codifica un frame, con el conteo total hasta el momento.</summary>
    public event Action<int>? OnFrameEncoded;

    /// <summary>Se dispara cuando se codifica el primer frame, con el tiempo transcurrido desde la creacion de la sesion.</summary>
    public event Action<TimeSpan>? OnFirstFrameElapsed;

    /// <summary>Se dispara cuando la sesion se cierra, con el tiempo total de vida de la sesion.</summary>
    public event Action<TimeSpan>? OnClosedWithElapsed;

    /// <summary>Se dispara con información de bitrate cada 50 frames.</summary>
    public event Action<int>? OnBitrateUpdated;

    /// <summary>Se dispara cuando se envía un frame por RTP.</summary>
    public event Action<int, int>? OnFrameSent; // frameCount, bytes

    /// <summary>Se dispara cuando hay un error en FFmpeg.</summary>
    public event Action<string>? OnFFmpegError;

    private WebRtcCameraSession(string viewerConnectionId, string cameraId, RTCPeerConnection peerConnection, FFmpegFileSource videoSource, ILogger? logger = null)
    {
        ViewerConnectionId = viewerConnectionId;
        CameraId = cameraId;
        _peerConnection = peerConnection;
        _videoSource = videoSource;
        _logger = logger;
        _creationTime = DateTime.UtcNow;
        _frameCount = 0;
        _firstFrameCaptured = false;
        _totalBytesSent = 0;
        _lastBitrateCheck = DateTime.UtcNow;
        _lastFrameCountForBitrate = 0;
    }

    /// <summary>
    /// Obtiene información del formato de video usando reflexión.
    /// </summary>
    private static string GetFormatInfo(VideoFormat format)
    {
        try
        {
            var props = format.GetType().GetProperties();
            var info = new List<string>();
            foreach (var prop in props)
            {
                try
                {
                    var value = prop.GetValue(format);
                    if (value != null)
                    {
                        info.Add($"{prop.Name}={value}");
                    }
                }
                catch { }
            }
            return string.Join(", ", info);
        }
        catch
        {
            return format.ToString() ?? "Unknown";
        }
    }

    /// <summary>
    /// Verifica si un formato es H264 usando reflexión.
    /// </summary>
    private static bool IsH264Format(VideoFormat format)
    {
        try
        {
            // Intentar obtener CodecName
            var codecNameProp = format.GetType().GetProperty("CodecName");
            if (codecNameProp != null)
            {
                var codecName = codecNameProp.GetValue(format) as string;
                if (!string.IsNullOrEmpty(codecName) &&
                    (codecName.Contains("264") || codecName.Contains("h264")))
                {
                    return true;
                }
            }

            // Intentar obtener Codec
            var codecProp = format.GetType().GetProperty("Codec");
            if (codecProp != null)
            {
                var codec = codecProp.GetValue(format);
                if (codec != null)
                {
                    var codecStr = codec.ToString();
                    if (!string.IsNullOrEmpty(codecStr) &&
                        (codecStr.Contains("264") || codecStr.Contains("h264")))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Arma la sesion completa: fuente de video FFmpeg apuntando al RTSP de la
    /// camara, RTCPeerConnection con los ICE servers configurados (ExpressTURN
    /// incluido) y la oferta SDP lista para mandar al viewer.
    /// </summary>
    public static async Task<(WebRtcCameraSession Session, SdpDescriptionDto Offer)> CreateAsync(
        string viewerConnectionId,
        string cameraId,
        string rtspStreamUrl,
        IReadOnlyList<RTCIceServer> iceServers,
        ILogger? logger = null)
    {
        logger?.LogInformation($"[WebRTC] Creando sesión para Viewer={viewerConnectionId}, Camera={cameraId}");
        logger?.LogInformation($"[WebRTC] Stream URL: {rtspStreamUrl}");
        Console.WriteLine($"[WebRTC] Creando sesión para Viewer={viewerConnectionId}, Camera={cameraId}");
        Console.WriteLine($"[WebRTC] Stream URL: {rtspStreamUrl}");

        // repeat=true: si ffmpeg pierde el stream (ej: la camara cortó un instante),
        // reintenta en vez de terminar la sesion. audioEncoder=null y audioFrameSize=0
        // porque por ahora solo mandamos video. useVideo=true.
        var videoSource = new FFmpegFileSource(rtspStreamUrl, true, null!, 0, true);

        // Obtener formatos de video con diagnóstico
        logger?.LogInformation("[WebRTC] Obteniendo formatos de video...");
        Console.WriteLine("[WebRTC] Obteniendo formatos de video...");
        var videoFormats = videoSource.GetVideoSourceFormats();

        if (videoFormats == null || videoFormats.Count == 0)
        {
            logger?.LogError("[WebRTC] ❌ No se encontraron formatos de video");
            Console.WriteLine("[WebRTC] ❌ No se encontraron formatos de video");
            throw new InvalidOperationException("No se encontraron formatos de video para el stream RTSP");
        }

        logger?.LogInformation($"[WebRTC] ✅ {videoFormats.Count} formato(s) encontrado(s)");
        Console.WriteLine($"[WebRTC] ✅ {videoFormats.Count} formato(s) encontrado(s)");
        foreach (var format in videoFormats)
        {
            try
            {
                var info = GetFormatInfo(format);
                var isH264 = IsH264Format(format);
                logger?.LogInformation($"[WebRTC]   - Formato: {info} {(isH264 ? "✅ H264" : "")}");
                Console.WriteLine($"[WebRTC]   - Formato: {info} {(isH264 ? "✅ H264" : "")}");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[WebRTC]   - Error obteniendo info del formato: {ex.Message}");
                Console.WriteLine($"[WebRTC]   - Error obteniendo info del formato: {ex.Message}");
            }
        }

        // Configurar ICE
        logger?.LogInformation($"[WebRTC] Configurando ICE con {iceServers.Count} servidores...");
        Console.WriteLine($"[WebRTC] Configurando ICE con {iceServers.Count} servidores...");
        var config = new RTCConfiguration { iceServers = iceServers.ToList() };
        var peerConnection = new RTCPeerConnection(config);

        // Agregar track de video
        logger?.LogInformation("[WebRTC] Agregando track de video...");
        Console.WriteLine("[WebRTC] Agregando track de video...");
        var track = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendOnly);
        peerConnection.addTrack(track);

        var session = new WebRtcCameraSession(viewerConnectionId, cameraId, peerConnection, videoSource, logger);

        // Suscribirse a eventos de FFmpeg para diagnóstico
        videoSource.OnVideoSourceError += error =>
        {
            logger?.LogError($"[WebRTC] ❌ FFmpeg Error: {error}");
            Console.WriteLine($"[WebRTC] ❌ FFmpeg Error: {error}");
            session.OnFFmpegError?.Invoke(error);
        };

        // Nota: OnVideoSourceInfo no existe en todas las versiones, lo manejamos con try-catch
        try
        {
            // Intentar suscribir el evento si existe (usando reflexión)
            var eventInfo = videoSource.GetType().GetEvent("OnVideoSourceInfo");
            if (eventInfo != null)
            {
                var handler = new Action<string>(info =>
                {
                    logger?.LogInformation($"[WebRTC] ℹ️ FFmpeg Info: {info}");
                    Console.WriteLine($"[WebRTC] ℹ️ FFmpeg Info: {info}");
                });
                eventInfo.AddEventHandler(videoSource, handler);
            }
        }
        catch
        {
            // El evento no existe, ignorar
        }

        // Cada muestra H264 ya codificada que entrega ffmpeg se manda directo por RTP.
        videoSource.OnVideoSourceEncodedSample += (durationRtpUnits, sample) =>
        {
            lock (session._lock)
            {
                // Contar frames y capturar el primero
                session._frameCount++;
                session._totalBytesSent += sample.Length;

                if (!session._firstFrameCaptured)
                {
                    session._firstFrameCaptured = true;
                    session._firstFrameTime = DateTime.UtcNow;
                    var elapsed = session._firstFrameTime.Value - session._creationTime;
                    logger?.LogInformation($"[WebRTC] 🎬 PRIMER FRAME: {elapsed.TotalMilliseconds:F0}ms, tamaño: {sample.Length} bytes");
                    Console.WriteLine($"[WebRTC] 🎬 PRIMER FRAME: {elapsed.TotalMilliseconds:F0}ms, tamaño: {sample.Length} bytes");
                    session.OnFirstFrameElapsed?.Invoke(elapsed);
                }

                // Log cada 25 frames
                if (session._frameCount % 25 == 0)
                {
                    logger?.LogInformation($"[WebRTC] 📹 Frames enviados: {session._frameCount} para cámara {cameraId}");
                    Console.WriteLine($"[WebRTC] 📹 Frames enviados: {session._frameCount} para cámara {cameraId}");
                }

                // Calcular bitrate cada 50 frames
                if (session._frameCount % 50 == 0)
                {
                    var now = DateTime.UtcNow;
                    var elapsed = now - session._lastBitrateCheck;
                    var framesDiff = session._frameCount - session._lastFrameCountForBitrate;

                    if (elapsed.TotalSeconds > 0 && framesDiff > 0)
                    {
                        var fps = framesDiff / elapsed.TotalSeconds;
                        var bitrate = (int)(session._totalBytesSent * 8 / elapsed.TotalSeconds / 1000);
                        logger?.LogInformation($"[WebRTC] 📊 FPS: {fps:F1}, Bitrate: {bitrate} kbps, Frames: {session._frameCount}");
                        Console.WriteLine($"[WebRTC] 📊 FPS: {fps:F1}, Bitrate: {bitrate} kbps, Frames: {session._frameCount}");
                        session.OnBitrateUpdated?.Invoke(bitrate);
                    }

                    session._totalBytesSent = 0;
                    session._lastBitrateCheck = now;
                    session._lastFrameCountForBitrate = session._frameCount;
                }

                // Disparar eventos
                session.OnFrameEncoded?.Invoke(session._frameCount);
                session.OnFrameSent?.Invoke(session._frameCount, sample.Length);

                // Enviar el frame por RTP
                try
                {
                    peerConnection.SendVideo(durationRtpUnits, sample);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"[WebRTC] ❌ Error enviando frame: {ex.Message}");
                    Console.WriteLine($"[WebRTC] ❌ Error enviando frame: {ex.Message}");
                }
            }
        };

        peerConnection.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                logger?.LogInformation("[WebRTC] ICE gathering completado");
                Console.WriteLine("[WebRTC] ICE gathering completado");
                return;
            }

            var candidateType = candidate.candidate.Contains("typ srflx") ? "STUN" :
                               candidate.candidate.Contains("typ relay") ? "TURN" :
                               candidate.candidate.Contains("typ host") ? "HOST" : "UNKNOWN";

            logger?.LogInformation($"[WebRTC] ICE Candidate ({candidateType}): {candidate.candidate}");
            Console.WriteLine($"[WebRTC] ICE Candidate ({candidateType}): {candidate.candidate}");

            session.OnLocalIceCandidate?.Invoke(new IceCandidateDto(
                candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex, candidate.usernameFragment));
        };

        peerConnection.onconnectionstatechange += async state =>
        {
            logger?.LogInformation($"[WebRTC] Connection State: {state}");
            Console.WriteLine($"[WebRTC] Connection State: {state}");
            session.OnConnectionStateChanged?.Invoke(state);

            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    // Recien arrancamos a mandar frames cuando el DTLS/ICE ya cerro:
                    // antes de eso SendVideo no tiene a donde mandar los paquetes.
                    logger?.LogInformation("[WebRTC] Conexión establecida, iniciando video...");
                    Console.WriteLine("[WebRTC] Conexión establecida, iniciando video...");
                    try
                    {
                        await videoSource.StartVideo();
                        logger?.LogInformation("[WebRTC] ✅ Video iniciado correctamente");
                        Console.WriteLine("[WebRTC] ✅ Video iniciado correctamente");
                        session.OnVideoStartFailed?.Invoke(null);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError($"[WebRTC] ❌ Error iniciando video: {ex.Message}");
                        Console.WriteLine($"[WebRTC] ❌ Error iniciando video: {ex.Message}");
                        session.OnVideoStartFailed?.Invoke(ex);
                        throw;
                    }
                    break;
                case RTCPeerConnectionState.closed:
                case RTCPeerConnectionState.failed:
                case RTCPeerConnectionState.disconnected:
                    var elapsed = DateTime.UtcNow - session._creationTime;
                    logger?.LogInformation($"[WebRTC] Sesión cerrada después de {elapsed.TotalMilliseconds:F0}ms");
                    Console.WriteLine($"[WebRTC] Sesión cerrada después de {elapsed.TotalMilliseconds:F0}ms");
                    session.OnClosedWithElapsed?.Invoke(elapsed);
                    session.OnClosed?.Invoke();
                    break;
            }
        };

        // Crear oferta
        logger?.LogInformation("[WebRTC] Creando oferta SDP...");
        Console.WriteLine("[WebRTC] Creando oferta SDP...");
        var offerInit = peerConnection.createOffer(new RTCOfferOptions());
        await peerConnection.setLocalDescription(offerInit);

        logger?.LogInformation($"[WebRTC] ✅ Oferta SDP creada: {offerInit.type}");
        logger?.LogInformation($"[WebRTC] SDP Length: {offerInit.sdp?.Length ?? 0} caracteres");
        Console.WriteLine($"[WebRTC] ✅ Oferta SDP creada: {offerInit.type}");
        Console.WriteLine($"[WebRTC] SDP Length: {offerInit.sdp?.Length ?? 0} caracteres");

        return (session, new SdpDescriptionDto(offerInit.type.ToString().ToLowerInvariant(), offerInit.sdp));
    }

    /// <summary>Aplica la respuesta SDP que mando el viewer.</summary>
    public void SetRemoteAnswer(SdpDescriptionDto answer)
    {
        _logger?.LogInformation($"[WebRTC] Estableciendo Answer SDP para {ViewerConnectionId}...");
        _logger?.LogInformation($"[WebRTC] Answer Type: {answer.Type}, Length: {answer.Sdp?.Length ?? 0}");
        Console.WriteLine($"[WebRTC] Estableciendo Answer SDP para {ViewerConnectionId}...");
        Console.WriteLine($"[WebRTC] Answer Type: {answer.Type}, Length: {answer.Sdp?.Length ?? 0}");

        try
        {
            _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answer.Sdp
            });
            _logger?.LogInformation("[WebRTC] ✅ Answer establecido correctamente");
            Console.WriteLine("[WebRTC] ✅ Answer establecido correctamente");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[WebRTC] ❌ Error estableciendo Answer: {ex.Message}");
            Console.WriteLine($"[WebRTC] ❌ Error estableciendo Answer: {ex.Message}");
            throw;
        }
    }

    /// <summary>Agrega un candidato ICE que mando el viewer (trickle ICE).</summary>
    public void AddRemoteIceCandidate(IceCandidateDto candidate)
    {
        _logger?.LogInformation($"[WebRTC] Agregando ICE candidate remoto: {candidate.Candidate}");
        Console.WriteLine($"[WebRTC] Agregando ICE candidate remoto: {candidate.Candidate}");

        try
        {
            _peerConnection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                usernameFragment = candidate.UsernameFragment
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[WebRTC] ❌ Error agregando ICE candidate: {ex.Message}");
            Console.WriteLine($"[WebRTC] ❌ Error agregando ICE candidate: {ex.Message}");
        }
    }

    /// <summary>Obtiene estadísticas de la sesión.</summary>
    public (int FrameCount, TimeSpan? Elapsed, bool HasVideo) GetStats()
    {
        lock (_lock)
        {
            var elapsed = _firstFrameTime.HasValue
                ? _firstFrameTime.Value - _creationTime
                : (TimeSpan?)null;

            return (_frameCount, elapsed, _firstFrameCaptured);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger?.LogInformation($"[WebRTC] Disposing sesión para {CameraId}...");
        Console.WriteLine($"[WebRTC] Disposing sesión para {CameraId}...");

        try
        {
            await _videoSource.CloseVideo();
            _logger?.LogInformation("[WebRTC] Video cerrado");
            Console.WriteLine("[WebRTC] Video cerrado");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[WebRTC] Error cerrando video: {ex.Message}");
            Console.WriteLine($"[WebRTC] Error cerrando video: {ex.Message}");
        }

        try
        {
            _videoSource.Dispose();
            _logger?.LogInformation("[WebRTC] Video source disposed");
            Console.WriteLine("[WebRTC] Video source disposed");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[WebRTC] Error disposing video source: {ex.Message}");
            Console.WriteLine($"[WebRTC] Error disposing video source: {ex.Message}");
        }

        try
        {
            _peerConnection.close();
            _peerConnection.Dispose();
            _logger?.LogInformation("[WebRTC] PeerConnection cerrada");
            Console.WriteLine("[WebRTC] PeerConnection cerrada");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[WebRTC] Error cerrando PeerConnection: {ex.Message}");
            Console.WriteLine($"[WebRTC] Error cerrando PeerConnection: {ex.Message}");
        }

        _logger?.LogInformation($"[WebRTC] ✅ Sesión {CameraId} disposed correctamente");
        Console.WriteLine($"[WebRTC] ✅ Sesión {CameraId} disposed correctamente");
    }
}