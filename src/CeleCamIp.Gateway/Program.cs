using CeleCamIp.Gateway.Services;
using CeleCamIp.Shared.Cameras;
using CeleCamIp.Shared.Cameras.Adapters;
using CeleCamIp.Shared.WebRtc;
using SIPSorceryMedia.FFmpeg;

namespace CeleCamIp.Gateway;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("==========================================");
        Console.WriteLine("  CeleCamIp Gateway - Iniciando...");
        Console.WriteLine("==========================================");

        // ============================================================
        // Inicializar FFmpeg
        // ============================================================
        // SIPSorceryMedia.FFmpeg (via FFmpeg.AutoGen) en Linux NO usa el cache
        // global del linker (ldconfig/ld.so.cache) para auto-detectar las libs.
        // RegisterFFmpegBinaries(libPath=null) solo prueba un puñado de rutas
        // fijas relativas al ejecutable; como nuestras libs de FFmpeg 8.1 se
        // copian a /usr/lib en el Dockerfile, hay que indicarlo EXPLICITAMENTE
        // con el parametro libPath. Sin esto tira:
        //   System.ApplicationException: Unable to find FFMPEG binaries
        //     at SIPSorceryMedia.FFmpeg.FFmpegInit.RegisterFFmpegBinaries(String libPath)
        //
        // Ruta usada en runtime Linux/Docker (ver Dockerfile: cp .../lib/*.so* /usr/lib/):
        var ffmpegLibPath = OperatingSystem.IsLinux() ? "/usr/lib" : null;

        Console.WriteLine("\n🔧 Inicializando FFmpeg...");
        try
        {
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_VERBOSE, ffmpegLibPath, null);
            Console.WriteLine("✅ FFmpeg inicializado correctamente");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error inicializando FFmpeg: {ex.GetType().FullName}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.WriteLine($"   InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }
            Console.WriteLine("   Verificar que las librerias nativas de libav esten en la ruta indicada (ffmpegLibPath)");
            Console.WriteLine("   y que su version mayor coincida con FFmpeg.AutoGen. Ver comentario en el .csproj.");
        }

        // ============================================================
        // Crear y configurar el Host
        // ============================================================
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        builder.Services.Configure<GatewayOptions>(options =>
        {
            builder.Configuration.GetSection("Server").Bind(options);
            options.HouseId = builder.Configuration["House:Id"] ?? string.Empty;
            options.CameraIps = builder.Configuration.GetSection("House:CameraIps").Get<List<string>>() ?? new();
            options.CameraUsername = builder.Configuration["House:CameraUsername"];
            options.CameraPassword = builder.Configuration["House:CameraPassword"];
            options.IceServers = builder.Configuration.GetSection("IceServers").Get<List<IceServerDto>>() ?? new();
            options.ApiKey = builder.Configuration["Auth:ApiKey"];

            Console.WriteLine($"   HouseId: {options.HouseId}");
            Console.WriteLine($"   CameraIps: {string.Join(", ", options.CameraIps)}");
            Console.WriteLine($"   IceServers: {options.IceServers.Count}");
        });

        builder.Services.AddSingleton<ICameraAdapter, RtspCameraAdapter>();
        builder.Services.AddSingleton<CameraDetectionService>();
        builder.Services.AddHostedService<GatewayConnectionService>();

        builder.Services.AddSingleton<ILogger>(sp =>
            sp.GetRequiredService<ILogger<Program>>());

        var host = builder.Build();

        var logger = host.Services.GetService<ILogger<Program>>();
        logger?.LogInformation("Gateway iniciado correctamente");

        Console.WriteLine("\n==========================================");
        Console.WriteLine("  Gateway listo - Esperando conexiones...");
        Console.WriteLine("  Presiona Ctrl+C para detener");
        Console.WriteLine("==========================================\n");

        try
        {
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error ejecutando el Gateway");
            Console.WriteLine($"❌ Error: {ex.Message}");
            throw;
        }
        finally
        {
            Console.WriteLine("\n🛑 Gateway detenido");
        }
    }
}
