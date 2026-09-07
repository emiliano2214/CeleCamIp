using CeleCamIp.App.Views;

namespace CeleCamIp.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(CamerasPage), typeof(CamerasPage));
        Routing.RegisterRoute(nameof(CameraPlayerPage), typeof(CameraPlayerPage));
    }
}
