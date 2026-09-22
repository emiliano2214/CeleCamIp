using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.PTZ;

/// <summary>
/// Si la camara soporta PTZ via ONVIF, esta prueba NO mueve la camara: solo
/// pregunta (GetPresets) que presets ya tiene guardados, que es la
/// informacion que hace falta para programar los comandos preestablecidos
/// en la app despues. Mover la camara de verdad durante un diagnostico
/// automatico es un efecto secundario no deseado (puede desencuadrar la
/// toma), asi que se deja fuera de esta prueba a proposito.
///
/// Igual que <see cref="CeleCamIp.CameraDiagnostic.Text.RTSP.RtspOnvifCapabilityTest"/>,
/// no implementa WS-Security: si el servicio PTZ exige autenticacion SOAP,
/// esto va a reportar el Fault en vez de los datos.
/// </summary>
public class OnvifPtzTest : IDiagnosticTest
{
    public string Nombre => "PTZ - Capacidades ONVIF";
    public string Descripcion => "Si la camara tiene PTZ via ONVIF, lista presets guardados (sin mover la camara).";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Distintos fabricantes exponen el servicio PTZ en rutas distintas;
    // se prueban las mas comunes en orden.
    private static readonly string[] RutasServicioPtz = { "/onvif/ptz_service", "/onvif/device_service" };

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        var intentos = new Dictionary<string, string>();

        foreach (var ruta in RutasServicioPtz)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"http://{camara.IpAddress}{ruta}";
            var respuestaPresets = await PostSoapAsync(url, SobreGetPresets, ct);

            if (respuestaPresets is null)
            {
                intentos[ruta] = "(sin respuesta / conexion rechazada)";
                continue;
            }

            // Muchos dispositivos sin ONVIF real devuelven un 404 HTML (u
            // otra pagina de error) en vez de rechazar la conexion. Eso NO
            // es una respuesta SOAP valida: si se lo tratara como "0
            // presets encontrados" daria un falso positivo de "tiene PTZ
            // ONVIF". Se exige que la respuesta sea al menos un sobre SOAP.
            var pareceSoap = respuestaPresets.Contains("Envelope", StringComparison.OrdinalIgnoreCase);

            if (!pareceSoap)
            {
                intentos[ruta] = respuestaPresets.Length > 200
                    ? $"Respuesta no-SOAP (probable 404/error): {respuestaPresets[..200]}..."
                    : $"Respuesta no-SOAP (probable 404/error): {respuestaPresets}";
                continue;
            }

            if (respuestaPresets.Contains("Fault", StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticoResultado.Fallo(
                    $"El servicio PTZ en '{ruta}' respondio con un SOAP Fault (puede pedir WS-Security, o no tener PTZ).",
                    respuestaPresets.Length > 500 ? respuestaPresets[..500] : respuestaPresets);
            }

            var presets = Regex.Matches(respuestaPresets, "<(?:tt:)?Name>([^<]+)</(?:tt:)?Name>")
                .Select(m => m.Groups[1].Value)
                .ToList();

            var datos = new Dictionary<string, string>
            {
                ["ServicioEncontradoEn"] = ruta,
                ["Presets"] = presets.Count == 0 ? "(ninguno guardado)" : string.Join(", ", presets),
            };

            return DiagnosticoResultado.Ok(
                presets.Count == 0
                    ? $"Servicio PTZ ONVIF encontrado en '{ruta}', sin presets guardados todavia."
                    : $"Servicio PTZ ONVIF encontrado en '{ruta}'. Presets: {string.Join(", ", presets)}",
                datos);
        }

        return DiagnosticoResultado.Fallo(
            "No se encontro un servicio PTZ ONVIF valido en las rutas probadas " +
            "(las respuestas no eran SOAP, o no hubo respuesta). " +
            "Si la camara tiene PTZ, probablemente lo controla por DVRIP (ver DvripPtzTest).",
            string.Join("\n", intentos.Select(kv => $"{kv.Key}: {kv.Value}")));
    }

    private const string SobreGetPresets =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <GetPresets xmlns="http://www.onvif.org/ver20/ptz/wsdl">
              <ProfileToken>Profile_1</ProfileToken>
            </GetPresets>
          </s:Body>
        </s:Envelope>
        """;

    private static async Task<string?> PostSoapAsync(string url, string sobreXml, CancellationToken ct)
    {
        try
        {
            using var contenido = new StringContent(sobreXml, Encoding.UTF8);
            contenido.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };

            using var respuesta = await Http.PostAsync(url, contenido, ct);
            return await respuesta.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }
}
