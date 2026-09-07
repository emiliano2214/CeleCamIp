using CeleCamIp.Server.Services;
using CeleCamIp.Shared.Cameras;
using CeleCamIp.Shared.WebRtc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CeleCamIp.Server.Hubs;

/// <summary>
/// Hub compartido por dos tipos de clientes, distinguidos por que metodos llaman:
///
/// - Gateway (casa): conexion SALIENTE desde la casa hacia el VPS. Llama
///   RegisterHouse al conectar y ReportCameras despues de correr la deteccion
///   local. Esto es lo que permite evitar abrir puertos y funcionar detras
///   de CGNAT.
/// - App movil (viewer): llama JoinAsViewer para sumarse al grupo de
///   notificaciones y recibir el snapshot actual de casas/camaras, y despues
///   recibe en tiempo real HouseOnline / HouseOffline / CamerasUpdated.
///
/// Ademas, el Hub actua de relay "ciego" de la senializacion WebRTC (SDP + ICE)
/// entre un viewer puntual y el Gateway de la casa correspondiente. El Server
/// no entiende ni valida el contenido SDP: solo enruta mensajes por ConnectionId,
/// igual que un servidor de senializacion clasico de WebRTC.
/// </summary>
[Authorize(AuthenticationSchemes = "ApiKey")]
public class GatewayHub : Hub
{
    private const string ViewersGroup = "viewers";

    private readonly IHouseRegistry _registry;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(IHouseRegistry registry, ILogger<GatewayHub> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Cliente conectado. ConnectionId: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var houseId = _registry.MarkOfflineByConnection(Context.ConnectionId);
        if (houseId is not null)
        {
            _logger.LogInformation("Casa desconectada: {HouseId}", houseId);
            await Clients.Group(ViewersGroup).SendAsync("HouseOffline", houseId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Lo llama el Gateway de cada casa al conectarse, para identificarse.</summary>
    public async Task RegisterHouse(string houseId)
    {
        _registry.MarkOnline(houseId, Context.ConnectionId);
        _logger.LogInformation("Casa registrada: {HouseId} -> {ConnectionId}", houseId, Context.ConnectionId);

        await Clients.Caller.SendAsync("Registered", houseId);
        await Clients.Group(ViewersGroup).SendAsync("HouseOnline", houseId);
    }

    /// <summary>Lo llama el Gateway despues de correr CameraDetectionService localmente.</summary>
    public async Task ReportCameras(string houseId, List<CameraDescriptor> cameras)
    {
        _registry.SetCameras(houseId, cameras);
        _logger.LogInformation("Casa {HouseId} reporto {Count} camara(s)", houseId, cameras.Count);

        await Clients.Group(ViewersGroup).SendAsync("CamerasUpdated", houseId, cameras);
    }

    /// <summary>
    /// Lo llama la app movil al conectarse. Se suma al grupo de notificaciones
    /// y recibe de una el estado actual de todas las casas conocidas, para no
    /// tener que esperar al proximo evento para pintar la pantalla.
    /// </summary>
    public async Task<List<HouseSnapshot>> JoinAsViewer()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ViewersGroup);
        _logger.LogInformation("Viewer conectado. ConnectionId: {ConnectionId}", Context.ConnectionId);
        return _registry.GetAll();
    }

    // ===================== Senializacion WebRTC (SDP/ICE) =====================
    //
    // El Gateway es siempre el "offerer" (tiene el video de la camara); el viewer
    // (app movil) es siempre el "answerer". El flujo es:
    //   1. viewer -> RequestStream(houseId, cameraId)
    //   2. Server -> Gateway: "StreamRequested"(viewerConnectionId, cameraId)
    //   3. Gateway arma RTCPeerConnection + oferta -> SendOffer(viewerConnectionId, cameraId, offer)
    //   4. Server -> viewer: "ReceiveOffer"(cameraId, offer)
    //   5. viewer arma su RTCPeerConnection, responde -> SendAnswer(houseId, cameraId, answer)
    //   6. Server -> Gateway: "ReceiveAnswer"(viewerConnectionId, cameraId, answer)
    //   7. Ambos lados van intercambiando candidatos ICE con SendIceCandidateTo* mientras dure la conexion.

    /// <summary>
    /// Lo llama la app movil (viewer) para pedir ver una camara puntual. Se
    /// relaya al Gateway de la casa correspondiente, que es quien arma la
    /// oferta WebRTC.
    /// </summary>
    public async Task RequestStream(string houseId, string cameraId)
    {
        var gatewayConnectionId = _registry.GetConnectionId(houseId);
        if (gatewayConnectionId is null)
        {
            _logger.LogWarning("RequestStream para casa {HouseId} pero no esta conectada.", houseId);
            await Clients.Caller.SendAsync("StreamError", cameraId, "La casa no esta conectada.");
            return;
        }

        await Clients.Client(gatewayConnectionId).SendAsync("StreamRequested", Context.ConnectionId, cameraId);
    }

    /// <summary>Lo llama el Gateway con la oferta SDP que armo para un viewer puntual.</summary>
    public async Task SendOffer(string viewerConnectionId, string cameraId, SdpDescriptionDto offer)
    {
        await Clients.Client(viewerConnectionId).SendAsync("ReceiveOffer", cameraId, offer);
    }

    /// <summary>Lo llama el viewer con la respuesta SDP a la oferta del Gateway.</summary>
    public async Task SendAnswer(string houseId, string cameraId, SdpDescriptionDto answer)
    {
        var gatewayConnectionId = _registry.GetConnectionId(houseId);
        if (gatewayConnectionId is null)
        {
            _logger.LogWarning("SendAnswer para casa {HouseId} pero ya no esta conectada.", houseId);
            return;
        }

        await Clients.Client(gatewayConnectionId).SendAsync("ReceiveAnswer", Context.ConnectionId, cameraId, answer);
    }

    /// <summary>Trickle ICE: el Gateway manda un candidato propio hacia un viewer puntual.</summary>
    public async Task SendIceCandidateToViewer(string viewerConnectionId, string cameraId, IceCandidateDto candidate)
    {
        await Clients.Client(viewerConnectionId).SendAsync("ReceiveIceCandidate", cameraId, candidate);
    }

    /// <summary>Trickle ICE: el viewer manda un candidato propio hacia el Gateway de una casa.</summary>
    public async Task SendIceCandidateToGateway(string houseId, string cameraId, IceCandidateDto candidate)
    {
        var gatewayConnectionId = _registry.GetConnectionId(houseId);
        if (gatewayConnectionId is null)
        {
            return;
        }

        await Clients.Client(gatewayConnectionId).SendAsync("ReceiveIceCandidate", Context.ConnectionId, cameraId, candidate);
    }

    /// <summary>Lo llama el Gateway si no pudo armar la sesion para una camara (ej: URL invalida, camara caida).</summary>
    public async Task ReportStreamError(string viewerConnectionId, string cameraId, string reason)
    {
        await Clients.Client(viewerConnectionId).SendAsync("StreamError", cameraId, reason);
    }
}
