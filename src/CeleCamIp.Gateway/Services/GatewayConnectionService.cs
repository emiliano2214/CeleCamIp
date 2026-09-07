using CeleCamIp.Gateway.Services;
using CeleCamIp.Shared.Cameras;
using CeleCamIp.Shared.WebRtc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;
using SIPSorceryMedia.FFmpeg;
using System.Collections.Concurrent;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Configuracion leida desde appsettings.json (secciones "Server", "House" e "IceServers").
/// </summary>
public class GatewayOptions
{
    public string HubUrl { get; set; } = string.Empty;
    public string HouseId { get; set; } = string.Empty;

    /// <summary>Clave compartida para autenticarse contra el Hub (Auth:ApiKey del lado del Server).</summary>
    public string? ApiKey { get; set; }

    /// <summary>IPs de camaras a detectar en esta casa. Por ahora es una lista fija en
    /// appsettings; mas adelante puede reemplazarse por un descubrimiento automatico en la LAN.</summary>
    public List<string> CameraIps { get; set; } = new();

    /// <summary>
    /// Credenciales por defecto para probar contra todas las camaras de la lista.
    /// NOTA: esto es solo para el prototipo local; en produccion no deberian vivir
    /// en texto plano en appsettings (usar User Secrets / variables de entorno / vault).
    /// </summary>
    public string? CameraUsername { get; set; }
    public string? CameraPassword { get; set; }

    /// <summary>
    /// Servidores STUN/TURN para armar las RTCPeerConnection del puente RTSP->WebRTC.
    /// Necesarios para que el video llegue a viewers en WAN (fuera de la LAN de la casa).
    /// </summary>
    public List<IceServerDto> IceServers { get; set; } = new();
}

/// <summary>
/// Mantiene la conexion SALIENTE del Gateway (casa) hacia el Hub del Server (VPS).
/// Es el nucleo de la arquitectura "casa inicia la conexion hacia afuera": no hace
/// falta abrir ningun puerto en el router ni lidiar con CGNAT, porque la conexion
/// siempre sale desde aca hacia el servidor.
///
/// Ademas de mantener la conexion con reconexion automatica, cada vez que logra
/// registrarse corre CameraDetectionService contra las IPs configuradas y le
/// reporta al Server las camaras encontradas (con su StreamUrl ya resuelto),
/// para que la app movil (viewer) tenga algo real para mostrar.
///
/// Tambien atiende la senializacion WebRTC (SDP/ICE) relayada por el Server:
/// cuando un viewer pide ver una camara, este servicio arma una RTCPeerConnection
/// puntual (WebRtcCameraSession) que lee el RTSP de esa camara y manda el video
/// directo al viewer.
/// </summary>
public class GatewayConnectionService : BackgroundService
{
    private readonly ILogger<GatewayConnectionService> _logger;
    private readonly GatewayOptions _options;
    private readonly CameraDetectionService _detectionService;
    private HubConnection? _connection;

    /// <summary>Ultimas camaras detectadas, indexadas por Id (la IP), para resolver el StreamUrl cuando llega un pedido de stream.</summary>
    private readonly ConcurrentDictionary<string, CameraDescriptor> _knownCameras = new();

    /// <summary>Sesiones WebRTC activas, indexadas por "viewerConnectionId:cameraId".</summary>
    private readonly ConcurrentDictionary<string, WebRtcCameraSession> _activeSessions = new();

