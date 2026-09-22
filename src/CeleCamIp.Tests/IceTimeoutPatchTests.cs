using System.Reflection;
using CeleCamIp.Gateway.Services;

namespace CeleCamIp.Tests;

/// <summary>
/// Este test existe por una razon concreta: el 20/09/2026 se detecto que
/// IceTimeoutPatch.cs -el fix que evita que las sesiones WebRTC se cierren
/// antes de que el relay TURN (ExpressTURN, Francia) termine de conectar en
/// pruebas WAN reales- NUNCA habia sido commiteado a git (confirmado con
/// `git log --all -S"IceTimeoutPatch"`, cero resultados en todo el
/// historial). El sintoma en produccion (Pi + MonsterASP + celular) era
/// exactamente "conecta y aparece la camara, pero al pedir el stream no
/// llega video" - pantalla negra porque el ICE se marcaba failed a los 16s,
/// antes de que el candidato TURN llegara.
///
/// Si en el futuro alguien vuelve a pisar/borrar el patch (o SIPSorcery
/// cambia de version y los campos dejan de existir con este nombre), este
/// test lo detecta en el momento de compilar/correr `dotnet test`, sin
/// necesidad de un Pi, un TURN, ni un celular para notarlo.
/// </summary>
public class IceTimeoutPatchTests
{
    private static (int disconnected, int failed) ReadCurrentTimeouts()
    {
        var t = typeof(SIPSorcery.Net.RtpIceChannel);
        var disconnectedField = t.GetField("DISCONNECTED_TIMEOUT_PERIOD", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var failedField = t.GetField("FAILED_TIMEOUT_PERIOD", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(disconnectedField);
        Assert.NotNull(failedField);

        return ((int)disconnectedField!.GetValue(null)!, (int)failedField!.GetValue(null)!);
    }

    [Fact]
    public void RtpIceChannel_expone_los_campos_de_timeout_que_el_patch_necesita()
    {
        // Si esto falla, SIPSorcery cambio de version/nombre de campo y
        // IceTimeoutPatch.Apply() esta fallando en silencio (solo loguea un
        // warning) - hay que actualizar los nombres de campo en el patch.
        var t = typeof(SIPSorcery.Net.RtpIceChannel);
        var disconnectedField = t.GetField("DISCONNECTED_TIMEOUT_PERIOD", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var failedField = t.GetField("FAILED_TIMEOUT_PERIOD", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(disconnectedField);
        Assert.NotNull(failedField);
        Assert.False(disconnectedField!.IsLiteral, "El campo debe ser 'static' mutable, no 'const', para que el patch por reflection funcione.");
        Assert.False(failedField!.IsLiteral, "El campo debe ser 'static' mutable, no 'const', para que el patch por reflection funcione.");
    }

    [Fact]
    public void Apply_deja_los_timeouts_en_los_valores_generosos_para_TURN_lejano()
    {
        IceTimeoutPatch.Apply();

        var (disconnected, failed) = ReadCurrentTimeouts();

        Assert.Equal(IceTimeoutPatch.DisconnectedTimeoutSeconds, disconnected);
        Assert.Equal(IceTimeoutPatch.FailedTimeoutSeconds, failed);

        // Los valores por defecto de SIPSorcery (8s / 16s) son los que
        // causaban el corte antes de que el TURN de Francia respondiera.
        // Si el patch alguna vez deja de aplicarse, esto tiene que fallar
        // detectando que seguimos en los defaults originales.
        Assert.True(disconnected > 8, "El timeout de DISCONNECTED debe ser mayor al default de SIPSorcery (8s), o el patch no esta haciendo nada.");
        Assert.True(failed > 16, "El timeout de FAILED debe ser mayor al default de SIPSorcery (16s), o el patch no esta haciendo nada.");
    }

    [Fact]
    public void Apply_es_idempotente_llamarlo_dos_veces_no_rompe_nada()
    {
        IceTimeoutPatch.Apply();
        IceTimeoutPatch.Apply();

        var (disconnected, failed) = ReadCurrentTimeouts();

        Assert.Equal(IceTimeoutPatch.DisconnectedTimeoutSeconds, disconnected);
        Assert.Equal(IceTimeoutPatch.FailedTimeoutSeconds, failed);
    }
}
