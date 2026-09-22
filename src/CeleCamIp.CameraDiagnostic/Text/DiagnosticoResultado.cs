namespace CeleCamIp.CameraDiagnostic.Text;

/// <summary>
/// Resultado uniforme de cualquier prueba de diagnostico: si funciono, un
/// resumen legible para mostrar en consola, y los datos crudos/detallados
/// (config encontrada, comandos PTZ soportados, etc) para poder programarlos
/// despues en el resto del sistema.
/// </summary>
public class DiagnosticoResultado
{
    public required bool Exitoso { get; init; }
    public required string Resumen { get; init; }

    /// <summary>Pares clave/valor con el detalle encontrado.</summary>
    public Dictionary<string, string> Datos { get; init; } = new();

    public string? Error { get; init; }

    public static DiagnosticoResultado Ok(string resumen, Dictionary<string, string>? datos = null) =>
        new() { Exitoso = true, Resumen = resumen, Datos = datos ?? new() };

    public static DiagnosticoResultado Fallo(string resumen, string? error = null) =>
        new() { Exitoso = false, Resumen = resumen, Error = error };
}
