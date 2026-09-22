using System.Net.Sockets;
using System.Text;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.RTSP;

/// <summary>
/// Pregunta a la camara, via RTSP DESCRIBE, el SDP del stream: que pistas
/// tiene (video/audio), que codec usa cada una y, si el fabricante lo
/// informa, atributos como fmtp (perfil/resolucion/framerate segun codec).
///
/// Esto es lo mas cercano a "configuracion disponible" que expone RTSP puro
/// sin ONVIF: no permite cambiar la config, pero confirma que codec/formato
/// hay que esperar para poder programar el resto del pipeline (FFmpeg,
/// WebRTC, etc). Para configuracion real y modificable, ver
/// <see cref="RtspOnvifCapabilityTest"/> (si la camara soporta ONVIF).
///
/// Prueba las rutas RTSP mas comunes por fabricante porque RTSP no tiene
/// forma estandar de "listar" rutas disponibles.
/// </summary>
public class RtspConfigDiscoveryTest : IDiagnosticTest
{
    public string Nombre => "RTSP - Descubrimiento de SDP";
    public string Descripcion => "Obtiene el SDP (DESCRIBE) y lista pistas, codecs y atributos por pista.";

    private const int PuertoPorDefecto = 554;
    private static readonly TimeSpan TimeoutConexion = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TimeoutLectura = TimeSpan.FromSeconds(3);

    private static readonly string[] RutasComunes =
    {
        "/stream1",
        "/h264/ch1/main/av_stream",
        "/cam/realmonitor?channel=1&subtype=0",
        "/videoMain",
        "/live/ch0",
        "/11",
        // Formato usado por DVR/NVR genericos chinos con firmware Xiongmai
        // (app iCSee), identificables por el header "Server: H264DVR x.x"
        // en la respuesta de OPTIONS: las credenciales van en el path, no
        // como userinfo. Ver ConstruirUrl.
        "/user={USER}&password={PASS}&channel=1&stream=0.sdp",
        "/onvif1",
        "/h264?channel=1",
        "/camera",
        "/",
    };

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        foreach (var ruta in RutasComunes)
        {
            ct.ThrowIfCancellationRequested();

            var url = ConstruirUrl(camara, ruta);
            var (ok, sdp) = await DescribeAsync(url, ct);

            if (ok && !string.IsNullOrWhiteSpace(sdp))
            {
                var datos = ParsearSdp(sdp);
                datos["RutaEncontrada"] = ruta;
                datos["UrlCompleta"] = url;

                return DiagnosticoResultado.Ok(
                    $"SDP obtenido en '{ruta}'. Pistas: {datos.GetValueOrDefault("Pistas", "?")}",
                    datos);
            }
        }

        return DiagnosticoResultado.Fallo(
            "Ninguna de las rutas RTSP comunes respondio DESCRIBE. " +
            "Si se conoce la ruta exacta del fabricante, probarla manualmente con VLC primero.");
    }

    private static string ConstruirUrl(CameraDescriptor camara, string ruta)
    {
        // Formato con credenciales embebidas en el path (ej. iCSee/H264DVR):
        // no llevan userinfo, van directo en la query string de la ruta.
        if (ruta.Contains("{USER}", StringComparison.Ordinal))
        {
            var rutaConCredenciales = ruta
                .Replace("{USER}", Uri.EscapeDataString(camara.Username ?? string.Empty))
                .Replace("{PASS}", Uri.EscapeDataString(camara.Password ?? string.Empty));

            return $"rtsp://{camara.IpAddress}:{PuertoPorDefecto}{rutaConCredenciales}";
        }

        var auth = string.IsNullOrWhiteSpace(camara.Username)
            ? string.Empty
            : string.IsNullOrWhiteSpace(camara.Password)
                ? $"{Uri.EscapeDataString(camara.Username)}@"
                : $"{Uri.EscapeDataString(camara.Username)}:{Uri.EscapeDataString(camara.Password)}@";

        return $"rtsp://{auth}{camara.IpAddress}:{PuertoPorDefecto}{ruta}";
    }

    private static async Task<(bool Ok, string? Sdp)> DescribeAsync(string url, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            using var cliente = new TcpClient();
            var tareaConexion = cliente.ConnectAsync(uri.Host, uri.Port, ct).AsTask();
            var completado = await Task.WhenAny(tareaConexion, Task.Delay(TimeoutConexion, ct));

            if (completado != tareaConexion || !cliente.Connected)
            {
                return (false, null);
            }

            using var stream = cliente.GetStream();

            var authHeader = string.IsNullOrEmpty(uri.UserInfo)
                ? string.Empty
                : $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)))}\r\n";

            var pedido =
                $"DESCRIBE {url} RTSP/1.0\r\n" +
                "CSeq: 1\r\n" +
                "Accept: application/sdp\r\n" +
                authHeader +
                "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(pedido), ct);

            var buffer = new byte[8192];
            var tareaLectura = stream.ReadAsync(buffer, ct).AsTask();
            var listo = await Task.WhenAny(tareaLectura, Task.Delay(TimeoutLectura, ct));

            if (listo != tareaLectura)
            {
                return (false, null);
            }

            var leidos = tareaLectura.Result;
            if (leidos <= 0)
            {
                return (false, null);
            }

            var respuesta = Encoding.ASCII.GetString(buffer, 0, leidos);
            // 200 = describe exitoso. 401 confirma que hay servidor RTSP real
            // pero pide autenticacion (util para diagnosticar credenciales).
            var ok = respuesta.Contains("200", StringComparison.Ordinal);

            var idx = respuesta.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var sdp = idx >= 0 ? respuesta[(idx + 4)..] : null;

            return (ok, sdp);
        }
        catch
        {
            return (false, null);
        }
    }

    private static Dictionary<string, string> ParsearSdp(string sdp)
    {
        var datos = new Dictionary<string, string>();
        var lineas = sdp.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var pistas = new List<string>();
        string? pistaActual = null;

        foreach (var linea in lineas)
        {
            if (linea.StartsWith("m=", StringComparison.Ordinal))
            {
                // Ej: "m=video 0 RTP/AVP 96"
                pistaActual = linea[2..].Split(' ')[0]; // "video" o "audio"
                pistas.Add(pistaActual);
            }
            else if (linea.StartsWith("a=rtpmap:", StringComparison.Ordinal) && pistaActual is not null)
            {
                // Ej: "a=rtpmap:96 H264/90000" -> codec + clock rate
                datos[$"Codec_{pistaActual}"] = linea["a=rtpmap:".Length..];
            }
            else if (linea.StartsWith("a=fmtp:", StringComparison.Ordinal) && pistaActual is not null)
            {
                // Parametros especificos del codec (perfil H264, SPS/PPS, etc)
                datos[$"Fmtp_{pistaActual}"] = linea["a=fmtp:".Length..];
            }
            else if (linea.StartsWith("a=control:", StringComparison.Ordinal) && pistaActual is not null)
            {
                datos[$"Control_{pistaActual}"] = linea["a=control:".Length..];
            }
        }

        datos["Pistas"] = pistas.Count == 0 ? "(ninguna)" : string.Join(", ", pistas);
        return datos;
    }
}
