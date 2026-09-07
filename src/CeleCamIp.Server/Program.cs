using CeleCamIp.Server.Auth;
using CeleCamIp.Server.Embed;
using CeleCamIp.Server.Hubs;
using CeleCamIp.Server.Services;
using CeleCamIp.Shared.WebRtc;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IHouseRegistry, HouseRegistry>();

// Autenticacion simple por API key compartida - ver Auth/ApiKeyAuthenticationHandler.cs
// para el porque. Sin esto, el Hub queda abierto a cualquiera en internet.
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// NOTA: se removio UseHttpsRedirection() a proposito. El Server escucha en
// http://0.0.0.0:5151 (sin TLS) para que clientes en la LAN (la app movil,
// el Gateway) puedan conectar directo por IP sin certificado. Si se agrega
// TLS mas adelante (ej. detras de un reverse proxy en el VPS), hay que
// reintroducir el redirect apuntando al host/puerto publico real, no a
// "localhost", que en el celular resuelve a si mismo y rompe la conexion.

// Sirve wwwroot/index.html: un viewer WebRTC minimo en HTML/JS puro, para probar
// el puente RTSP->WebRTC de punta a punta sin depender de la app MAUI.
//
// IMPORTANTE: esto SOLO se sirve en Development. Pedir la API key con un
// prompt() del lado del navegador es cosmetico (cualquiera con el link
// llega igual a la pagina, ve el codigo fuente, y puede intentar adivinar
// o probar claves). La proteccion real es no exponer esta pagina de debug
// en produccion: sin UseDefaultFiles/UseStaticFiles mapeados, "/" en el
// sitio publico devuelve 404 y no hay forma de llegar al viewer ni de
// listar casas/camaras desde ahi.
if (app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();

// Endpoint al que se conectan los Gateways de cada casa (conexion SALIENTE
// desde la casa hacia aca). Esto es lo que resuelve CGNAT sin abrir puertos.
app.MapHub<GatewayHub>("/hubs/gateway");

// Config de ICE servers (STUN/TURN) que necesita el LADO VIEWER (el WebView
// de la app) para armar su propia RTCPeerConnection. Es la misma idea que
// "IceServers" en el appsettings del Gateway, pero del lado Server: cada
// extremo de la conexion WebRTC arma su propia config ICE. Protegido con la
// misma API key que el Hub para no regalar las credenciales de TURN a
// cualquiera que encuentre la URL.
app.MapGet("/api/ice-servers", (IConfiguration config) =>
{
    var servers = config.GetSection("IceServers").Get<List<IceServerDto>>() ?? new();
    return Results.Ok(servers);
}).RequireAuthorization();

// Pagina embebida (WebView de la app) que reproduce UNA camara puntual via
// WebRTC. A diferencia de wwwroot/index.html, esta SI se sirve siempre (la
// necesita la app en produccion, no es solo una herramienta de debug). No
// requiere autenticacion en si misma (no tiene secretos adentro); la
// autenticacion la hace el JS de la pagina al llamar al Hub y a
// /api/ice-servers, usando el token que le paso la app por query string.
app.MapGet("/embed/player.html", () => Results.Content(PlayerPage.Html, "text/html"));

app.Run();