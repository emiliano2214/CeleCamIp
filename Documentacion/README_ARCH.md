# CeleCamIp — Arquitectura en una página

(Ver `ARQUITECTURA.md` para el detalle completo. Esto es un mapa rápido.)

## Los 5 proyectos y su rol

| Proyecto | Tipo de SDK | Target | Rol |
|---|---|---|---|
| `CeleCamIp.Shared` | `Microsoft.NET.Sdk` | `net9.0` | Modelos y contratos compartidos. Sin dependencias externas. Referenciado por los otros 4. |
| `CeleCamIp.Server` | `Microsoft.NET.Sdk.Web` | `net9.0` | ASP.NET Core. Hub SignalR (`GatewayHub`), registro en memoria de casas (`HouseRegistry`), auth por API key, endpoint de ICE servers, página embebida del reproductor. |
| `CeleCamIp.Gateway` | `Microsoft.NET.Sdk.Worker` | `net9.0` | `BackgroundService` que corre en la LAN de la casa. Detecta cámaras RTSP, mantiene conexión SignalR saliente al Server, arma sesiones WebRTC bajo demanda. |
| `CeleCamIp.App` | `Microsoft.NET.Sdk` (MAUI) | `net9.0-android`, `net9.0-windows10.0.19041.0` | App móvil/desktop. Lista casas → cámaras → reproductor (WebView con WebRTC). |
| `CeleCamIp.CameraDiagnostic` | `Microsoft.NET.Sdk` (consola) | `net9.0` | Herramienta de consola para probar si una IP responde RTSP y con qué ruta, sin levantar todo el sistema. |
| `tools/ViewerTestClient` | `Microsoft.NET.Sdk` (consola) | — | Cliente de consola para probar el Hub a mano. **No está incluido en `CeleCamIp.sln`.** |

## Por qué "el Gateway llama al Server" y no al revés

Las cámaras están en la LAN de una casa doméstica, típicamente detrás de
CGNAT del ISP (sin IP pública real) y sin acceso para abrir puertos en el
router. La única forma confiable de que un cliente externo (la app, en
cualquier red) llegue a esas cámaras es que **la propia casa inicie la
conexión saliente** hacia un servidor con IP pública real (hoy, un hosting
compartido en MonsterASP). Esto es el mismo patrón que usan servicios de
cámaras de consumo (Ring, Reolink, etc.) y evita configuración de red del
lado del usuario.

## El "doble rol" del `GatewayHub`

Un único Hub de SignalR (`/hubs/gateway`) atiende a dos tipos de clientes
distintos, diferenciados por qué métodos invocan:

- **Gateway (casa):** `RegisterHouse`, `ReportCameras`, y del lado de
  señalización WebRTC: `SendOffer`, `SendIceCandidateToViewer`,
  `ReportStreamError`.
- **App/Viewer:** `JoinAsViewer` (trae el snapshot inicial), y del lado de
  señalización: `RequestStream`, `SendAnswer`, `SendIceCandidateToGateway`.

El Server **no entiende el contenido SDP**: solo enruta mensajes por
`ConnectionId`, igual que un servidor de señalización WebRTC clásico.

## El puente RTSP → WebRTC (la pieza más delicada del sistema)

```
Cámara IP (RTSP, LAN casa)
        │  TCP 554, RTSP DESCRIBE/SETUP/PLAY (transporte UDP forzado)
        ▼
FFmpeg (proceso externo, -c:v copy, sin recodificar)
        │  H264 Annex-B por stdout (pipe:1)
        ▼
FFmpegProcessSource (parser de NALs hecho a mano, en Gateway)
        │  agrupa NALs en access units, calcula timestamps RTP reales
        ▼
WebRtcCameraSession (SIPSorcery RTCPeerConnection)
        │  SendVideo(durationRtpUnits, sample) — un peer connection por viewer×cámara
        ▼
Internet (ICE: host / STUN / TURN — ExpressTURN free tier)
        │
        ▼
WebView de la App (RTCPeerConnection nativo del navegador embebido)
        │  carga /embed/player.html, señalización vía el mismo Hub SignalR
        ▼
<video> HTML reproduciendo en vivo
```

Este camino existe porque reproducir el RTSP **directo** con un reproductor
nativo (LibVLC) en la app solo funciona si el celular está en la misma LAN
que las cámaras. El puente WebRTC sí atraviesa redes distintas (como
cualquier videollamada), a costa de mucha más complejidad y de depender de
un TURN externo para los casos donde ICE no encuentra un camino directo.

## Autenticación (todo el sistema comparte una única clave)

No hay usuarios ni cuentas. Una **API key compartida** (`Auth:ApiKey`) se
exige para:
- Conectarse al Hub (`GatewayHub`, gateway y app por igual).
- Llamar a `GET /api/ice-servers`.

Ver `SECURITY.md` para los riesgos concretos de este modelo (incluye que la
clave real está versionada en git).

## Dónde corre cada cosa hoy

| Componente | Entorno real |
|---|---|
| Server | Hosting compartido MonsterASP (`camarasip.runasp.net`), Windows, IIS/ANCM |
| Gateway | Pensado para Docker con `network_mode: host` en una Raspberry Pi (Linux) en la LAN de la casa; también corre standalone en Windows durante desarrollo |
| App | APK de sideload para Android; build de depuración para Windows |
