using System.Collections.Concurrent;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.Server.Services;

/// <summary>
/// Registro en memoria de que casas estan conectadas ahora mismo y que
/// camaras reporto cada una. Vive mientras vive el proceso del Server;
/// si se reinicia, las casas se vuelven a registrar solas apenas el
/// Gateway reconecta (tiene reintento automatico).
/// </summary>
public interface IHouseRegistry
{
    void MarkOnline(string houseId, string connectionId);
    string? MarkOfflineByConnection(string connectionId);
    void SetCameras(string houseId, IReadOnlyList<CameraDescriptor> cameras);
    HouseSnapshot? Get(string houseId);
    List<HouseSnapshot> GetAll();

    /// <summary>ConnectionId actual del Gateway de una casa, o null si no esta online. Se usa para enrutar la senializacion WebRTC (SDP/ICE) hacia el Gateway correcto.</summary>
    string? GetConnectionId(string houseId);
}

public class HouseRegistry : IHouseRegistry
{
    private readonly ConcurrentDictionary<string, string> _connectionByHouse = new();
    private readonly ConcurrentDictionary<string, string> _houseByConnection = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<CameraDescriptor>> _camerasByHouse = new();

    public void MarkOnline(string houseId, string connectionId)
    {
        _connectionByHouse[houseId] = connectionId;
        _houseByConnection[connectionId] = houseId;
    }

    public string? MarkOfflineByConnection(string connectionId)
    {
        if (_houseByConnection.TryRemove(connectionId, out var houseId))
        {
            // OJO: solo borramos la asociacion houseId->connectionId si el
            // connectionId que se desconecta es el que esta activo ahora mismo.
            // Si dos Gateways de la misma casa llegaron a conectarse a la vez
            // (ej: quedo un proceso viejo corriendo), al caerse el viejo esto
            // evita tumbar el registro del nuevo, que sigue vivo.
            _connectionByHouse.TryRemove(new KeyValuePair<string, string>(houseId, connectionId));
            return houseId;
        }

        return null;
    }

    public void SetCameras(string houseId, IReadOnlyList<CameraDescriptor> cameras)
    {
        _camerasByHouse[houseId] = cameras;
    }

    public HouseSnapshot? Get(string houseId)
    {
        var isKnown = _connectionByHouse.ContainsKey(houseId) || _camerasByHouse.ContainsKey(houseId);
        if (!isKnown)
        {
            return null;
        }

        return new HouseSnapshot
        {
            HouseId = houseId,
            IsOnline = _connectionByHouse.ContainsKey(houseId),
            Cameras = _camerasByHouse.TryGetValue(houseId, out var cams) ? cams : Array.Empty<CameraDescriptor>()
        };
    }

    public List<HouseSnapshot> GetAll()
    {
        var houseIds = _connectionByHouse.Keys.Union(_camerasByHouse.Keys).Distinct();
        return houseIds.Select(id => Get(id)!).ToList();
    }

    public string? GetConnectionId(string houseId)
    {
        return _connectionByHouse.TryGetValue(houseId, out var connectionId) ? connectionId : null;
    }
}
