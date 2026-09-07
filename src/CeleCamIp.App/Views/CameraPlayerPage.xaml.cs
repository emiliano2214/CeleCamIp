using CeleCamIp.App.Services;
using CeleCamIp.Shared.Cameras;

namespace CeleCamIp.App.Views;

/// <summary>
/// Reproduce una camara puntual dentro de un WebView cargando la pagina
/// /embed/player.html del Server (WebRTC nativo del navegador embebido).
///
/// OJO - por que WebRTC y no RTSP directo (como en la primera version de
/// esta pantalla, con LibVLCSharp): camera.StreamUrl es una URL RTSP de la
/// LAN de la casa (ej. rtsp://192.168.0.25:554/...). Reproducirla directo
/// con un reproductor nativo (LibVLC) SOLO funciona si el celular esta en
/// la misma red que las camaras. En cuanto el celular cambia de red (otro
/// Wi-Fi, datos moviles), esa IP deja de ser alcanzable sin importar que
/// tan bien configurado este el Server.
///
/// El puente RTSP->WebRTC que corre en el Gateway (WebRtcCameraSession) +
/// la senializacion relayada por el Server (GatewayHub) SI atraviesan redes
/// distintas (usan ICE con STUN/TURN, como cualquier videollamada). Por eso
/// el video ahora viaja por ahi, y esta pantalla es solo un WebView que
/// carga la pagina que hace ese trabajo en JS (la misma logica ya probada
/// en wwwroot/index.html, pero reducida a una sola camara).
///
/// El boton "Abrir en VLC" se mantiene como via alternativa MANUAL (RTSP
/// directo) para cuando el usuario sabe que esta en la misma red que las
/// camaras y prefiere el camino mas liviano/con menos latencia.
/// </summary>
[QueryProperty(nameof(Camera), "Camera")]
[QueryProperty(nameof(HouseId), "HouseId")]
public partial class CameraPlayerPage : ContentPage
{
    private string _cameraTitle = "Camara";
    public string CameraTitle
    {
        get => _cameraTitle;
        private set
        {
            _cameraTitle = value;
            OnPropertyChanged(nameof(CameraTitle));
        }
    }

    private string _cameraSubtitle = string.Empty;
    public string CameraSubtitle
    {
        get => _cameraSubtitle;
        private set
        {
            _cameraSubtitle = value;
            OnPropertyChanged(nameof(CameraSubtitle));
        }
    }

    private CameraDescriptor? _camera;
    public CameraDescriptor? Camera
    {
        get => _camera;
        set
        {
            _camera = value;
            ApplyCamera(value);
        }
    }

    /// <summary>Id de la casa duena de la camara. Lo necesita el player para armar RequestStream(houseId, cameraId).</summary>
    public string? HouseId { get; set; }

    public CameraPlayerPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    private void ApplyCamera(CameraDescriptor? camera)
    {
        if (camera is null)
        {
            return;
        }

        Title = camera.Name;
        CameraTitle = camera.Name;
        CameraSubtitle = string.IsNullOrWhiteSpace(camera.AdapterUsed)
            ? camera.IpAddress
            : $"{camera.IpAddress} - {camera.AdapterUsed}";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        StartPlayback();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // "about:blank" descarga la pagina actual (y con ella el JS en
        // ejecucion), lo que cierra la RTCPeerConnection y la conexion
        // SignalR del lado del WebView. Sin esto, el video seguiria
        // consumiendo datos/batería en segundo plano al salir de la pantalla.
        playerWebView.Source = "about:blank";
    }

    private void StartPlayback()
    {
        if (_camera is null || string.IsNullOrWhiteSpace(HouseId))
        {
            return;
        }

        loadingIndicator.IsRunning = true;
        loadingIndicator.IsVisible = true;

        var url =
            $"{AppConfig.EmbedPlayerBaseUrl}" +
            $"?houseId={Uri.EscapeDataString(HouseId)}" +
            $"&cameraId={Uri.EscapeDataString(_camera.Id)}" +
            $"&token={Uri.EscapeDataString(AppConfig.ApiKey)}";

        playerWebView.Source = new UrlWebViewSource { Url = url };
    }

    /// <summary>
    /// Puente JS -> nativo: la pagina embebida (PlayerPage.cs, del lado
    /// Server) reporta su estado navegando a "app://status?state=...&message=...".
    /// Ese esquema no es una URL real, asi que se cancela SIEMPRE la
    /// navegacion (e.Cancel = true) antes de que el WebView intente
    /// resolverla; solo se usa como forma de mandar el evento. Cualquier
    /// otra navegacion (la carga inicial de la pagina, redirecciones, etc)
    /// se deja pasar normalmente.
    /// </summary>
    private void OnPlayerWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!e.Url.StartsWith("app://status", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;

        var query = ParseQuery(new Uri(e.Url).Query);
        query.TryGetValue("state", out var state);
        query.TryGetValue("message", out var message);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (state)
            {
                case "connecting":
                    loadingIndicator.IsRunning = true;
                    loadingIndicator.IsVisible = true;
                    break;

                case "connected":
                    loadingIndicator.IsRunning = false;
                    loadingIndicator.IsVisible = false;
                    break;

                case "error":
                    loadingIndicator.IsRunning = false;
                    loadingIndicator.IsVisible = false;
                    await ShowAlertSafeAsync(
                        "Error de reproduccion",
                        string.IsNullOrWhiteSpace(message)
                            ? "No se pudo conectar con la camara. Proba 'Abrir en VLC' si estas en la misma red."
                            : $"{message}\n\nProba 'Abrir en VLC' si estas en la misma red.");
                    break;
            }
        });
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();

        var trimmed = query.TrimStart('?');
        if (string.IsNullOrEmpty(trimmed))
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    // OJO: al girar el celular a horizontal, ademas de que MAUI recalcula el
    // tamano del Grid (y con el el WebView, que ya escala solo porque esta
    // en HorizontalOptions/VerticalOptions=Fill dentro de una celda "*"), queremos
    // que la camara ocupe TODA la pantalla: ocultamos el subtitulo, el boton y
    // el padding/espaciado del Grid raiz, y escondemos la barra de navegacion del
    // Shell (titulo + flecha de volver) para no perder espacio util.
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var isLandscape = width > height;
        ApplyOrientationLayout(isLandscape);
    }

    private void ApplyOrientationLayout(bool isLandscape)
    {
        subtitleLabel.IsVisible = !isLandscape;
        openInVlcButton.IsVisible = !isLandscape;
        rootGrid.Padding = isLandscape ? new Thickness(0) : new Thickness(16);
        rootGrid.RowSpacing = isLandscape ? 0 : 12;

        Shell.SetNavBarIsVisible(this, !isLandscape);
    }

    private async void OnOpenInVlcClicked(object? sender, EventArgs e)
    {
        if (_camera is null || string.IsNullOrWhiteSpace(_camera.StreamUrl))
        {
            await ShowAlertSafeAsync(
                "Sin URL directa",
                "Esta camara todavia no tiene una URL RTSP resuelta.");
            return;
        }

        if (!VlcLauncher.TryOpen(_camera.StreamUrl, out var error))
        {
            await ShowAlertSafeAsync(
                "No se pudo abrir VLC",
                error ?? "Instala VLC o usa un dispositivo con una app compatible con RTSP.");
        }
    }

    private Task ShowAlertSafeAsync(string title, string message)
    {
        return MainThread.InvokeOnMainThreadAsync(() => DisplayAlert(title, message, "OK"));
    }
}