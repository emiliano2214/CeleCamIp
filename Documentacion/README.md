# CeleCamIp — Documentación técnica

> Última actualización de esta documentación: 08-09-2026, generada mediante
> auditoría directa del código fuente en
> `C:\Users\emiab\source\repos\CeleCamIp` (commit base `02bc17c`, más cambios
> sin commitear presentes en el working tree al momento del análisis — ver
> `DECISIONES.md` § "Estado del repo al documentar").

## Qué es CeleCamIp

Sistema para ver, desde una app móvil (Android, y de forma parcial Windows),
las cámaras IP de una casa **sin abrir puertos en el router** y **sin
depender de una IP pública fija ni pelear con CGNAT**. La idea central: la
casa (el **Gateway**) es quien inicia la conexión hacia un servidor público
(el **Server**), nunca al revés. La app (el **Viewer**) se conecta a ese
mismo Server para descubrir casas/cámaras y pedir video en vivo.

```
┌─────────────┐        conexión saliente         ┌──────────────┐        conexión saliente        ┌─────────────┐
│   Gateway   │ ────────(SignalR/WSS)──────────► │    Server    │ ◄────────(SignalR/WSS)────────── │  App (MAUI) │
│  (casa,     │                                   │ (MonsterASP, │                                   │  (celular,  │
│  Docker/PC) │ ◄──── señalización WebRTC ──────► │  ASP.NET Core)│ ◄──── señalización WebRTC ──────► │  cualquier  │
│             │                                   │              │                                   │  red)       │
│  FFmpeg +   │ ═══════ video WebRTC (P2P/TURN) ══════════════════════════════════════════════════► │  WebView    │
│  RTSP cams  │                                                                                       │  (player)   │
└─────────────┘                                                                                       └─────────────┘
```

## Índice de la documentación

| Documento | Contenido |
|---|---|
| [`ALCANCE_FUNCIONAL.md`](./ALCANCE_FUNCIONAL.md) | Qué hace hoy el sistema, qué no, y los flujos de usuario reales (verificado contra código, no solo contra intención). |
| [`ARQUITECTURA.md`](./ARQUITECTURA.md) | Arquitectura completa: componentes, modelos de datos, protocolo de señalización WebRTC paso a paso, estructura de carpetas, stack tecnológico. |
| [`README_ARCH.md`](./README_ARCH.md) | Resumen visual de una página de la arquitectura, para orientarse rápido antes de entrar a `ARQUITECTURA.md`. |
| [`DECISIONES.md`](./DECISIONES.md) | Decisiones de diseño y su porqué, historial de bugs ya resueltos (muy relevante para no reintroducirlos), y discrepancias detectadas entre la documentación previa y el código real. |
| [`DEPLOYMENT.md`](./DEPLOYMENT.md) | Cómo compilar y desplegar cada componente: Server (MonsterASP), Gateway (Docker/Raspberry Pi), App (Android/Windows). |
| [`SECURITY.md`](./SECURITY.md) | Modelo de seguridad actual, hallazgos concretos (incluye secretos versionados en git) y recomendaciones priorizadas. |

## Componentes del repositorio

```
CeleCamIp/
├── CeleCamIp.sln
├── docker-compose.gateway.yml       # Despliegue del Gateway (host networking)
├── Dockerfile.gateway               # Build multi-stage del Gateway + ffmpeg
├── .env.gateway.example             # Plantilla de variables del Gateway
├── src/
│   ├── CeleCamIp.Shared/            # Modelos y contratos comunes (net9.0, sin dependencias externas)
│   ├── CeleCamIp.Server/            # ASP.NET Core Web — Hub SignalR + endpoints (net9.0)
│   ├── CeleCamIp.Gateway/           # Worker Service — corre en la casa (net9.0)
│   ├── CeleCamIp.App/               # App .NET MAUI — Android + Windows (net9.0-android / net9.0-windows)
│   └── CeleCamIp.CameraDiagnostic/  # Consola de diagnóstico RTSP standalone
└── tools/
    └── ViewerTestClient/            # Cliente de consola para probar el Hub sin la app (NO está en el .sln)
```

## Quick start (desarrollo local)

1. **Server** (requiere .NET 9 SDK):
   ```powershell
   cd src\CeleCamIp.Server
   dotnet run
   # Escucha en http://0.0.0.0:5151 (ver Properties/launchSettings.json)
   ```
   En `Development`, `wwwroot/index.html` queda accesible en `http://localhost:5151/`
   como viewer WebRTC mínimo de prueba (ver `ARQUITECTURA.md`).

2. **Gateway** (requiere FFmpeg 8.1.x instalado y en PATH, o junto al .exe):
   ```powershell
   cd src\CeleCamIp.Gateway
   dotnet run
   ```
   Editar `appsettings.json` / `appsettings.Development.json` con la URL del
   Hub, `House:CameraIps`, credenciales de cámara y `Auth:ApiKey` (debe
   coincidir con la del Server).

3. **App** (Windows, para probar rápido sin un celular):
   - Copiar `src/CeleCamIp.App/Services/AppSecrets.cs.example` a
     `AppSecrets.cs` y completar la API key.
   - Ajustar `AppConfig.ServerBaseUrl` si se apunta a un Server local
     (`http://localhost:5151`) en vez del público de MonsterASP.
   - Ejecutar el proyecto con el `TargetFramework`
     `net9.0-windows10.0.19041.0`.

Ver `DEPLOYMENT.md` para el flujo completo de publicación (MonsterASP,
Docker en Raspberry Pi, APK firmado para sideload).

## Estado funcional en una frase

**Funciona hoy:** descubrimiento de casas/cámaras por WAN, autenticación
básica del Hub, y — según el código actual del working tree — reproducción
de video vía el puente RTSP→WebRTC (WebView embebido) desde cualquier red.
**No funciona todavía / es frágil:** el puente WebRTC depende de un
`FFmpegProcessSource` casero con varios bugs de firmware de cámaras ya
parcheados puntualmente (ver `DECISIONES.md`), no hay reconexión automática
del video si se corta el ICE, y la app no tiene pantalla de configuración
(URLs y claves están hardcodeadas). Ver `ALCANCE_FUNCIONAL.md` para el
detalle completo, incluida una discrepancia detectada con la documentación
previa del proyecto.
