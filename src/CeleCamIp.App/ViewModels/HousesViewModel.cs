using System.Collections.ObjectModel;
using System.ComponentModel;
using CeleCamIp.App.Services;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.App.ViewModels;

/// <summary>
/// Envuelve ServerViewerService para exponer lo que la UI necesita bindear
/// con INotifyPropertyChanged (el servicio en si no lo implementa porque no
/// depende de MAUI para nada mas que MainThread; esta clase es la unica
/// pieza especifica de UI).
/// </summary>
public class HousesViewModel : INotifyPropertyChanged
{
    private readonly ServerViewerService _viewer;
    private bool _isRefreshing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HouseSnapshot> Houses => _viewer.Houses;

    public string StatusText => _viewer.State switch
    {
        ServerConnectionState.Connected => "Conectado al servidor",
        ServerConnectionState.Connecting => "Conectando...",
        _ => "Sin conexion con el servidor",
    };

    public Color StatusColor => _viewer.State switch
    {
        ServerConnectionState.Connected => Colors.LimeGreen,
        ServerConnectionState.Connecting => Colors.Orange,
        _ => Colors.OrangeRed,
    };

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            _isRefreshing = value;
            OnPropertyChanged(nameof(IsRefreshing));
        }
    }

    public HousesViewModel(ServerViewerService viewer)
    {
        _viewer = viewer;
        _viewer.StateChanged += OnViewerStateChanged;
    }

    public async Task LoadAsync()
    {
        await _viewer.ConnectAsync(AppConfig.ServerHubUrl);
    }

    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await _viewer.RefreshAsync(AppConfig.ServerHubUrl);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void OnViewerStateChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        });
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
