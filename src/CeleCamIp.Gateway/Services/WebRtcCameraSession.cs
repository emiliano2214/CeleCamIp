using CeleCamIp.Shared.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Reflection;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Una sesion WebRTC 1 a 1 entre este Gateway y un viewer puntual, para una
/// camara puntual. El Gateway es siempre el "offerer" (tiene el video); el
/// viewer solo responde con su answer SDP.
///
/// El video sale de FFmpeg leyendo directamente la URL RTSP de la camara
/// usando FFmpeg como proceso externo (workaround para el bug de SIPSorceryMedia.FFmpeg).
/// </summary>
public sealed class WebRtcCameraSession : IAsyncDisposable
{
    private readonly RTCPeerConnection _peerConnection;
    private readonly FFmpegProcessSource _videoSource;
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
    public event Action<int, int>? OnFrameSent;

    /// <summary>Se dispara cuando hay un error en FFmpeg.</summary>
    public event Action<string>? OnFFmpegError;

    private WebRtcCameraSession(string viewerConnectionId, string cameraId, RTCPeerConnection peerConnection, FFmpegProcessSource videoSource, ILogger? logger = null)
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
    /// Crea el formato de video H264 que se anuncia en el SDP.
    ///
    /// OJO: la firma real de VideoFormat (confirmada via reflexion sobre
    /// SIPSorceryMedia.Abstractions 10.0.16) es:
    ///   VideoFormat(VideoCodecsEnum codec, int formatID, int clockRate, string parameters)
    /// El parametro "parameters" es la linea fmtp del SDP (ej. packetization-mode,
    /// profile-level-id). Sin eso, el navegador rechaza el SDP con
    /// "Failed to parse codecs correctly" porque H264 requiere esos atributos
    /// para poder negociar el formato. La version anterior probaba varios
    /// constructores por reflexion "a ciegas" y ninguno seteaba este parametro.
    ///
    /// 96 es un payload type dinamico valido (rango 96-127) para H264.
    /// packetization-mode=1 = non-interleaved (el modo mas soportado).
    /// profile-level-id=42e01f = Baseline Profile, Level 3.1 (compatible con
    /// la gran mayoria de camaras IP y navegadores). Como FFmpeg usa "-c:v copy"
    /// (no re-codifica), el profile real de la camara puede diferir del anunciado;
    /// level-asymmetry-allowed=1 le dice al navegador que igual acepte streams
    /// con un profile/level distinto al declarado.
    /// </summary>
    private static List<VideoFormat> CreateH264Formats()
    {
        const int h264DynamicPayloadType = 96;
        const int h264ClockRate = 90000;
        const string h264FmtpParameters =
            "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1";

        var format = new VideoFormat(
            VideoCodecsEnum.H264,
            h264DynamicPayloadType,
            h264ClockRate,
            h264FmtpParameters);

        Console.WriteLine($"[WebRTC] ✅ H264 creado: PT={h264DynamicPayloadType}, ClockRate={h264ClockRate}, Fmtp=\"{h264FmtpParameters}\"");

        return new List<VideoFormat> { format };
    }


    /// <summary>
    /// Arma la sesion completa: fuente de video FFmpeg apuntando al RTSP de la
    /// camara, RTCPeerConnection con los ICE servers configurados y la oferta SDP.
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

        // ============================================================
        // USAR FFmpegProcessSource EN VEZ DE FFmpegFileSource
        // ============================================================
        var videoSource = new FFmpegProcessSource(rtspStreamUrl, logger);

        // Crear formatos H264 manualmente
        var videoFormats = CreateH264Formats();

        if (videoFormats == null || videoFormats.Count == 0)
        {
            logger?.LogError("[WebRTC] ❌ No se pudieron crear formatos de video H264");
            Console.WriteLine("[WebRTC] ❌ No se pudieron crear formatos de video H264");
            throw new InvalidOperationException("No se pudieron crear formatos de video H264");
        }

        logger?.LogInformation($"[WebRTC] ✅ {videoFormats.Count} formato(s) H264 creado(s)");
        Console.WriteLine($"[WebRTC] ✅ {videoFormats.Count} formato(s) H264 creado(s)");

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

        // Cada muestra H264 ya codificada que entrega ffmpeg se manda directo por RTP.
        videoSource.OnVideoSourceEncodedSample += (durationRtpUnits, sample) =>
        {
            lock (session._lock)
            {
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

                if (session._frameCount % 25 == 0)
                {
                    logger?.LogInformation($"[WebRTC] 📹 Frames enviados: {session._frameCount} para cámara {cameraId}");
                    Console.WriteLine($"[WebRTC] 📹 Frames enviados: {session._frameCount} para cámara {cameraId}");
                }

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

                session.OnFrameEncoded?.Invoke(session._frameCount);
                session.OnFrameSent?.Invoke(session._frameCount, sample.Length);

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

        // [FIX LATENCIA/TIMEOUT] Arrancar FFmpeg YA, en paralelo a la negociacion ICE,
        // en vez de esperar a que la RTCPeerConnection llegue a "connected". Antes,
        // toda la demora de conectar al RTSP + esperar el primer keyframe se sumaba
        // DESPUES de terminar la negociacion ICE (que ya de por si puede tardar
        // segundos si hay TURN de por medio). Si el ICE nunca llegaba a "connected"
        // (por la latencia extra de relayar por un TURN lejano, por ejemplo), la
        // sesion se cerraba sin haber arrancado FFmpeg ni una sola vez. Arrancando
        // ya mismo, FFmpeg tiene tiempo de conectar al RTSP y tener frames listos
        // MIENTRAS el ICE todavia esta negociando.
        try
        {
            await videoSource.StartVideo();
            logger?.LogInformation("[WebRTC] ✅ FFmpeg arrancado (en paralelo a la negociación ICE)");
            Console.WriteLine("[WebRTC] ✅ FFmpeg arrancado (en paralelo a la negociación ICE)");
            session.OnVideoStartFailed?.Invoke(null);
        }
        catch (Exception ex)
        {
            logger?.LogError($"[WebRTC] ❌ Error iniciando video: {ex.Message}");
            Console.WriteLine($"[WebRTC] ❌ Error iniciando video: {ex.Message}");
            session.OnVideoStartFailed?.Invoke(ex);
            throw;
        }

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

        peerConnection.onconnectionstatechange += state =>
        {
            logger?.LogInformation($"[WebRTC] Connection State: {state}");
            Console.WriteLine($"[WebRTC] Connection State: {state}");
            session.OnConnectionStateChanged?.Invoke(state);

            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    // El video ya se arranco antes (en paralelo a la negociacion ICE,
                    // ver comentario mas arriba); aca solo queda loguear el estado.
                    logger?.LogInformation("[WebRTC] Conexión establecida (video ya iniciado en paralelo)");
                    Console.WriteLine("[WebRTC] Conexión establecida (video ya iniciado en paralelo)");
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
