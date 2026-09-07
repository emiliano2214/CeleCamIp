namespace CeleCamIp.App.Services;

/// <summary>
/// Secretos de la app que NO deberian versionarse en texto plano.
/// Copiar este archivo como "AppSecrets.cs" (sin ".example") y completar
/// con el valor real. AppSecrets.cs deberia agregarse a .gitignore si el
/// repo usa control de versiones.
/// </summary>
public static class AppSecrets
{
    /// <summary>Debe coincidir con Auth:ApiKey configurado en el Server.</summary>
    public const string ApiKey = "Y8MiXX0GUKUNqfW1yhP0TBBUsjE9n+4/NwW8q1rG/jE=";
}
