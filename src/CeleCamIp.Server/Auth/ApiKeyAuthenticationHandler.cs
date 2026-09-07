using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CeleCamIp.Server.Auth;

public class ApiKeyAuthOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Autenticacion simple por API key compartida (NO es un sistema de usuarios,
/// no hay login ni cuentas). Sirve solo para que el Hub no quede totalmente
/// abierto a cualquiera en internet: unicamente clientes (Gateway y App) que
/// conozcan la clave configurada en Auth:ApiKey pueden conectarse.
///
/// El cliente .NET de SignalR (HubConnection) manda el AccessTokenProvider
/// como header estandar "Authorization: Bearer &lt;token&gt;" para las
/// llamadas HTTP normales (negotiate incluido). El query string
/// "access_token" es una convencion aparte que solo usa el cliente JS de
/// navegador, por las limitaciones de WebSocket en el browser (no puede
/// setear headers custom en el handshake). Por eso hay que revisar las
/// tres formas: query string, header Authorization Bearer, y X-Api-Key
/// por si en el futuro se llama desde algo que prefiera ese header.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private readonly string _expectedKey;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _expectedKey = configuration["Auth:ApiKey"] ?? string.Empty;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var providedKey = Request.Query["access_token"].FirstOrDefault()
            ?? Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey))
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                providedKey = authHeader["Bearer ".Length..];
            }
        }

        if (string.IsNullOrEmpty(_expectedKey))
        {
            // Sin key configurada del lado del servidor no hay nada contra que
            // comparar - se rechaza todo para no dejar el Hub abierto por un
            // descuido de configuracion.
            return Task.FromResult(AuthenticateResult.Fail(
                "El servidor no tiene Auth:ApiKey configurada."));
        }

        if (string.IsNullOrEmpty(providedKey) || providedKey != _expectedKey)
        {
            return Task.FromResult(AuthenticateResult.Fail("API key invalida o ausente."));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "celecamip-client") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
