namespace CeleCamIp.App.Services;

/// <summary>
/// Configuracion de la app. Por ahora la URL del servidor es fija aca; mas
/// adelante conviene moverla a una pantalla de "Configuracion" para que el
/// usuario pueda apuntar a su propio VPS sin recompilar.
/// </summary>
public static class AppConfig
{
    /// <summary>
    /// URL base del Server (sin sufijo de ruta), usada para armar tanto el
    /// hub de SignalR como la URL del reproductor embebido (WebView).
    ///
    /// Apunta siempre al Server publico en MonsterASP, en las dos plataformas:
    /// el Gateway tambien se conecta ahi (ver Gateway/appsettings.json), asi que
    /// para probar de punta a punta no hace falta nada corriendo en localhost.
    /// Si en algun momento hace falta volver a apuntar a un Server local (Windows,
    /// mientras se prueba con el Gateway en la misma PC), cambiar este valor
    /// directamente por "http://localhost:5151".
    /// </summary>
    public static string ServerBaseUrl => "http://camarasip.runasp.net";

    /// <summary>URL del hub del Server (SignalR).</summary>
    public static string ServerHubUrl => $"{ServerBaseUrl}/hubs/gateway";

    /// <summary>
    /// URL de la pagina embebida (WebView) que reproduce UNA camara via WebRTC.
    /// houseId/cameraId/token van por query string; los arma CameraPlayerPage.
    /// </summary>
    public static string EmbedPlayerBaseUrl => $"{ServerBaseUrl}/embed/player.html";

    /// <summary>
    /// Clave compartida para autenticarse contra el Hub (debe coincidir con
    /// Auth:ApiKey del Server). Vive en AppSecrets.cs (archivo separado, no
    /// deberia versionarse en texto plano) para no mezclar el secreto real
    /// con el resto de la configuracion de la app.
    /// </summary>
    public static string ApiKey => AppSecrets.ApiKey;
}