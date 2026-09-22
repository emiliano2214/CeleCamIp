using System.Reflection;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Parche de reflection sobre SIPSorcery.Net.RtpIceChannel.
///
/// SIPSorcery trae hardcodeados DISCONNECTED_TIMEOUT_PERIOD=8s y
/// FAILED_TIMEOUT_PERIOD=16s. Con un TURN gratuito lejos geograficamente
/// (ExpressTURN, Francia) el relay a veces tarda mas de esos 16s en
/// completarse sobre WAN real (no LAN), asi que la RTCPeerConnection se
/// marca "failed" y se cierra ANTES de que llegue el candidato via TURN.
/// Resultado tipico: la app conecta al servidor, ve la camara, pero al
/// pedir el stream nunca llega video (pantalla negra) - la sesion WebRTC
/// ya murio antes de que el video pudiera empezar a fluir.
///
/// Estos campos son static mutables (NO const, NO readonly), asi que se
/// pueden pisar por reflection sin tocar el paquete NuGet. Se estiran a
/// 25s/45s: generoso para el peor caso de relay via TURN en Francia, sin
/// llegar a ser un timeout eterno si la conexion realmente esta muerta.
///
/// IMPORTANTE: llamar a Apply() UNA sola vez, antes de host.RunAsync(),
/// para que el valor este seteado antes de que se cree la primera
/// RTCPeerConnection.
/// </summary>
public static class IceTimeoutPatch
{
    public const int DisconnectedTimeoutSeconds = 25;
    public const int FailedTimeoutSeconds = 45;

    public static void Apply(ILogger? logger = null)
    {
        try
        {
            var iceChannelType = typeof(SIPSorcery.Net.RtpIceChannel);

            var disconnectedField = iceChannelType.GetField(
                "DISCONNECTED_TIMEOUT_PERIOD",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var failedField = iceChannelType.GetField(
                "FAILED_TIMEOUT_PERIOD",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (disconnectedField is null || failedField is null)
            {
                var msg = "[IceTimeoutPatch] No se encontraron los campos esperados en RtpIceChannel " +
                           "(puede que haya cambiado la version de SIPSorcery) - el patch NO se aplico.";
                logger?.LogWarning(msg);
                Console.WriteLine(msg);
                return;
            }

            var before = $"DISCONNECTED {disconnectedField.GetValue(null)}s, FAILED {failedField.GetValue(null)}s";

            disconnectedField.SetValue(null, DisconnectedTimeoutSeconds);
            failedField.SetValue(null, FailedTimeoutSeconds);

            var msgOk = $"[IceTimeoutPatch] Timeouts de ICE parcheados: {before} -> " +
                        $"DISCONNECTED {DisconnectedTimeoutSeconds}s, FAILED {FailedTimeoutSeconds}s";
            logger?.LogInformation(msgOk);
            Console.WriteLine(msgOk);
        }
        catch (Exception ex)
        {
            var msg = $"[IceTimeoutPatch] Error aplicando el patch de timeouts ICE: {ex.Message}";
            logger?.LogError(ex, msg);
            Console.WriteLine(msg);
        }
    }
}
