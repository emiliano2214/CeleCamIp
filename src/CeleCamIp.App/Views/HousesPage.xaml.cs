using System.Diagnostics;
using CeleCamIp.App.ViewModels;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.App.Views;

public partial class HousesPage : ContentPage
{
    private readonly HousesViewModel _viewModel;

    public HousesPage(HousesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await _viewModel.RefreshAsync();
    }

    private async void OnHouseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not HouseSnapshot house)
        {
            return;
        }

        try
        {
            await Shell.Current.GoToAsync(nameof(CamerasPage), new Dictionary<string, object>
            {
                { "House", house }
            });
        }
        catch (Exception ex)
        {
            // Ver nota en CamerasPage.ShowAlertSafeAsync: sin el
            // MainThread.InvokeOnMainThreadAsync, un DisplayAlert llamado
            // desde este catch puede crashear toda la app en Windows en vez
            // de mostrar el mensaje.
            Debug.WriteLine($"[HousesPage] Fallo GoToAsync(CamerasPage): {ex}");

            await MainThread.InvokeOnMainThreadAsync(() =>
                DisplayAlert("Error de Navegación", ex.Message, "OK"));
        }
    }
}
