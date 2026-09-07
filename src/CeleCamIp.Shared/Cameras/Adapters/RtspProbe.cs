using System.Net.Sockets;
using System.Text;

namespace CeleCamIp.Shared.Cameras.Adapters;

/// <summary>
/// Cliente RTSP minimo (sin dependencias externas) que solo implementa lo
/// necesario para detectar si una camara responde RTSP: conexion TCP y el
/// comando DESCRIBE, que sirve tanto para confirmar el protocolo como para
/// leer el SDP (por ejemplo, para saber si la camara tiene pista de audio).
///
/// No implementa PLAY/streaming real: eso lo hace el proceso de video
/// (FFmpeg u otro relay) mas adelante. Este cliente es solo para el
/// handshake de deteccion/negociacion inicial.
///
/// Nota: solo soporta Basic Auth (RFC 2326). Muchas camaras exigen Digest;
/// eso queda pendiente como mejora si aparecen camaras que lo requieran.
/// </summary>
internal static class RtspProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    public static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var completed = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout, ct));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool Ok, string? Sdp)> DescribeAsync(string rtspUrl, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(rtspUrl);
            var port = uri.Port > 0 ? uri.Port : 554;

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(uri.Host, port, ct).AsTask();
            var connected = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout, ct));
            if (connected != connectTask || !client.Connected)
            {
                return (false, null);
            }

            using var stream = client.GetStream();

            var authHeader = BuildBasicAuthHeader(uri);
            var request =
                $"DESCRIBE {rtspUrl} RTSP/1.0\r\n" +
                "CSeq: 1\r\n" +
                "Accept: application/sdp\r\n" +
                (authHeader is null ? string.Empty : $"{authHeader}\r\n") +
                "\r\n";

            var requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, ct);

            var response = await ReadResponseAsync(stream, ct);
            if (response is null)
            {
                return (false, null);
            }

            var statusLine = response.Split("\r\n", 2)[0];
            // 200 = describe exitoso. 401 = pide auth, pero confirma que hay
            // un servidor RTSP real detras (util para el CanHandleAsync).
            var ok = statusLine.Contains("200") || statusLine.Contains("401");

            var sdpIndex = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var sdp = sdpIndex >= 0 ? response[(sdpIndex + 4)..] : null;

            return (ok, sdp);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string? BuildBasicAuthHeader(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo));
        return $"Authorization: Basic {Convert.ToBase64String(bytes)}";
    }

    private static async Task<string?> ReadResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        var readTask = stream.ReadAsync(buffer, ct).AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(ReadTimeout, ct));
        if (completed != readTask)
        {
            return null;
        }

        var read = readTask.Result;
        if (read <= 0)
        {
            return null;
        }

        ms.Write(buffer, 0, read);

        while (stream.DataAvailable)
        {
            var extra = await stream.ReadAsync(buffer, ct);
            if (extra <= 0) break;
            ms.Write(buffer, 0, extra);
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
