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
    private readonly AudioProcessSource _audioSource;
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

    /// <summary>Se dispara con informaci�n de bitrate cada 50 frames.</summary>
    public event Action<int>? OnBitrateUpdated;

    /// <summary>Se dispara cuando se env�a un frame por RTP.</summary>
    public event Action<int, int>? OnFrameSent;

    /// <summary>Se dispara cuando hay un error en FFmpeg.</summary>
    public event Action<string>? OnFFmpegError;

    /// <summary>Se dispara cuando hay un error en el proceso FFmpeg de audio (no fatal: la sesion sigue solo con video).</summary>
    public event Action<string>? OnFFmpegAudioError;

    /// <summary>Se dispara cuando el audio no pudo iniciarse (ej: la camara no tiene stream de audio). No fatal.</summary>
    public event Action<Exception>? OnAudioStartFailed;

    private WebRtcCameraSession(string viewerConnectionId, string cameraId, RTCPeerConnection peerConnection, FFmpegProcessSource videoSource, AudioProcessSource audioSource, ILogger? logger = null)
    {
        ViewerConnectionId = viewerConnectionId;
        CameraId = cameraId;
        _peerConnection = peerConnection;
        _videoSource = videoSource;
        _audioSource = audioSource;
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

        Console.WriteLine($"[WebRTC] ? H264 creado: PT={h264DynamicPayloadType}, ClockRate={h264ClockRate}, Fmtp=\"{h264FmtpParameters}\"");

        return new List<VideoFormat> { format };
    }

    /// <summary>
    /// Crea el formato de audio Opus que se anuncia en el SDP.
    ///
    /// El "channels" de la linea rtpmap SIEMPRE se anuncia como 2 para Opus
    /// en WebRTC/SDP (RFC 7587), sin importar si el audio codificado es en
    /// realidad mono - es una convencion del formato, no una declaracion de
    /// cuantos canales trae cada paquete (eso lo indica el propio paquete
    /// Opus, que es autodescriptivo). AudioProcessSource codifica en mono
    /// igual, que es lo tipico en microfonos de camaras IP.
    ///
    /// PT 97 (distinto del 96 que ya usa H264) para no chocar dynamic
    /// payload types dentro del mismo SDP. minptime=10;useinbandfec=1 son
    /// los parametros fmtp recomendados para Opus en WebRTC (mejor manejo
    /// de perdida de paquetes via FEC in-band).
    /// </summary>
    private static List<AudioFormat> CreateOpusFormats()
    {
        const int opusDynamicPayloadType = 97;
        const int opusClockRate = 48000;
        const int opusChannels = 2; // Convencion SDP RFC 7587, no el conteo real de canales codificados.
        const string opusFmtpParameters = "minptime=10;useinbandfec=1";

        var format = new AudioFormat(
            AudioCodecsEnum.OPUS,
            opusDynamicPayloadType,
            opusClockRate,
            opusChannels,
            opusFmtpParameters);

        Console.WriteLine($"[WebRTC] Opus creado: PT={opusDynamicPayloadType}, ClockRate={opusClockRate}, Fmtp=\"{opusFmtpParameters}\"");

        return new List<AudioFormat> { format };
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
        logger?.LogInformation($"[WebRTC] Creando sesi�n para Viewer={viewerConnectionId}, Camera={cameraId}");
        logger?.LogInformation($"[WebRTC] Stream URL: {rtspStreamUrl}");
        Console.WriteLine($"[WebRTC] Creando sesi�n para Viewer={viewerConnectionId}, Camera={cameraId}");
        Console.WriteLine($"[WebRTC] Stream URL: {rtspStreamUrl}");

        // ============================================================
        // USAR FFmpegProcessSource EN VEZ DE FFmpegFileSource
        // ============================================================
        var videoSource = new FFmpegProcessSource(rtspStreamUrl, logger);
        var audioSource = new AudioProcessSource(rtspStreamUrl, logger);

        // Crear formatos H264 manualmente
        var videoFormats = CreateH264Formats();

        if (videoFormats == null || videoFormats.Count == 0)
        {
            logger?.LogError("[WebRTC] ? No se pudieron crear formatos de video H264");
            Console.WriteLine("[WebRTC] ? No se pudieron crear formatos de video H264");
            throw new InvalidOperationException("No se pudieron crear formatos de video H264");
        }

        logger?.LogInformation($"[WebRTC] ? {videoFormats.Count} formato(s) H264 creado(s)");
        Console.WriteLine($"[WebRTC] ? {videoFormats.Count} formato(s) H264 creado(s)");

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

        // Agregar track de audio (Opus). Se agrega siempre (para que el SDP
        // incluya el m=audio y el viewer negocie el codec), aunque el arranque
        // de AudioProcessSource mas abajo pueda fallar si la camara no tiene
        // audio: en ese caso el m=audio queda negociado pero sin datos, y el
        // video sigue andando igual.
        logger?.LogInformation("[WebRTC] Agregando track de audio (Opus)...");
        Console.WriteLine("[WebRTC] Agregando track de audio (Opus)...");
        var audioFormats = CreateOpusFormats();
        var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendOnly);
        peerConnection.addTrack(audioTrack);

        var session = new WebRtcCameraSession(viewerConnectionId, cameraId, peerConnection, videoSource, audioSource, logger);

        // Suscribirse a eventos de FFmpeg para diagn�stico
        videoSource.OnVideoSourceError += error =>
        {
            logger?.LogError($"[WebRTC] ? FFmpeg Error: {error}");
            Console.WriteLine($"[WebRTC] ? FFmpeg Error: {error}");
            session.OnFFmpegError?.Invoke(error);
        };

        // Igual criterio que el error de video: se loguea y se propaga, pero
        // nunca tira abajo la sesion (el audio es secundario al video).
        audioSource.OnAudioSourceError += error =>
        {
            logger?.LogWarning($"[WebRTC] Audio Warning: {error}");
            Console.WriteLine($"[WebRTC] Audio Warning: {error}");
            session.OnFFmpegAudioError?.Invoke(error);
        };

        // Cada frame Opus ya codificado (20ms) se manda directo por RTP de audio.
        audioSource.OnAudioSourceEncodedSample += (durationRtpUnits, sample) =>
        {
            try
            {
                peerConnection.SendAudio(durationRtpUnits, sample);
            }
            catch (Exception ex)
            {
                logger?.LogError($"[WebRTC] Error enviando frame de audio: {ex.Message}");
                Console.WriteLine($"[WebRTC] Error enviando frame de audio: {ex.Message}");
            }
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
                    logger?.LogInformation($"[WebRTC] ?? PRIMER FRAME: {elapsed.TotalMilliseconds:F0}ms, tama�o: {sample.Length} bytes");
                    Console.WriteLine($"[WebRTC] ?? PRIMER FRAME: {elapsed.TotalMilliseconds:F0}ms, tama�o: {sample.Length} bytes");
                    session.OnFirstFrameElapsed?.Invoke(elapsed);
                }

                if (session._frameCount % 25 == 0)
                {
                    logger?.LogInformation($"[WebRTC] ?? Frames enviados: {session._frameCount} para c�mara {cameraId}");
                    Console.WriteLine($"[WebRTC] ?? Frames enviados: {session._frameCount} para c�mara {cameraId}");
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
                        logger?.LogInformation($"[WebRTC] ?? FPS: {fps:F1}, Bitrate: {bitrate} kbps, Frames: {session._frameCount}");
                        Console.WriteLine($"[WebRTC] ?? FPS: {fps:F1}, Bitrate: {bitrate} kbps, Frames: {session._frameCount}");
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
                    logger?.LogError($"[WebRTC] ? Error enviando frame: {ex.Message}");
                    Console.WriteLine($"[WebRTC] ? Error enviando frame: {ex.Message}");
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
            logger?.LogInformation("[WebRTC] ? FFmpeg arrancado (en paralelo a la negociaci�n ICE)");
            Console.WriteLine("[WebRTC] ? FFmpeg arrancado (en paralelo a la negociaci�n ICE)");
            session.OnVideoStartFailed?.Invoke(null);
        }
        catch (Exception ex)
        {
            logger?.LogError($"[WebRTC] ? Error iniciando video: {ex.Message}");
            Console.WriteLine($"[WebRTC] ? Error iniciando video: {ex.Message}");
            session.OnVideoStartFailed?.Invoke(ex);
            throw;
        }

        // Audio en paralelo, mismo criterio de arranque temprano que el video.
        // A DIFERENCIA del video, un fallo aca NO es fatal: si la camara no
        // tiene stream de audio (o ffmpeg no puede abrirlo), la sesion sigue
        // con video solo. Es el comportamiento esperado, no un bug: antes
        // de esto el audio nunca se transmitia (el pipeline de video usaba
        // -an a proposito), asi que "sin audio" seguia siendo el resultado
        // normal si la camara no lo tiene.
        try
        {
            await audioSource.StartAudio();
            logger?.LogInformation("[WebRTC] Audio (Opus) arrancado en paralelo");
            Console.WriteLine("[WebRTC] Audio (Opus) arrancado en paralelo");
        }
        catch (Exception ex)
        {
            logger?.LogWarning($"[WebRTC] No se pudo iniciar audio (sesion sigue solo con video): {ex.Message}");
            Console.WriteLine($"[WebRTC] No se pudo iniciar audio (sesion sigue solo con video): {ex.Message}");
            session.OnAudioStartFailed?.Invoke(ex);
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

        // [DIAGNOSTICO cierre ~10s tras el primer keyframe] Estos tres hooks
        // no estaban conectados. La hipotesis a confirmar: el burst grande
        // del primer keyframe (100-300KB fragmentado en RTP) satura algo y
        // el navegador deja de mandar RTCP receiver reports / keepalives a
        // tiempo, lo que dispara RTPSession.OnTimeout y cierra la conexion
        // sin que sea un fallo de ICE progresivo (por eso nunca vemos
        // "disconnected"/"failed", solo "closed" directo).
        peerConnection.OnTimeout += mediaType =>
        {
            logger?.LogWarning($"[WebRTC] ?? OnTimeout: no se recibio RTP/RTCP del peer para {mediaType}");
            Console.WriteLine($"[WebRTC] ?? OnTimeout: no se recibio RTP/RTCP del peer para {mediaType}");
        };

        peerConnection.OnReceiveReport += (remoteEndPoint, mediaType, rtcpCompoundPacket) =>
        {
            var rr = rtcpCompoundPacket.ReceiverReport;
            if (rr?.ReceptionReports is { Count: > 0 })
            {
                foreach (var report in rr.ReceptionReports)
                {
                    logger?.LogInformation($"[WebRTC] ?? RTCP ReceiverReport de {remoteEndPoint}: FractionLost={report.FractionLost}, PacketsLost={report.PacketsLost}, Jitter={report.Jitter}");
                    Console.WriteLine($"[WebRTC] ?? RTCP ReceiverReport de {remoteEndPoint}: FractionLost={report.FractionLost}, PacketsLost={report.PacketsLost}, Jitter={report.Jitter}");
                }
            }
        };

        peerConnection.oniceconnectionstatechange += iceState =>
        {
            logger?.LogInformation($"[WebRTC] ICE Connection State: {iceState}");
            Console.WriteLine($"[WebRTC] ICE Connection State: {iceState}");
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
                    logger?.LogInformation("[WebRTC] Conexi�n establecida (video ya iniciado en paralelo)");
                    Console.WriteLine("[WebRTC] Conexi�n establecida (video ya iniciado en paralelo)");
                    break;
                case RTCPeerConnectionState.closed:
                case RTCPeerConnectionState.failed:
                case RTCPeerConnectionState.disconnected:
                    var elapsed = DateTime.UtcNow - session._creationTime;
                    logger?.LogInformation($"[WebRTC] Sesi�n cerrada despu�s de {elapsed.TotalMilliseconds:F0}ms");
                    Console.WriteLine($"[WebRTC] Sesi�n cerrada despu�s de {elapsed.TotalMilliseconds:F0}ms");
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

        logger?.LogInformation($"[WebRTC] ? Oferta SDP creada: {offerInit.type}");
        logger?.LogInformation($"[WebRTC] SDP Length: {offerInit.sdp?.Length ?? 0} caracteres");
        Console.WriteLine($"[WebRTC] ? Oferta SDP creada: {offerInit.type}");
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
            _logger?.LogInformation("[WebRTC] ? Answer establecido correctamente");
            Console.WriteLine("[WebRTC] ? Answer establecido correctamente");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[WebRTC] ? Error estableciendo Answer: {ex.Message}");
            Console.WriteLine($"[WebRTC] ? Error estableciendo Answer: {ex.Message}");
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
            _logger?.LogError($"[WebRTC] ? Error agregando ICE candidate: {ex.Message}");
            Console.WriteLine($"[WebRTC] ? Error agregando ICE candidate: {ex.Message}");
        }
    }

    /// <summary>Obtiene estad�sticas de la sesi�n.</summary>
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
        _logger?.LogInformation($"[WebRTC] Disposing sesi�n para {CameraId}...");
        Console.WriteLine($"[WebRTC] Disposing sesi�n para {CameraId}...");

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
            await _audioSource.CloseAudio();
            _logger?.LogInformation("[WebRTC] Audio cerrado");
            Console.WriteLine("[WebRTC] Audio cerrado");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[WebRTC] Error cerrando audio: {ex.Message}");
            Console.WriteLine($"[WebRTC] Error cerrando audio: {ex.Message}");
        }

        try
        {
            _audioSource.Dispose();
            _logger?.LogInformation("[WebRTC] Audio source disposed");
            Console.WriteLine("[WebRTC] Audio source disposed");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[WebRTC] Error disposing audio source: {ex.Message}");
            Console.WriteLine($"[WebRTC] Error disposing audio source: {ex.Message}");
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

        _logger?.LogInformation($"[WebRTC] ? Sesi�n {CameraId} disposed correctamente");
        Console.WriteLine($"[WebRTC] ? Sesi�n {CameraId} disposed correctamente");
    }
}
