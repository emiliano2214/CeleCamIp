namespace CeleCamIp.Shared.Cameras;

/// <summary>
/// Resultado de una conexion exitosa a una camara: la URL/canal ya
/// normalizado que el resto del sistema (WebRTC Gateway) puede consumir
/// sin saber que protocolo hay detras.
/// </summary>
public record CameraConnection
{
    public required string CameraId { get; init; }
    public required string NormalizedStreamUrl { get; init; }
    public bool SupportsAudio { get; init; }
    public bool SupportsPtz { get; init; }
}
