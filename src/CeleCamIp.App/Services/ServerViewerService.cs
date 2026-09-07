using System.Collections.ObjectModel;
using CeleCamIp.Shared.Cameras;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace CeleCamIp.App.Services;

/// <summary>Estados posibles de la conexion con el servidor, para pintar la UI.</summary>
public enum ServerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// Mantiene la conexion de la app (viewer) hacia el mismo GatewayHub al que
/// se conectan las casas, pero del lado "cliente que mira". Al conectar llama
/// JoinAsViewer para traerse el snapshot inicial, y despues escucha en tiempo
/// real HouseOnline / HouseOffline / CamerasUpdated para mantener la lista
/// actualizada sin tener que refrescar a mano.
///
/// Es un singleton (registrado en MauiProgram) para que la conexion se
/// mantenga viva mientras la app esta abierta, sin importar entre que
/// paginas navegue el usuario.
/// </summary>
public class ServerViewerService : IAsyncDisposable
{
    private readonly ILogger<ServerViewerService> _logger;
    private HubConnection? _connection;

    public ObservableCollection<HouseSnapshot> Houses { get; } = new();

    public ServerConnectionState State { get; private set; } = ServerConnectionState.Disconnected;

    public event Action? StateChanged;

    public ServerViewerService(ILogger<ServerViewerService> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string hubUrl)
    {
        if (_connection is not null)
        {
            return; // Ya conectado o conectandose.
        }

        SetState(ServerConnectionState.Connecting);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult<string?>(AppSecrets.ApiKey))
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.On<string>("HouseOnline", houseId => UpsertHouse(houseId, isOnline: true, cameras: null));
        _connection.On<string>("HouseOffline", houseId => UpsertHouse(houseId, isOnline: false, cameras: null));
        _connection.On<string, List<CameraDescriptor>>("CamerasUpdated", (houseId, cameras) =>
            UpsertHouse(houseId, isOnline: null, cameras: cameras));

        _connection.Reconnecting += _ =>
        {
            SetState(ServerConnectionState.Connecting);
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            await RequestSnapshotAsync();
            SetState(ServerConnectionState.Connected);
        };

        _connection.Closed += _ =>
        {
            SetState(ServerConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync();
            await RequestSnapshotAsync();
            SetState(ServerConnectionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo conectar al servidor en {HubUrl}", hubUrl);
            SetState(ServerConnectionState.Disconnected);
        }
    }

    /// <summary>Pide de nuevo el snapshot completo (por ejemplo, en un pull-to-refresh).</summary>
    public async Task RefreshAsync(string hubUrl)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            await ConnectAsync(hubUrl);
            return;
        }

        await RequestSnapshotAsync();
    }

    private async Task RequestSnapshotAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var snapshot = await _connection.InvokeAsync<List<HouseSnapshot>>("JoinAsViewer");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Houses.Clear();
                foreach (var house in snapshot)
                {
                    Houses.Add(house);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al pedir el snapshot inicial de casas.");
        }
    }

    private void UpsertHouse(string houseId, bool? isOnline, List<CameraDescriptor>? cameras)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var index = -1;
            for (var i = 0; i < Houses.Count; i++)
            {
                if (Houses[i].HouseId == houseId)
                {
                    index = i;
                    break;
                }
            }

            var current = index >= 0 ? Houses[index] : new HouseSnapshot { HouseId = houseId };
            var updated = current with
            {
                IsOnline = isOnline ?? current.IsOnline,
                Cameras = cameras ?? current.Cameras,
            };

            if (index >= 0)
            {
                Houses[index] = updated;
            }
            else
            {
                Houses.Add(updated);
            }
        });
    }

    private void SetState(ServerConnectionState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
