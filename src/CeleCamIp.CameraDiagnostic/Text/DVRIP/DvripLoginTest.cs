using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.DVRIP;

/// <summary>
/// Primer chequeo para camaras que no hablan RTSP (o que ademas exponen su
/// protocolo propietario, puerto 34567): confirma que el dispositivo
/// entiende DVRIP y que las credenciales son validas. Si el login funciona,
/// deja la sesion abierta y da el SessionID que despues usan
/// <see cref="DvripConfigDiscoveryTest"/> y las pruebas de PTZ.
/// </summary>
public class DvripLoginTest : IDiagnosticTest
{
    public string Nombre => "DVRIP - Login";
    public string Descripcion => "Confirma que la camara habla DVRIP (puerto 34567) y valida usuario/contraseña.";

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        using var cliente = new DvripClient();

        try
        {
            await cliente.ConectarAsync(camara.IpAddress, ct);
        }
        catch (Exception ex)
        {
            return DiagnosticoResultado.Fallo(
                "No se pudo conectar al puerto 34567 (DVRIP). Probablemente la camara no usa este protocolo.",
                ex.Message);
        }

        var (ok, cuerpo) = await cliente.LoginAsync(camara.Username ?? string.Empty, camara.Password ?? string.Empty, ct);

        if (!ok)
        {
            // Si la camara no devolvio JSON valido, UltimoMotivoSinCuerpo trae
            // el detalle (timeout, cuerpo vacio, o el texto crudo recibido).
            var detalle = cuerpo?.ToString() ?? cliente.UltimoMotivoSinCuerpo ?? "(sin informacion adicional)";
            return DiagnosticoResultado.Fallo(
                "La camara respondio DVRIP pero el login fallo (usuario/contraseña incorrectos, o formato de hash distinto en este firmware).",
                detalle);
        }

        return DiagnosticoResultado.Ok(
            $"Login DVRIP exitoso. SessionID: 0x{cliente.SessionId:x8}",
            new Dictionary<string, string>
            {
                ["SessionID"] = $"0x{cliente.SessionId:x8}",
                ["RespuestaLogin"] = cuerpo?.ToString() ?? string.Empty,
            });
    }
}
