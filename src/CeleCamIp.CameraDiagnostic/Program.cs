using CeleCamIp.CameraDiagnostic.Text;
using CeleCamIp.CameraDiagnostic.Text.DVRIP;
using CeleCamIp.CameraDiagnostic.Text.PTZ;
using CeleCamIp.CameraDiagnostic.Text.RTSP;
using CeleCamIp.Shared.Cameras;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("=================================================");
Console.WriteLine(" CeleCamIp - Diagnostico de camara (RTSP/DVRIP/PTZ)");
Console.WriteLine("=================================================");
Console.WriteLine();

Console.Write("IP de la camara (ej: 192.168.1.50): ");
var ip = Console.ReadLine()?.Trim() ?? "";

if (string.IsNullOrWhiteSpace(ip))
{
    Console.WriteLine();
    Console.WriteLine("No se ingreso una IP. Cerrando.");
    return;
}

// Las credenciales de RTSP/ONVIF y de DVRIP suelen ser distintas en estas
// camaras (usuarios y/o passwords diferentes para cada protocolo), asi que
// se piden por separado y cada prueba usa el descriptor que corresponde.
Console.WriteLine();
Console.WriteLine("-- Credenciales RTSP / ONVIF --");
Console.Write("Usuario RTSP (Enter para dejar vacio): ");
var usuarioRtsp = Console.ReadLine()?.Trim();
Console.Write("Contraseña RTSP (Enter para dejar vacio): ");
var passwordRtsp = Console.ReadLine()?.Trim();

Console.WriteLine();
Console.WriteLine("-- Credenciales DVRIP (protocolo propietario, puerto 34567) --");
Console.Write("Usuario DVRIP (Enter para dejar vacio): ");
var usuarioDvrip = Console.ReadLine()?.Trim();
Console.Write("Contraseña DVRIP (Enter para dejar vacio): ");
var passwordDvrip = Console.ReadLine()?.Trim();

var camaraRtsp = new CameraDescriptor
{
    Id = "diagnostic-camera-rtsp",
    Name = "Camara de prueba (RTSP/ONVIF)",
    IpAddress = ip,
    Username = string.IsNullOrWhiteSpace(usuarioRtsp) ? null : usuarioRtsp,
    Password = string.IsNullOrWhiteSpace(passwordRtsp) ? null : passwordRtsp,
};

var camaraDvrip = new CameraDescriptor
{
    Id = "diagnostic-camera-dvrip",
    Name = "Camara de prueba (DVRIP)",
    IpAddress = ip,
    Username = string.IsNullOrWhiteSpace(usuarioDvrip) ? null : usuarioDvrip,
    Password = string.IsNullOrWhiteSpace(passwordDvrip) ? null : passwordDvrip,
};

// Cada prueba viaja con el descriptor (y por lo tanto las credenciales) del
// protocolo que le corresponde: las de RTSP/ONVIF para las pruebas RTSP y
// PTZ-ONVIF, las de DVRIP para las pruebas DVRIP y PTZ-DVRIP.
var pruebas = new List<(string Grupo, IDiagnosticTest Prueba, CameraDescriptor Camara)>
{
    ("RTSP",  new RtspOptionsTest(),          camaraRtsp),
    ("RTSP",  new RtspConfigDiscoveryTest(),  camaraRtsp),
    ("RTSP",  new RtspOnvifCapabilityTest(),  camaraRtsp),
    ("DVRIP", new DvripLoginTest(),           camaraDvrip),
    ("DVRIP", new DvripConfigDiscoveryTest(), camaraDvrip),
    ("PTZ",   new OnvifPtzTest(),             camaraRtsp),
    ("PTZ",   new DvripPtzTest(),             camaraDvrip),
};

while (true)
{
    Console.WriteLine();
    Console.WriteLine("Pruebas disponibles:");
    for (var i = 0; i < pruebas.Count; i++)
    {
        var (grupo, prueba, _) = pruebas[i];
        Console.WriteLine($"  {i + 1}. [{grupo}] {prueba.Nombre} - {prueba.Descripcion}");
    }
    Console.WriteLine("  A. Ejecutar todas");
    Console.WriteLine("  0. Salir");
    Console.Write("Opcion: ");

    var opcion = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(opcion) || opcion == "0")
    {
        break;
    }

    if (opcion.Equals("A", StringComparison.OrdinalIgnoreCase))
    {
        foreach (var (grupo, prueba, camara) in pruebas)
        {
            await EjecutarConConfirmacionAsync(grupo, prueba, camara);
        }
        continue;
    }

    if (int.TryParse(opcion, out var indice) && indice >= 1 && indice <= pruebas.Count)
    {
        var (grupo, prueba, camara) = pruebas[indice - 1];
        await EjecutarConConfirmacionAsync(grupo, prueba, camara);
    }
    else
    {
        Console.WriteLine("Opcion invalida.");
    }
}

Console.WriteLine();
Console.WriteLine("Fin del diagnostico.");

static async Task EjecutarConConfirmacionAsync(string grupo, IDiagnosticTest prueba, CameraDescriptor camara)
{
    // DvripPtzTest mueve la camara de verdad (aunque sea un pulso breve por
    // direccion): se pide confirmacion antes de correrla para no mover la
    // camara sin querer al elegir "Ejecutar todas".
    if (prueba is DvripPtzTest)
    {
        Console.WriteLine();
        Console.Write($"'{prueba.Nombre}' va a mover la camara brevemente en cada direccion. ¿Continuar? (s/N): ");
        var confirmacion = Console.ReadLine()?.Trim();

        if (!string.Equals(confirmacion, "s", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Prueba omitida.");
            return;
        }
    }

    await EjecutarYMostrarAsync(grupo, prueba, camara);
}

static async Task EjecutarYMostrarAsync(string grupo, IDiagnosticTest prueba, CameraDescriptor camara)
{
    Console.WriteLine();
    Console.WriteLine($"--- [{grupo}] {prueba.Nombre} ---");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    try
    {
        var resultado = await prueba.EjecutarAsync(camara, cts.Token);

        Console.WriteLine(resultado.Exitoso ? $"OK: {resultado.Resumen}" : $"FALLO: {resultado.Resumen}");

        if (!string.IsNullOrWhiteSpace(resultado.Error))
        {
            var errorCorto = resultado.Error.Length > 300 ? resultado.Error[..300] + "..." : resultado.Error;
            Console.WriteLine($"  Detalle: {errorCorto}");
        }

        foreach (var (clave, valor) in resultado.Datos)
        {
            var valorCorto = valor.Length > 200 ? valor[..200] + "..." : valor;
            Console.WriteLine($"  {clave}: {valorCorto}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Tiempo de espera agotado (30s).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error inesperado: {ex.Message}");
    }
}
