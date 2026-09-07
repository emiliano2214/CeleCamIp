namespace CeleCamIp.Shared.Cameras;

/// <summary>
/// Representa el resultado de la deteccion/configuracion de una camara.
/// Se detecta una sola vez (al agregarla) y se persiste; no se vuelve
/// a re-detectar el protocolo en cada conexion.
/// </summary>
public record CameraDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string IpAddress { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }

    public bool OnvifSupported { get; init; }
    public bool RtspSupported { get; init; }

    public string? StreamUrl { get; init; }

    /// <summary>Nombre del adaptador que finalmente logro conectarse (ej. "Onvif", "Rtsp", "Icsee").</summary>
    public string? AdapterUsed { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
}
