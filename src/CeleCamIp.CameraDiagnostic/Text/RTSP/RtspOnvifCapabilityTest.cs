using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.RTSP;

/// <summary>
/// Si la camara habla ONVIF (servicio SOAP en /onvif/device_service, puerto
/// 80 por defecto), esta prueba consulta los perfiles de medios reales que
/// expone. Es la fuente MAS confiable de "que configuraciones son posibles":
/// a diferencia del SDP de RTSP puro (<see cref="RtspConfigDiscoveryTest"/>),
/// ONVIF permite ademas leer y modificar resolucion/framerate/bitrate por
/// perfil (GetVideoEncoderConfigurationOptions / SetVideoEncoderConfiguration),
/// que queda como siguiente paso una vez confirmado que hay perfiles.
///
/// LIMITACION CONOCIDA: no implementa WS-Security (UsernameToken con
/// nonce+digest). Si la camara exige autenticacion en el SOAP, esta prueba
/// va a reportarlo como fallo con el fragmento de respuesta del server
/// (normalmente un SOAP Fault "NotAuthorized"). Queda pendiente como mejora
/// si aparecen camaras que lo requieran para operaciones de lectura.
/// </summary>
public class RtspOnvifCapabilityTest : IDiagnosticTest
{
    public string Nombre => "RTSP - Capacidades ONVIF";
    public string Descripcion => "Si la camara soporta ONVIF, lista los perfiles de video disponibles.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string SobreGetProfiles =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <GetProfiles xmlns="http://www.onvif.org/ver10/media/wsdl" />
          </s:Body>
        </s:Envelope>
        """;

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        var url = $"http://{camara.IpAddress}/onvif/device_service";

        try
        {
            var respuesta = await PostSoapAsync(url, SobreGetProfiles, ct);
            if (respuesta is null)
            {
                return DiagnosticoResultado.Fallo(
                    "La camara no respondio en /onvif/device_service " +
                    "(probablemente no soporta ONVIF, o usa otro puerto/ruta).");
            }

            if (respuesta.Contains("Fault", StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticoResultado.Fallo(
                    "ONVIF respondio con un SOAP Fault (posiblemente pide WS-Security, no implementado).",
                    respuesta.Length > 500 ? respuesta[..500] : respuesta);
            }

            var nombresPerfiles = Regex.Matches(respuesta, "<(?:trt:)?Name>([^<]+)</(?:trt:)?Name>")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            if (nombresPerfiles.Count == 0)
            {
                return DiagnosticoResultado.Fallo(
                    "La camara respondio pero no se encontraron perfiles en el XML.",
                    respuesta.Length > 500 ? respuesta[..500] : respuesta);
            }

            return DiagnosticoResultado.Ok(
                $"ONVIF respondio. Perfiles encontrados: {string.Join(", ", nombresPerfiles)}",
                new Dictionary<string, string> { ["Perfiles"] = string.Join(", ", nombresPerfiles) });
        }
        catch (Exception ex)
        {
            return DiagnosticoResultado.Fallo("Error consultando ONVIF.", ex.Message);
        }
    }

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
