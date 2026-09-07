namespace CeleCamIp.Shared.Cameras;

/// <summary>
/// Contrato que implementa cada estrategia de conexion a camara
/// (ONVIF, RTSP directo, protocolo propietario, etc).
///
/// El CameraDetectionService los recorre en orden de prioridad:
/// Onvif -> Rtsp -> adaptadores propietarios. El primero que
/// pueda manejar la camara gana, y el resultado se persiste en el
/// CameraDescriptor (AdapterUsed) para no re-detectar en cada conexion.
/// </summary>
public interface ICameraAdapter
{
    /// <summary>Nombre unico del adaptador (ej. "Onvif", "Rtsp", "Icsee").</summary>
    string Name { get; }

    /// <summary>Determina si este adaptador puede manejar la camara dada. No debe lanzar excepciones.</summary>
    Task<bool> CanHandleAsync(CameraDescriptor camera, CancellationToken ct = default);

    /// <summary>Establece la conexion real y devuelve el stream normalizado.</summary>
    Task<CameraConnection> ConnectAsync(CameraDescriptor camera, CancellationToken ct = default);
}
