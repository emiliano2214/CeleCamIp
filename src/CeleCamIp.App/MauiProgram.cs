using CeleCamIp.App.Services;
using CeleCamIp.App.ViewModels;
using CeleCamIp.App.Views;
using CommunityToolkit.Maui;
#if ANDROID
using LibVLCSharp.MAUI;
using LibVLCSharp.Shared;
#endif
using Microsoft.Extensions.Logging;

namespace CeleCamIp.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if ANDROID
        // UseLibVLCSharp() solo tiene implementacion real para Android/iOS;
        // en Windows (WinUI) rompe la app en el arranque porque intenta
        // registrar handlers de un control que esa plataforma no soporta
        // todavia. Por eso se llama SOLO en Android.
        Core.Initialize();
        builder.UseLibVLCSharp();
#endif

        builder.Services.AddSingleton<ServerViewerService>();
        builder.Services.AddTransient<HousesViewModel>();
        builder.Services.AddTransient<HousesPage>();
        builder.Services.AddTransient<CamerasPage>();
        builder.Services.AddTransient<CameraPlayerPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
