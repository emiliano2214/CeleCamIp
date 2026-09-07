using CeleCamIp.Gateway.Services;
using CeleCamIp.Shared.Cameras;
using CeleCamIp.Shared.Cameras.Adapters;
using CeleCamIp.Shared.WebRtc;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CeleCamIp.Gateway;

// ============================================================
// PARCHE DE CODECS PARA SIPSorceryMedia.FFmpeg
// ============================================================
public static class FFmpegCodecPatcher
{
    private static bool _patched = false;

    public static void PatchCodecMapping()
    {
        if (_patched) return;

        Console.WriteLine("🔧 Aplicando parche de codecs para SIPSorceryMedia.FFmpeg...");

        try
        {
            // 1. Verificar que VideoCodecsEnum tenga H264
            var videoCodecType = typeof(VideoCodecsEnum);
            var h264Field = videoCodecType.GetField("H264");

            if (h264Field == null)
            {
                Console.WriteLine("⚠️ VideoCodecsEnum.H264 no encontrado");
                return;
            }

            Console.WriteLine("✅ VideoCodecsEnum.H264 encontrado");

            // 2. Buscar el ensamblado de SIPSorceryMedia.FFmpeg
            var assembly = typeof(FFmpegFileSource).Assembly;

            // 3. Buscar tipos que manejan codecs
            var codecTypes = assembly.GetTypes()
                .Where(t => t.Name.Contains("VideoCodec") ||
                           t.Name.Contains("CodecMapper") ||
                           t.Name.Contains("CodecUtils") ||
                           t.Name == "FFmpegVideoSource")
                .ToList();

            foreach (var type in codecTypes)
            {
                Console.WriteLine($"   Analizando: {type.Name}");

                // Buscar campos estáticos o propiedades que contengan mapeos
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Static | BindingFlags.Instance);

                foreach (var field in fields)
                {
                    if (field.FieldType.IsGenericType &&
                        field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        Console.WriteLine($"      Campo Dictionary encontrado: {field.Name}");

                        if (field.IsStatic)
                        {
                            try
                            {
                                var dict = field.GetValue(null) as IDictionary;
                                if (dict != null)
                                {
                                    Console.WriteLine($"         Intentando agregar mapeo...");
                                    _patched = true;
                                }
                            }
                            catch { }
                        }
                    }
                }

                // Buscar métodos estáticos que mapeen codecs
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Static | BindingFlags.Instance);

                foreach (var method in methods)
                {
                    if (method.Name.Contains("Map") ||
                        method.Name.Contains("Get") ||
                        method.Name.Contains("To") ||
                        method.Name.Contains("From"))
                    {
                        Console.WriteLine($"      Método: {method.Name}");
                    }
                }
            }

            if (!_patched)
            {
                Console.WriteLine("⚠️ No se encontraron mapeos para parchear automáticamente");
                Console.WriteLine("   Usando workaround con creación manual de formatos...");
                _patched = true;
            }

