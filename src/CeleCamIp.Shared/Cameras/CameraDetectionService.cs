namespace CeleCamIp.Shared.Cameras;

/// <summary>
/// Orquesta la deteccion de una camara probando los adaptadores en orden
/// de prioridad (el orden en que fueron inyectados). Se usa UNA sola vez
/// al agregar la camara; el resultado (AdapterUsed) se persiste y las
/// conexiones futuras van directo al adaptador correcto.
/// </summary>
public class CameraDetectionService
{
    private readonly IReadOnlyList<ICameraAdapter> _adapters;

    public CameraDetectionService(IEnumerable<ICameraAdapter> adapters)
    {
        _adapters = adapters.ToList();
    }

    public async Task<CameraDescriptor> DetectAsync(CameraDescriptor camera, CancellationToken ct = default)
    {
        foreach (var adapter in _adapters)
        {
            bool canHandle;
            try
            {
                canHandle = await adapter.CanHandleAsync(camera, ct);
            }
            catch
            {
                // Un adaptador que falla al probar no debe frenar la cascada.
                continue;
            }

            if (canHandle)
            {
                return camera with { AdapterUsed = adapter.Name };
            }
        }

        // Ningun adaptador pudo manejarla: queda sin AdapterUsed.
        return camera;
    }

    public ICameraAdapter? GetAdapter(CameraDescriptor camera)
    {
        return _adapters.FirstOrDefault(a => a.Name == camera.AdapterUsed);
    }
}
