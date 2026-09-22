using System.Net.Sockets;
using System.Text;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.RTSP;

/// <summary>
/// Pregunta a la camara, via RTSP OPTIONS, que metodos soporta (DESCRIBE,
/// PLAY, PAUSE, SET_PARAMETER, GET_PARAMETER...). Sirve como primer chequeo
/// rapido: confirma que hay un servidor RTSP real respondiendo en el puerto
/// 554 antes de intentar DESCRIBE (<see cref="RtspConfigDiscoveryTest"/>).
///
/// No reutiliza CeleCamIp.Shared.Cameras.Adapters.RtspProbe porque esa clase
/// es 'internal' al proyecto Shared (no visible desde este ensamblado) y
/// solo implementa DESCRIBE, no OPTIONS. Esta prueba tiene su propio cliente
/// RTSP minimo, autocontenido.
/// </summary>
public class RtspOptionsTest : IDiagnosticTest
{
    public string Nombre => "RTSP - OPTIONS";
    public string Descripcion => "Consulta los metodos RTSP soportados por la camara (comando OPTIONS).";

    private const int PuertoPorDefecto = 554;
    private static readonly TimeSpan TimeoutConexion = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TimeoutLectura = TimeSpan.FromSeconds(3);

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        try
        {
            using var cliente = new TcpClient();
            var tareaConexion = cliente.ConnectAsync(camara.IpAddress, PuertoPorDefecto, ct).AsTask();
            var completado = await Task.WhenAny(tareaConexion, Task.Delay(TimeoutConexion, ct));

            if (completado != tareaConexion || !cliente.Connected)
            {
                return DiagnosticoResultado.Fallo($"No se pudo conectar a {camara.IpAddress}:{PuertoPorDefecto} (RTSP).");
            }

            var url = $"rtsp://{camara.IpAddress}:{PuertoPorDefecto}/";
            var pedido =
                $"OPTIONS {url} RTSP/1.0\r\n" +
                "CSeq: 1\r\n" +
                "\r\n";

            using var stream = cliente.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(pedido), ct);

            var respuesta = await LeerRespuestaAsync(stream, ct);
            if (respuesta is null)
            {
                return DiagnosticoResultado.Fallo("La camara no respondio a OPTIONS dentro del tiempo de espera.");
            }

            var lineaPublic = respuesta
                .Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Public:", StringComparison.OrdinalIgnoreCase));

            var metodos = lineaPublic?["Public:".Length..].Trim() ?? "(la camara no informo el header 'Public')";

            return DiagnosticoResultado.Ok(
                $"La camara respondio OPTIONS. Metodos soportados: {metodos}",
                new Dictionary<string, string>
                {
                    ["Metodos"] = metodos,
                    ["RespuestaCruda"] = respuesta.TrimEnd(),
                });
        }
        catch (Exception ex)
        {
            return DiagnosticoResultado.Fallo("Error al consultar OPTIONS.", ex.Message);
        }
    }

    private static async Task<string?> LeerRespuestaAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var tareaLectura = stream.ReadAsync(buffer, ct).AsTask();
        var listo = await Task.WhenAny(tareaLectura, Task.Delay(TimeoutLectura, ct));

        if (listo != tareaLectura)
        {
            return null;
        }

        var leidos = tareaLectura.Result;
        return leidos <= 0 ? null : Encoding.ASCII.GetString(buffer, 0, leidos);
    }
}
