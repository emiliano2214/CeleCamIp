using CeleCamIp.Shared.Cameras;
using CeleCamIp.Shared.Cameras.Adapters;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("=================================================");
Console.WriteLine(" CeleCamIp - Diagnostico de camara");
Console.WriteLine(" Prueba si la camara responde RTSP y con que ruta");
Console.WriteLine("=================================================");
Console.WriteLine();

Console.Write("IP de la camara (ej: 192.168.1.50): ");
var ip = Console.ReadLine()?.Trim() ?? "";

Console.Write("Usuario (Enter para dejar vacio): ");
var user = Console.ReadLine()?.Trim();

Console.Write("Contraseña (Enter para dejar vacio): ");
var pass = Console.ReadLine()?.Trim();

if (string.IsNullOrWhiteSpace(ip))
{
    Console.WriteLine();
    Console.WriteLine("No se ingreso una IP. Cerrando.");
}
else
{
    var camera = new CameraDescriptor
    {
        Id = "diagnostic-camera",
        Name = "Camara de prueba",
        IpAddress = ip,
        Username = string.IsNullOrWhiteSpace(user) ? null : user,
        Password = string.IsNullOrWhiteSpace(pass) ? null : pass,
    };

    var adapters = new ICameraAdapter[] { new RtspCameraAdapter() };
    var detection = new CameraDetectionService(adapters);

    Console.WriteLine();
    Console.WriteLine($"Probando conexion a {ip} ...");
    Console.WriteLine("(esto puede tardar unos segundos mientras se prueban distintas rutas RTSP)");
    Console.WriteLine();

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    try
    {
        var detected = await detection.DetectAsync(camera, cts.Token);

        if (detected.AdapterUsed is null)
        {
            Console.WriteLine("RESULTADO: No se pudo detectar la camara por RTSP.");
            Console.WriteLine();
            Console.WriteLine("Posibles causas:");
            Console.WriteLine("  - La camara no soporta RTSP (usa protocolo propietario, tipo iCSee).");
            Console.WriteLine("  - La IP, usuario o contraseña son incorrectos.");
            Console.WriteLine("  - La camara esta en otra red / no responde en el puerto 554.");
            Console.WriteLine("  - Este dispositivo directamente no es una camara.");
            Console.WriteLine();
            Console.WriteLine("Proximo paso sugerido: probar con la app ONVIF Device Manager,");
            Console.WriteLine("o confirmar la IP/credenciales desde la app iCSee.");
        }
        else
        {
            Console.WriteLine($"RESULTADO: La camara respondio RTSP (adapter: {detected.AdapterUsed})");
            Console.WriteLine();

            var adapter = detection.GetAdapter(detected)!;
            var connection = await adapter.ConnectAsync(detected, cts.Token);

            Console.WriteLine($"  URL RTSP encontrada : {connection.NormalizedStreamUrl}");
            Console.WriteLine($"  Soporta audio        : {(connection.SupportsAudio ? "Si" : "No")}");
            Console.WriteLine();
            Console.WriteLine("Podes probar esta URL directamente en VLC:");
            Console.WriteLine("  Medio -> Abrir ubicacion de red -> pegar la URL de arriba");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("RESULTADO: Tiempo de espera agotado (30s). La camara no respondio.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RESULTADO: Error inesperado - {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("Presione Enter para salir...");
Console.ReadLine();