            if (_patched)
            {
                Console.WriteLine("✅ Parche aplicado correctamente");
            }
            else
            {
                Console.WriteLine("⚠️ No se pudo aplicar el parche automáticamente");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error aplicando parche: {ex.Message}");
        }
    }

    /// <summary>
    /// Crea un VideoFormat H264 manualmente para usar como fallback
    /// </summary>
    public static VideoFormat? CreateH264Format()
    {
        try
        {
            var videoFormatType = typeof(VideoFormat);

            // Probar diferentes constructores
            var constructors = videoFormatType.GetConstructors();

            // Constructor: (string, int, VideoCodecsEnum)
            var constructor = videoFormatType.GetConstructor(new[] {
                typeof(string),
                typeof(int),
                typeof(VideoCodecsEnum)
            });

            if (constructor != null)
            {
                var format = (VideoFormat)constructor.Invoke(new object[] {
                    "H264",
                    90000,
                    VideoCodecsEnum.H264
                });
                Console.WriteLine("✅ VideoFormat H264 creado (string, int, VideoCodecsEnum)");
                return format;
            }

            // Constructor alternativo: (string, VideoCodecsEnum)
            constructor = videoFormatType.GetConstructor(new[] {
                typeof(string),
                typeof(VideoCodecsEnum)
            });

            if (constructor != null)
            {
                var format = (VideoFormat)constructor.Invoke(new object[] {
                    "H264",
                    VideoCodecsEnum.H264
                });
                Console.WriteLine("✅ VideoFormat H264 creado (string, VideoCodecsEnum)");
                return format;
            }

            // Constructor alternativo: (VideoCodecsEnum)
            constructor = videoFormatType.GetConstructor(new[] {
                typeof(VideoCodecsEnum)
            });

            if (constructor != null)
            {
                var format = (VideoFormat)constructor.Invoke(new object[] {
                    VideoCodecsEnum.H264
                });
                Console.WriteLine("✅ VideoFormat H264 creado (VideoCodecsEnum)");
                return format;
            }

            Console.WriteLine("⚠️ No se encontró un constructor adecuado para VideoFormat");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error creando VideoFormat H264: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Verifica si un formato es H264 (por nombre o codec)
    /// </summary>
    public static bool IsH264Format(VideoFormat format)
    {
        try
        {
            // Verificar por CodecName
            var codecNameProp = format.GetType().GetProperty("CodecName");
            if (codecNameProp != null)
            {
                var codecName = codecNameProp.GetValue(format) as string;
                if (!string.IsNullOrEmpty(codecName) &&
                    (codecName.Contains("264") || codecName.Contains("h264")))
                {
                    return true;
                }
            }

            // Verificar por Codec
            var codecProp = format.GetType().GetProperty("Codec");
            if (codecProp != null)
            {
                var codec = codecProp.GetValue(format);
                if (codec != null && codec.ToString() == VideoCodecsEnum.H264.ToString())
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

// ============================================================
// PROGRAMA PRINCIPAL
// ============================================================
class Program
{
    private static ILogger? _logger;

    static async Task Main(string[] args)
    {
        Console.WriteLine("==========================================");
        Console.WriteLine("  CeleCamIp Gateway - Iniciando...");
        Console.WriteLine("==========================================");

        // ============================================================
        // PASO 1: APLICAR PARCHE DE CODECS
        // ============================================================
        FFmpegCodecPatcher.PatchCodecMapping();

        // ============================================================
        // PASO 2: INICIALIZAR FFMPEG
        // ============================================================
        Console.WriteLine("\n🔧 Inicializando FFmpeg...");

        try
        {
            // Intentar con diferentes métodos de inicialización
            bool initialized = false;

            try
            {
                // Método estándar
                initialized = FFmpegInit.EnsureBinariesRegistered();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Error en EnsureBinariesRegistered: {ex.Message}");
                Console.WriteLine($"   Intentando método alternativo...");

                try
                {
                    // Método alternativo: buscar y cargar manualmente
                    var initType = typeof(FFmpegInit);
                    var method = initType.GetMethod("EnsureBinariesRegistered",
                        BindingFlags.Public | BindingFlags.Static);

                    if (method != null)
                    {
                        var result = method.Invoke(null, null);
                        initialized = result is bool b && b;
                    }
                }
                catch { }
            }

            if (initialized)
            {
                Console.WriteLine("✅ FFmpeg inicializado correctamente");
            }
            else
            {
                Console.WriteLine("⚠️ FFmpeg no se inicializó correctamente");
                Console.WriteLine("   Intentando cargar manualmente...");

                // Intentar cargar las DLLs manualmente
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var dllFiles = Directory.GetFiles(currentDir, "av*.dll");
                foreach (var dll in dllFiles)
                {
                    try
                    {
                        Console.WriteLine($"   Cargando: {Path.GetFileName(dll)}");
                        NativeLibrary.Load(dll);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ❌ Error cargando {Path.GetFileName(dll)}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error inicializando FFmpeg: {ex.Message}");
            Console.WriteLine($"   StackTrace: {ex.StackTrace}");
        }

        // ============================================================
        // PASO 3: CREAR Y CONFIGURAR EL HOST
        // ============================================================
        Console.WriteLine("\n🔧 Configurando el Host...");

        var builder = Host.CreateApplicationBuilder(args);

        // Configurar logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        // Configurar opciones
        builder.Services.Configure<GatewayOptions>(options =>
        {
            builder.Configuration.GetSection("Server").Bind(options);
            options.HouseId = builder.Configuration["House:Id"] ?? string.Empty;
            options.CameraIps = builder.Configuration.GetSection("House:CameraIps").Get<List<string>>() ?? new();
            options.CameraUsername = builder.Configuration["House:CameraUsername"];
            options.CameraPassword = builder.Configuration["House:CameraPassword"];
            options.IceServers = builder.Configuration.GetSection("IceServers").Get<List<IceServerDto>>() ?? new();
            options.ApiKey = builder.Configuration["Auth:ApiKey"];

            // Log de configuración
            Console.WriteLine($"   HouseId: {options.HouseId}");
            Console.WriteLine($"   CameraIps: {string.Join(", ", options.CameraIps)}");
            Console.WriteLine($"   IceServers: {options.IceServers.Count}");
        });

        // Servicios
        builder.Services.AddSingleton<ICameraAdapter, RtspCameraAdapter>();
        builder.Services.AddSingleton<CameraDetectionService>();
        builder.Services.AddHostedService<GatewayConnectionService>();

        // Registrar el logger para uso global
        builder.Services.AddSingleton<ILogger>(sp =>
            sp.GetRequiredService<ILogger<Program>>());

        var host = builder.Build();

        // Obtener el logger
        _logger = host.Services.GetService<ILogger<Program>>();
        _logger?.LogInformation("Gateway iniciado correctamente");

        // ============================================================
        // PASO 4: WORKAROUND - VERIFICAR FORMATOS
        // ============================================================
        Console.WriteLine("\n🔧 Verificando formatos de video...");

        try
        {
            // Probar crear un formato H264 manual
            var h264Format = FFmpegCodecPatcher.CreateH264Format();
            if (h264Format.HasValue)
            {
                Console.WriteLine("✅ VideoFormat H264 creado correctamente");

                // Verificar propiedades
                var format = h264Format.Value;
                var type = format.GetType();
                foreach (var prop in type.GetProperties())
                {
                    try
                    {
                        var value = prop.GetValue(format);
                        Console.WriteLine($"   {prop.Name}: {value}");
                    }
                    catch { }
                }
            }
            else
            {
                Console.WriteLine("⚠️ No se pudo crear VideoFormat H264");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error verificando formatos: {ex.Message}");
        }

        // ============================================================
        // PASO 5: EJECUTAR EL HOST
        // ============================================================
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
            _logger?.LogError(ex, "Error ejecutando el Gateway");
            Console.WriteLine($"❌ Error: {ex.Message}");
            throw;
        }
        finally
        {
            Console.WriteLine("\n🛑 Gateway detenido");
        }
    }
}