    public GatewayConnectionService(
        ILogger<GatewayConnectionService> logger,
        IOptions<GatewayOptions> options,
        CameraDetectionService detectionService)
    {
        _logger = logger;
        _options = options.Value;
        _detectionService = detectionService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.HubUrl))
        {
            _logger.LogError("No se configuro Server:HubUrl en appsettings.json. El Gateway no puede conectarse.");
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl, options => options.AccessTokenProvider = () => Task.FromResult(_options.ApiKey))
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.On<string>("Registered", houseId =>
        {
            _logger.LogInformation("Servidor confirmo el registro de la casa: {HouseId}", houseId);
        });

        RegisterWebRtcSignalingHandlers(stoppingToken);

        _connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "Conexion con el servidor perdida. Reintentando...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Reconectado al servidor. ConnectionId: {ConnectionId}", connectionId);
            await RegisterHouseAsync(stoppingToken);
        };

        _connection.Closed += async error =>
        {
            _logger.LogWarning(error, "Conexion cerrada definitivamente. Reintentando manualmente en 5s...");
            await Task.Delay(5000, stoppingToken);
            await ConnectWithRetryAsync(stoppingToken);
        };

        await ConnectWithRetryAsync(stoppingToken);

        // Mantiene vivo el BackgroundService mientras dure el host.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Apagado normal del host.
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Conectando al servidor en {HubUrl}...", _options.HubUrl);
                await _connection!.StartAsync(ct);
                _logger.LogInformation("Conexion establecida con el servidor.");
                await RegisterHouseAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo conectar al servidor. Reintentando en 5s...");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task RegisterHouseAsync(CancellationToken ct)
    {
        if (_connection is null || string.IsNullOrWhiteSpace(_options.HouseId))
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync("RegisterHouse", _options.HouseId, ct);
            _logger.LogInformation("Solicitud de registro enviada para la casa: {HouseId}", _options.HouseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al registrar la casa {HouseId}", _options.HouseId);
            return;
        }

        // No bloquea el registro: la deteccion de camaras puede tardar (varios
        // segundos por IP si prueba rutas RTSP comunes una por una).
        _ = DetectAndReportCamerasAsync(ct);
    }

    /// <summary>
    /// Corre la cascada de deteccion (CameraDetectionService + adapters de Shared)
    /// contra cada IP configurada, resuelve el StreamUrl real via adapter.ConnectAsync
    /// y le manda el resultado al Server para que la app movil lo vea.
    /// </summary>
    private async Task DetectAndReportCamerasAsync(CancellationToken ct)
    {
        if (_options.CameraIps.Count == 0)
        {
            _logger.LogInformation("No hay IPs de camaras configuradas en House:CameraIps.");
            return;
        }

        var results = new List<CameraDescriptor>();

        foreach (var ip in _options.CameraIps)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = new CameraDescriptor
            {
                Id = ip,
                Name = $"Camara {ip}",
                IpAddress = ip,
                Username = _options.CameraUsername,
                Password = _options.CameraPassword,
            };

            try
            {
                var detected = await _detectionService.DetectAsync(candidate, ct);
                if (detected.AdapterUsed is null)
                {
                    _logger.LogWarning("No se detecto ningun protocolo soportado para la camara {Ip}", ip);
                    continue;
                }

                var adapter = _detectionService.GetAdapter(detected);
                if (adapter is null)
                {
                    continue;
                }

                var connection = await adapter.ConnectAsync(detected, ct);
                var final = detected with { StreamUrl = connection.NormalizedStreamUrl };
                results.Add(final);

                // Se guarda para poder resolver el RTSP real cuando un viewer pida el stream por WebRTC.
                _knownCameras[final.Id] = final;

                _logger.LogInformation(
                    "Camara {Ip} lista via {Adapter}: {Url}",
                    ip, detected.AdapterUsed, connection.NormalizedStreamUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error detectando/conectando la camara {Ip}", ip);
            }
        }

        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("No se pudo reportar camaras: la conexion con el servidor no esta activa.");
            return;
        }

        try
        {
            await _connection.InvokeAsync("ReportCameras", _options.HouseId, results, ct);
            _logger.LogInformation("Reportadas {Count} camara(s) al servidor.", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al reportar camaras al servidor.");
        }
    }

    // ===================== Senializacion WebRTC (SDP/ICE) =====================

    /// <summary>
    /// Registra los handlers de los mensajes de senializacion que llegan relayados
    /// por el Server. Se registran antes de StartAsync, en el mismo lugar donde
    /// ya se registraba "Registered".
    /// </summary>
    private void RegisterWebRtcSignalingHandlers(CancellationToken hostStoppingToken)
    {
        _connection!.On<string, string>("StreamRequested", async (viewerConnectionId, cameraId) =>
        {
            await HandleStreamRequestedAsync(viewerConnectionId, cameraId, hostStoppingToken);
        });

        _connection!.On<string, string, SdpDescriptionDto>("ReceiveAnswer", (viewerConnectionId, cameraId, answer) =>
        {
            if (_activeSessions.TryGetValue(SessionKey(viewerConnectionId, cameraId), out var session))
            {
                session.SetRemoteAnswer(answer);
            }
            else
            {
                _logger.LogWarning("Llego un answer para una sesion que no existe (viewer {ViewerConnectionId}, camara {CameraId})", viewerConnectionId, cameraId);
            }
        });

        _connection!.On<string, string, IceCandidateDto>("ReceiveIceCandidate", (viewerConnectionId, cameraId, candidate) =>
        {
            if (_activeSessions.TryGetValue(SessionKey(viewerConnectionId, cameraId), out var session))
            {
                session.AddRemoteIceCandidate(candidate);
            }
        });
    }

    private static string SessionKey(string viewerConnectionId, string cameraId) => $"{viewerConnectionId}:{cameraId}";

    /// <summary>
    /// Arma una RTCPeerConnection puntual para un viewer que pidio ver una camara,
    /// y le manda la oferta SDP de vuelta a traves del Server.
    /// </summary>
    private async Task HandleStreamRequestedAsync(string viewerConnectionId, string cameraId, CancellationToken ct)
    {
        if (_connection is null)
        {
            return;
        }

        if (!_knownCameras.TryGetValue(cameraId, out var camera) || string.IsNullOrWhiteSpace(camera.StreamUrl))
        {
            _logger.LogWarning("Viewer {ViewerConnectionId} pidio la camara {CameraId} pero no esta disponible.", viewerConnectionId, cameraId);
            await _connection.InvokeAsync("ReportStreamError", viewerConnectionId, cameraId, "Camara no disponible.", ct);
            return;
        }

        var key = SessionKey(viewerConnectionId, cameraId);

        try
        {
            var iceServers = _options.IceServers.Select(s => new RTCIceServer
            {
                urls = s.Urls,
                username = s.Username,
                credential = s.Credential
            }).ToList();

            var (session, offer) = await WebRtcCameraSession.CreateAsync(viewerConnectionId, cameraId, camera.StreamUrl, iceServers);

            session.OnLocalIceCandidate += candidate =>
            {
                _logger.LogInformation("ICE candidato local (viewer {ViewerConnectionId}, camara {CameraId}): {Candidate}", viewerConnectionId, cameraId, candidate.Candidate);
                _ = _connection.InvokeAsync("SendIceCandidateToViewer", viewerConnectionId, cameraId, candidate, ct);
            };

            session.OnConnectionStateChanged += state =>
            {
                _logger.LogInformation("Estado de RTCPeerConnection (viewer {ViewerConnectionId}, camara {CameraId}): {State}", viewerConnectionId, cameraId, state);
            };

            session.OnVideoStartFailed += ex =>
            {
                if (ex is null)
                {
                    _logger.LogInformation("FFmpeg arranco el video OK (viewer {ViewerConnectionId}, camara {CameraId})", viewerConnectionId, cameraId);
                }
                else
                {
                    _logger.LogError(ex, "FFmpeg fallo al arrancar el video (viewer {ViewerConnectionId}, camara {CameraId})", viewerConnectionId, cameraId);
                }
            };

            session.OnFrameEncoded += count =>
            {
                if (count == 1 || count % 50 == 0)
                {
                    _logger.LogInformation("Frames de video enviados hasta ahora (viewer {ViewerConnectionId}, camara {CameraId}): {Count}", viewerConnectionId, cameraId, count);
                }
            };

            session.OnFirstFrameElapsed += elapsed =>
            {
                _logger.LogInformation("Primer frame codificado a los {ElapsedMs} ms de crear la sesion (viewer {ViewerConnectionId}, camara {CameraId})", elapsed.TotalMilliseconds, viewerConnectionId, cameraId);
            };

            session.OnClosedWithElapsed += elapsed =>
            {
                _logger.LogInformation("Conexion cerrada a los {ElapsedMs} ms de crear la sesion (viewer {ViewerConnectionId}, camara {CameraId})", elapsed.TotalMilliseconds, viewerConnectionId, cameraId);
            };

            session.OnClosed += () =>
            {
                if (_activeSessions.TryRemove(key, out var closedSession))
                {
                    _logger.LogInformation("Sesion WebRTC cerrada (viewer {ViewerConnectionId}, camara {CameraId})", viewerConnectionId, cameraId);
                    _ = closedSession.DisposeAsync();
                }
            };

            _activeSessions[key] = session;

            await _connection.InvokeAsync("SendOffer", viewerConnectionId, cameraId, offer, ct);
            _logger.LogInformation("Oferta WebRTC enviada para camara {CameraId} a viewer {ViewerConnectionId}", cameraId, viewerConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo armar la sesion WebRTC para camara {CameraId}", cameraId);
            await _connection.InvokeAsync("ReportStreamError", viewerConnectionId, cameraId, "No se pudo iniciar la transmision.", ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _activeSessions.Values)
        {
            await session.DisposeAsync();
        }
        _activeSessions.Clear();

        if (_connection is not null)
        {
            await _connection.StopAsync(cancellationToken);
            await _connection.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }

    public async Task DiagnoseAsync()
    {
        _logger.LogInformation("=== DIAGNÓSTICO DEL SISTEMA ===");

        // 1. Estado de la conexión SignalR
        _logger.LogInformation("Estado SignalR: {State}", _connection?.State);

        // 2. Cámaras conocidas
        _logger.LogInformation("Cámaras conocidas: {Count}", _knownCameras.Count);
        foreach (var cam in _knownCameras.Values)
        {
            _logger.LogInformation("  - {Id}: {Name}, URL: {Url}, Adapter: {Adapter}",
                cam.Id, cam.Name, cam.StreamUrl, cam.AdapterUsed);
        }

        // 3. Sesiones activas
        _logger.LogInformation("Sesiones activas: {Count}", _activeSessions.Count);
        foreach (var session in _activeSessions.Values)
        {
            _logger.LogInformation("  - Viewer: {Viewer}, Camera: {Camera}",
                session.ViewerConnectionId, session.CameraId);
        }

        // 4. Probar cada cámara
        foreach (var cam in _knownCameras.Values)
        {
            if (!string.IsNullOrWhiteSpace(cam.StreamUrl))
            {
                var isValid = await TestRtspStreamAsync(cam.StreamUrl);
                _logger.LogInformation("Cámara {Id} - Stream válido: {IsValid}", cam.Id, isValid);
            }
        }

        _logger.LogInformation("=== FIN DIAGNÓSTICO ===");
    }

    private async Task<bool> TestRtspStreamAsync(string rtspUrl)
    {
        try
        {
            using var source = new FFmpegFileSource(rtspUrl, false, null!, 0, true);
            var formats = source.GetVideoSourceFormats();
            return formats != null && formats.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
