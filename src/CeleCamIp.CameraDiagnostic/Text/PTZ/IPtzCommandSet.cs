namespace CeleCamIp.CameraDiagnostic.Text.PTZ;

/// <summary>Direcciones de movimiento PTZ estandar (pan/tilt) mas zoom y foco.</summary>
public enum PtzDireccion
{
    Arriba,
    Abajo,
    Izquierda,
    Derecha,
    ArribaIzquierda,
    ArribaDerecha,
    AbajoIzquierda,
    AbajoDerecha,
    ZoomAcercar,
    ZoomAlejar,
    FocoCerca,
    FocoLejos,
}

/// <summary>
/// Contrato comun para "hablarle" PTZ a una camara, sin importar si el
/// transporte es ONVIF (<see cref="OnvifPtzTest"/>) o DVRIP
/// (<see cref="DvripPtzTest"/>). El objetivo de las pruebas de diagnostico
/// que implementan esto no es solo confirmar que la camara se mueve, sino
/// devolver la tabla real de comandos/funciones preestablecidas que esa
/// camara puntual soporta, para que la app los use como comandos fijos en
/// vez de adivinar cuales existen.
/// </summary>
public interface IPtzCommandSet
{
    Task<bool> MoverAsync(PtzDireccion direccion, int velocidad, CancellationToken ct);

    Task<bool> DetenerAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ListarPresetsAsync(CancellationToken ct);

    Task<bool> IrAPresetAsync(int numero, CancellationToken ct);
}
