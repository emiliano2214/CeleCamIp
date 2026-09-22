using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.DVRIP;

/// <summary>
/// Con sesion DVRIP abierta (login previo), recorre un set de secciones de
/// configuracion conocidas y reporta cuales existen en este dispositivo y
/// que contienen. A diferencia de RTSP puro, DVRIP si tiene un verdadero
/// sistema de configuracion consultable por comando (CONFIG_GET, msgid
/// 1042): cada seccion trae los valores actuales y, en varios firmwares,
/// tambien el rango/opciones permitidas — que es justo lo que hace falta
/// para poder programarlas despues en vez de adivinar.
///
/// "General" y "NetWork.NetCommon" son las secciones que devolvieron el
/// error "sin conexion con el servidor" al abrirlas desde la app de la
/// camara, asi que son las primeras candidatas a revisar con esta prueba.
/// </summary>
public class DvripConfigDiscoveryTest : IDiagnosticTest
{
    public string Nombre => "DVRIP - Descubrimiento de configuracion";
    public string Descripcion => "Pide (CONFIG_GET) las secciones de configuracion mas comunes y reporta cuales responden.";

    private static readonly string[] SeccionesComunes =
    {
        "General",
        "NetWork.NetCommon",
        "Camera",
        "Encode",
        "Simplify.Encode",
        "Ptz",
    };

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        using var cliente = new DvripClient();

        try
        {
            await cliente.ConectarAsync(camara.IpAddress, ct);
        }
        catch (Exception ex)
        {
            return DiagnosticoResultado.Fallo("No se pudo conectar al puerto 34567 (DVRIP).", ex.Message);
        }

        var (loginOk, _) = await cliente.LoginAsync(camara.Username ?? string.Empty, camara.Password ?? string.Empty, ct);
        if (!loginOk)
        {
            return DiagnosticoResultado.Fallo("Login DVRIP fallido: no se puede consultar configuracion sin sesion.");
        }

        var datos = new Dictionary<string, string>();
        var encontradas = new List<string>();

        foreach (var seccion in SeccionesComunes)
        {
            ct.ThrowIfCancellationRequested();

            var cuerpo = await cliente.ObtenerConfigAsync(seccion, ct);
            if (cuerpo is null)
            {
                datos[seccion] = "(sin respuesta)";
                continue;
            }

            var texto = cuerpo.Value.ToString();

            // Ret 203/206 suele significar "seccion no existe" en este protocolo;
            // igual guardamos la respuesta cruda para inspeccionarla a mano.
            var noExiste = cuerpo.Value.TryGetProperty("Ret", out var ret) && ret.GetInt32() is 203 or 206;

            datos[seccion] = texto;
            if (!noExiste)
            {
                encontradas.Add(seccion);
            }
        }

        return encontradas.Count > 0
            ? DiagnosticoResultado.Ok($"Secciones de configuracion encontradas: {string.Join(", ", encontradas)}", datos)
            : DiagnosticoResultado.Fallo("Ninguna de las secciones probadas respondio con datos utiles.", string.Join("\n", datos.Select(kv => $"{kv.Key}: {kv.Value}")));
    }
}
