namespace CeleCamIp.Shared.Cameras;

/// <summary>
/// Estado de una casa tal como lo ve el Server: si el Gateway esta conectado
/// en este momento y que camaras reporto la ultima vez que corrio la
/// deteccion local. Es lo que se envia a la app movil (viewer) para que
/// pueda mostrar la lista de casas/camaras sin saber nada de SignalR.
/// </summary>
public record HouseSnapshot
{
    public required string HouseId { get; init; }
    public bool IsOnline { get; init; }
    public IReadOnlyList<CameraDescriptor> Cameras { get; init; } = Array.Empty<CameraDescriptor>();
}
