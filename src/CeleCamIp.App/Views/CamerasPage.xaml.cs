using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CeleCamIp.Shared.Cameras;
using Microsoft.Maui.Controls;

namespace CeleCamIp.App.Views;

[QueryProperty(nameof(House), "House")]
public partial class CamerasPage : ContentPage
{
    public ObservableCollection<CameraDescriptor> Cameras { get; } = new();

    private string _houseTitle = "Cámaras";
    public string HouseTitle
    {
        get => _houseTitle;
        private set
        {
            _houseTitle = value;
            OnPropertyChanged(nameof(HouseTitle));
        }
    }

    private HouseSnapshot? _house;
    public HouseSnapshot? House
    {
        get => _house;
        set
        {
            _house = value;
            ApplyHouse(value);
        }
    }

    public CamerasPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    private void ApplyHouse(HouseSnapshot? house)
    {
        if (house is null)
        {
            return;
        }

        HouseTitle = $"Casa: {house.HouseId}";

        Cameras.Clear();
        foreach (var camera in house.Cameras)
        {
            Cameras.Add(camera);
        }
    }

    private async void OnViewLiveClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        if (button.BindingContext is not CameraDescriptor camera ||
            string.IsNullOrWhiteSpace(camera.StreamUrl))
        {
            await ShowAlertSafeAsync(
                "Sin stream todavía",
                "Esta cámara no tiene una URL de video resuelta. Puede que el Gateway todavía no haya terminado de detectarla.");
            return;
        }

        try
        {
            button.IsEnabled = false;

            await Shell.Current.GoToAsync(nameof(CameraPlayerPage), new Dictionary<string, object>
            {
                { "Camera", camera },
                { "HouseId", _house?.HouseId ?? string.Empty }
            });
        }
        catch (Exception ex)
        {
            // Log completo (con stack trace) al Output de Visual Studio / Debug,
            // asi la proxima vez vemos la excepcion real sin depender de que
            // el DisplayAlert llegue a mostrarse.
            Debug.WriteLine($"[CamerasPage] Fallo GoToAsync(CameraPlayerPage): {ex}");

            await ShowAlertSafeAsync("Error de Navegación", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// DisplayAlert crea un ContentDialog nativo (WinUI en Windows) que EXIGE
    /// ejecutarse en el hilo de UI. Si esto se llama desde un continuation que
    /// quedo en un hilo de threadpool (tipico despues de un catch de una
    /// operacion async que fallo), WinUI tira COMException 0x8001010E y
    /// mata el proceso entero en vez de mostrar el dialogo. MainThread.
    /// InvokeOnMainThreadAsync fuerza que esto siempre corra en el hilo
    /// correcto, sin importar de donde se llame.
    /// </summary>
    private Task ShowAlertSafeAsync(string title, string message)
    {
        return MainThread.InvokeOnMainThreadAsync(() => DisplayAlert(title, message, "OK"));
    }
}
