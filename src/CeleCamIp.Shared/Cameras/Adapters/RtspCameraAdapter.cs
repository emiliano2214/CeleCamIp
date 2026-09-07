namespace CeleCamIp.Shared.Cameras.Adapters;

/// <summary>
/// Adaptador para camaras RTSP "genericas" (sin ONVIF). Prueba primero si la
/// StreamUrl ya conocida responde y, si no, intenta un conjunto de rutas
/// comunes usadas por las marcas mas habituales (Hikvision, Dahua, TP-Link,
/// DVRs genericos, etc) hasta encontrar una que responda.
///
/// Va DESPUES de Onvif en el orden de prioridad de CameraDetectionService:
/// Onvif es mas confiable cuando esta disponible porque la propia camara
/// informa su stream real; esto es el fallback para camaras sin ONVIF.
/// </summary>
public class RtspCameraAdapter : ICameraAdapter
{
    public string Name => "Rtsp";

    private const int DefaultRtspPort = 554;

    // Rutas RTSP mas comunes entre fabricantes cuando no se conoce el modelo exacto.
    private static readonly string[] CommonPaths =
    {
        "/stream1",
        "/h264/ch1/main/av_stream",
        "/cam/realmonitor?channel=1&subtype=0",
        "/videoMain",
        "/live/ch0",
        "/11",
        "/",
    };

    public async Task<bool> CanHandleAsync(CameraDescriptor camera, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(camera.IpAddress))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(camera.StreamUrl) &&
            camera.StreamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return await RtspProbe.CanConnectAsync(camera.IpAddress, GetPort(camera.StreamUrl), ct);
        }

        return await RtspProbe.CanConnectAsync(camera.IpAddress, DefaultRtspPort, ct);
    }

    public async Task<CameraConnection> ConnectAsync(CameraDescriptor camera, CancellationToken ct = default)
    {
        var candidateUrl = BuildCandidateUrl(camera, camera.StreamUrl);
        if (candidateUrl is not null)
        {
            var (ok, sdp) = await RtspProbe.DescribeAsync(candidateUrl, ct);
            if (ok)
            {
                return BuildResult(camera, candidateUrl, sdp);
            }
        }

        foreach (var path in CommonPaths)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildCandidateUrl(camera, path);
            if (url is null)
            {
                continue;
            }

            var (ok, sdp) = await RtspProbe.DescribeAsync(url, ct);
            if (ok)
            {
                return BuildResult(camera, url, sdp);
            }
        }

        throw new InvalidOperationException(
            $"No se encontro una ruta RTSP valida para la camara '{camera.Id}' ({camera.IpAddress}). " +
            "Cargue la StreamUrl manualmente si el fabricante usa una ruta no estandar.");
    }

    private static CameraConnection BuildResult(CameraDescriptor camera, string url, string? sdp)
    {
        return new CameraConnection
        {
            CameraId = camera.Id,
            NormalizedStreamUrl = url,
            SupportsAudio = sdp is not null && sdp.Contains("m=audio", StringComparison.OrdinalIgnoreCase),
            SupportsPtz = false,
        };
    }

    private static string? BuildCandidateUrl(CameraDescriptor camera, string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(camera.IpAddress))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(pathOrUrl) && pathOrUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return InjectCredentials(pathOrUrl, camera.Username, camera.Password);
        }

        var path = string.IsNullOrWhiteSpace(pathOrUrl) ? "/" : pathOrUrl;
        var auth = BuildAuthPrefix(camera.Username, camera.Password);
        return $"rtsp://{auth}{camera.IpAddress}:{DefaultRtspPort}{path}";
    }

    private static string BuildAuthPrefix(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(password)
            ? $"{Uri.EscapeDataString(username)}@"
            : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@";
    }

    private static string InjectCredentials(string url, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return url;
        }

        var uri = new Uri(url);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return url;
        }

        var auth = BuildAuthPrefix(username, password);
        return $"rtsp://{auth}{uri.Authority}{uri.PathAndQuery}";
    }

    private static int GetPort(string rtspUrl)
    {
        try
        {
            var uri = new Uri(rtspUrl);
            return uri.Port > 0 ? uri.Port : DefaultRtspPort;
        }
        catch
        {
            return DefaultRtspPort;
        }
    }
}
