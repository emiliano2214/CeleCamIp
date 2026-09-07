using System.Diagnostics;

namespace CeleCamIp.App.Services;

internal static class VlcLauncher
{
    private static readonly string[] WindowsVlcPaths =
    {
        @"C:\Program Files\VideoLAN\VLC\vlc.exe",
        @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
    };

    public static bool TryOpen(string streamUrl, out string? error)
    {
#if WINDOWS
        var vlcPath = WindowsVlcPaths.FirstOrDefault(File.Exists);
        if (vlcPath is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vlcPath,
                    Arguments = $"\"{streamUrl}\"",
                    UseShellExecute = false,
                });
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        error = "No se encontro VLC instalado en las rutas conocidas.";
        return false;
#else
        error = "Abrir en VLC solo esta implementado para Windows por ahora.";
        return false;
#endif
    }
}
