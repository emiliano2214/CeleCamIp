using CeleCamIp.CameraDiagnostic.Text.DVRIP;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.CameraDiagnostic.Text.PTZ;

/// <summary>
/// Para camaras sin ONVIF que controlan el PTZ por DVRIP (comando
/// OPPTZControl, msgid 1400 — ver <see cref="DvripClient"/>), esta prueba
/// manda un pulso muy breve y a velocidad baja en cada direccion conocida
/// y frena inmediatamente despues (Start + Stop), confirmando si el
/// dispositivo aceptó ("ACK") ese comando puntual.
///
/// El resultado es justo la tabla que pide la app para programar los
/// comandos preestablecidos: qué direcciones/funciones responden en esta
/// camara concreta, porque no todas las genericas implementan las 8
/// direcciones + zoom + foco.
///
/// El pulso es intencionalmente corto (300ms, velocidad 1) para minimizar
/// que la camara termine desencuadrada solo por correr el diagnostico; aun
/// asi, esta prueba SI mueve la camara un poco en cada direccion soportada.
/// </summary>
public class DvripPtzTest : IDiagnosticTest
{
    public string Nombre => "PTZ - Comandos DVRIP";
    public string Descripcion => "Prueba cada direccion PTZ por DVRIP (pulso breve) y reporta cuales acepta la camara.";

    private static readonly TimeSpan DuracionPulso = TimeSpan.FromMilliseconds(300);
    private const int VelocidadPrueba = 1;

    // Nombres de comando tal como los espera el firmware DVRIP/Sofia en
    // OPPTZControl. Puede variar levemente segun fabricante/firmware.
    private static readonly (string Nombre, string Comando)[] Direcciones =
    {
        ("Arriba", "Up"),
        ("Abajo", "Down"),
        ("Izquierda", "Left"),
        ("Derecha", "Right"),
        ("ArribaIzquierda", "LeftUp"),
        ("ArribaDerecha", "RightUp"),
        ("AbajoIzquierda", "LeftDown"),
        ("AbajoDerecha", "RightDown"),
        ("ZoomAcercar", "ZoomTele"),
        ("ZoomAlejar", "ZoomWide"),
        ("FocoCerca", "FocusNear"),
        ("FocoLejos", "FocusFar"),
    };

    public async Task<DiagnosticoResultado> EjecutarAsync(CameraDescriptor camara, CancellationToken ct)
    {
        using var cliente = new DvripClient();

        try
        {
            await cliente.ConectarAsync(camara.IpAddress, ct);
        }
        catch (Exception ex)
        {
            return DiagnosticoResultado.Fallo("No se pudo conectar al puerto 34567 (DVRIP).", ex.Message);
        }

        var (loginOk, _) = await cliente.LoginAsync(camara.Username ?? string.Empty, camara.Password ?? string.Empty, ct);
        if (!loginOk)
        {
            return DiagnosticoResultado.Fallo("Login DVRIP fallido: no se puede probar PTZ sin sesion.");
        }

        var soportados = new List<string>();
        var datos = new Dictionary<string, string>();

        foreach (var (nombre, comando) in Direcciones)
        {
            ct.ThrowIfCancellationRequested();

            var respuestaStart = await cliente.EnviarComandoPtzAsync(comando, VelocidadPrueba, ct, clase: "Start");
            await Task.Delay(DuracionPulso, ct);
            var respuestaStop = await cliente.EnviarComandoPtzAsync(comando, VelocidadPrueba, ct, clase: "Stop");

            // Ret 100 = OK en este protocolo; cualquier otra cosa (o sin
            // respuesta) se interpreta como "no soportado" para ese comando.
            var ok = respuestaStart is { } r &&
                     r.TryGetProperty("Ret", out var ret) &&
                     ret.GetInt32() == 100;

            datos[nombre] = ok ? $"OK (comando '{comando}')" : $"No soportado o sin ACK (comando '{comando}')";
            if (ok)
            {
                soportados.Add(nombre);
            }

            // Por si el Stop no llego a procesarse antes del siguiente comando.
            _ = respuestaStop;
        }

        return soportados.Count > 0
            ? DiagnosticoResultado.Ok($"La camara acepto {soportados.Count}/{Direcciones.Length} comandos PTZ: {string.Join(", ", soportados)}", datos)
            : DiagnosticoResultado.Fallo("La camara no acepto ningun comando PTZ por DVRIP (puede no tener motor PTZ, o usar otros nombres de comando en este firmware).", string.Join("\n", datos.Select(kv => $"{kv.Key}: {kv.Value}")));
    }
}
