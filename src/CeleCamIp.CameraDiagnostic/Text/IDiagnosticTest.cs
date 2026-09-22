using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text;

/// <summary>
/// Contrato comun para toda prueba de diagnostico (RTSP, DVRIP, PTZ).
/// Cada prueba es independiente, se puede correr sola desde un menu de
/// consola, y devuelve un resultado uniforme (<see cref="DiagnosticoResultado"/>)
/// con lo que se pudo averiguar de la camara.
/// </summary>
public interface IDiagnosticTest
{
    /// <summary>Nombre corto para mostrar en el menu / logs.</summary>
    string Nombre { get; }

    /// <summary>Que hace la prueba y que informacion devuelve.</summary>
    string Descripcion { get; }

    Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct);
}
