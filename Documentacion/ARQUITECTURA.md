# Arquitectura — CeleCamIp

## 1. Visión general

CeleCamIp resuelve un problema concreto: ver cámaras IP domésticas desde
fuera de la LAN de la casa, sin abrir puertos ni depender de IP pública, y
sin pagar un servicio de cámaras en la nube de terceros. La solución es un
sistema de 3 partes con un **servidor de señalización/registro** en el
medio, siguiendo el mismo patrón que WebRTC usa para videollamadas:

1. **Gateway** — proceso que corre en la red de la casa. Detecta cámaras,
   mantiene una conexión saliente permanente al Server, y arma un puente
   RTSP→WebRTC bajo demanda.
2. **Server** — servicio público (hoy en un hosting compartido) que actúa de
   *rendez-vous point*: sabe qué casas están online, qué cámaras tiene cada
   una, y relaya la señalización WebRTC (SDP/ICE) entre Gateway y App. No
   toca video en ningún momento.
3. **App** — cliente .NET MAUI (Android/Windows) que lista casas/cámaras y
   reproduce el video en un `WebView` que corre WebRTC nativo del navegador
   embebido.

Todo el código de dominio compartido (modelos, contratos de adaptadores de
cámara, DTOs de señalización) vive en **CeleCamIp.Shared**, referenciado por
los tres componentes anteriores.

## 2. Stack tecnológico

| Capa | Tecnología | Versión relevada |
|---|---|---|
| Runtime | .NET | 9.0 (todos los proyectos) |
| Server web | ASP.NET Core (`Microsoft.NET.Sdk.Web`) | net9.0 |
| Tiempo real | `Microsoft.AspNetCore.SignalR` / `.Client` | 10.0.11 (cliente) |
| Worker (Gateway) | `Microsoft.NET.Sdk.Worker` + `Microsoft.Extensions.Hosting` | 9.0.17 |
| WebRTC (servidor/Gateway) | `SIPSorcery` + `SIPSorceryMedia.FFmpeg` | 10.0.16 |
| Decodificación/mux de video | FFmpeg (binario **externo**, invocado como proceso) | 8.1.x (build "full" de Gyan.FFmpeg vía WinGet en dev) |
| WebRTC (cliente embebido) | API `RTCPeerConnection` nativa del WebView + `signalr.min.js` 8.0.0 (CDN) | — |
| App móvil/desktop | .NET MAUI + `CommunityToolkit.Maui` 11.2.0 | net9.0-android, net9.0-windows10.0.19041.0 |
| Reproductor nativo alternativo | `LibVLCSharp` / `LibVLCSharp.MAUI` 3.10.1, `VideoLAN.LibVLC.Android` 3.6.5 | solo Android + Windows (vía "Abrir en VLC") |
| Contenedores | Docker (`Dockerfile.gateway`, `docker-compose.gateway.yml`) | SDK/runtime `mcr.microsoft.com/dotnet` 9.0 |
| Hosting del Server | MonsterASP (hosting compartido Windows/IIS) | — |

## 3. Estructura de carpetas (código fuente, sin `bin`/`obj`/`.git`)

```
src/
  CeleCamIp.Shared/
    Cameras/
      CameraDescriptor.cs        # Resultado persistible de detección de una cámara
      CameraConnection.cs        # Resultado de una conexión exitosa (URL normalizada)
      HouseSnapshot.cs           # Estado de una casa tal como lo ve el Server
      ICameraAdapter.cs          # Contrato de estrategia de conexión (ONVIF/RTSP/propietario)
      CameraDetectionService.cs  # Orquesta la cascada de adaptadores
      Adapters/
        RtspCameraAdapter.cs     # Único adaptador implementado hoy
        RtspProbe.cs             # Cliente RTSP mínimo (TCP + DESCRIBE), sin dependencias
    WebRtc/
      SignalingModels.cs         # SdpDescriptionDto, IceCandidateDto, IceServerDto

  CeleCamIp.Server/
    Program.cs                  # Composition root: SignalR, auth, endpoints, pipeline HTTP
    Hubs/GatewayHub.cs           # Hub compartido Gateway/Viewer + relay de señalización
    Services/HouseRegistry.cs    # Registro en memoria (ConcurrentDictionary) de casas online
    Auth/ApiKeyAuthenticationHandler.cs
    Embed/PlayerPage.cs          # HTML/JS del reproductor embebido (string embebido en C#)
    wwwroot/index.html           # Viewer de debug (solo se sirve en Development)
    appsettings*.json

  CeleCamIp.Gateway/
    Program.cs                   # Inicializa FFmpeg (SIPSorceryMedia), arma el Host, DI
    Services/
      GatewayConnectionService.cs # BackgroundService: conexión SignalR + detección + señalización
      WebRtcCameraSession.cs      # Una RTCPeerConnection por (viewer, cámara)
      FFmpegProcessSource.cs      # Fuente de video: FFmpeg como proceso + parser de NALs a mano
    appsettings*.json

  CeleCamIp.App/
    MauiProgram.cs                # DI, fonts, LibVLC solo en Android
    AppShell.xaml(.cs)            # Rutas: HousesPage (raíz), CamerasPage, CameraPlayerPage
    Services/
      AppConfig.cs                 # URLs del Server (hardcodeadas)
      AppSecrets.cs / .cs.example  # API key (hardcodeada, ver SECURITY.md)
      ServerViewerService.cs       # Cliente SignalR del lado viewer (singleton)
      VlcLauncher.cs                # Abre VLC de escritorio (solo Windows)
    ViewModels/HousesViewModel.cs
    Views/
      HousesPage.xaml(.cs)          # Lista de casas
      CamerasPage.xaml(.cs)         # Lista de cámaras de una casa
      CameraPlayerPage.xaml(.cs)    # WebView → /embed/player.html
    Converters/StatusConverters.cs

  CeleCamIp.CameraDiagnostic/
    Program.cs                     # Consola: prueba RtspCameraAdapter contra una IP a mano

tools/
  ViewerTestClient/
    Program.cs                     # Cliente SignalR de consola para inspeccionar eventos del Hub
```

## 4. Modelo de dominio (`CeleCamIp.Shared`)

### `CameraDescriptor`
Resultado de detectar/configurar una cámara. Se detecta **una sola vez** (al
agregarla) y se persiste (`AdapterUsed`) para no re-detectar en cada
conexión. Campos: `Id`, `Name`, `IpAddress`, `Manufacturer?`, `Model?`,
`OnvifSupported`, `RtspSupported`, `StreamUrl?`, `AdapterUsed?`,
`Username?`, `Password?`.

> Nota: `OnvifSupported`/`Manufacturer`/`Model` existen en el modelo pero
> **no hay ningún adaptador ONVIF implementado todavía** (ver §6 y
> `DECISIONES.md`); solo se completan si en el futuro se agrega ese
> adaptador.

### `CameraConnection`
Resultado de una conexión exitosa: `CameraId`, `NormalizedStreamUrl`,
`SupportsAudio`, `SupportsPtz`. Es lo que el resto del sistema (el puente
WebRTC) consume sin saber qué protocolo hay detrás.

### `HouseSnapshot`
`HouseId`, `IsOnline`, `Cameras` (lista de `CameraDescriptor`). Es el DTO
que ve la app: no sabe nada de SignalR ni de cómo se arma este estado.

### `ICameraAdapter` + `CameraDetectionService`
Patrón *Chain of Responsibility*: `CameraDetectionService` recorre los
adaptadores inyectados en orden (`Onvif → Rtsp → propietarios`, según el
comentario del código — aunque hoy solo `Rtsp` está registrado) y el primero
que responda `CanHandleAsync == true` gana. Un adaptador que lanza excepción
al probar **no frena la cascada** (se atrapa y se sigue con el próximo).

### `RtspCameraAdapter` + `RtspProbe`
- `RtspProbe` es un cliente RTSP mínimo hecho a mano sobre `TcpClient`
  (sin librerías externas): abre el socket, manda `DESCRIBE` y lee la
  respuesta. Considera éxito tanto `200` (describe OK) como `401` (pide
  auth, pero confirma que hay un servidor RTSP real). **Solo soporta Basic
  Auth** (RFC 2326); cámaras que exigen Digest no son detectadas.
- `RtspCameraAdapter.ConnectAsync` prueba primero la `StreamUrl` ya
  conocida y, si falla, itera un set de rutas comunes por fabricante
  (`/stream1`, `/h264/ch1/main/av_stream` [Hikvision-like],
  `/cam/realmonitor?channel=1&subtype=0` [Dahua], `/videoMain`, `/live/ch0`,
  `/11`, `/`) hasta que una responda.

### DTOs de señalización WebRTC (`WebRtc/SignalingModels.cs`)
`SdpDescriptionDto(Type, Sdp)`, `IceCandidateDto(Candidate, SdpMid,
SdpMLineIndex, UsernameFragment)`, `IceServerDto(Urls, Username?,
Credential?)`. Son un espejo directo de los tipos de SIPSorcery/WebRTC del
navegador para poder viajar por SignalR sin transformación.

## 5. Server (`CeleCamIp.Server`)

### Pipeline HTTP (`Program.cs`)
- `AddSignalR()`, `AddSingleton<IHouseRegistry, HouseRegistry>()`.
- Auth por esquema custom `"ApiKey"` (ver `ApiKeyAuthenticationHandler`),
  única scheme registrada.
- **Sin `UseHttpsRedirection()`** — deliberado: el Server escucha en
  `http://0.0.0.0:5151` sin TLS para que clientes en LAN puedan conectar
  directo por IP sin certificado. En producción (MonsterASP), TLS lo maneja
  la capa de hosting compartido por delante.
- `wwwroot/index.html` (viewer de debug) **solo se sirve si
  `Environment.IsDevelopment()`**: sin `UseDefaultFiles`/`UseStaticFiles`
  mapeados en producción, `/` devuelve 404.
- Rutas mapeadas:
  - `POST/GET` implícito de SignalR en `/hubs/gateway` → `GatewayHub`.
  - `GET /api/ice-servers` → devuelve `IceServerDto[]` desde
    `IceServers` en config. Requiere autorización (`RequireAuthorization()`).
  - `GET /embed/player.html` → sirve el HTML embebido de `PlayerPage.Html`
    (siempre, en cualquier entorno — la app lo necesita en producción). No
    requiere auth por sí misma; el JS de la página autentica sus propias
    llamadas (Hub e `/api/ice-servers`) con el `token` que le pasa la query
    string.

### `GatewayHub` (`Hubs/GatewayHub.cs`)
`[Authorize(AuthenticationSchemes = "ApiKey")]`. Un único Hub para dos tipos
de cliente:

**Ciclo de vida de una casa:**
- `RegisterHouse(houseId)` — lo llama el Gateway al conectar. Marca la casa
  online en el registro y notifica a todos los viewers (`HouseOnline`).
- `OnDisconnectedAsync` — si el `ConnectionId` que se cae correspondía a una
  casa, la marca offline y notifica (`HouseOffline`). Hay protección
  explícita contra condición de carrera: si dos Gateways de la misma casa
  llegaron a conectar (proceso viejo colgado + uno nuevo), al caerse el
  viejo no se pisa el registro del nuevo.
- `ReportCameras(houseId, cameras)` — lo llama el Gateway tras correr la
  detección local. Notifica `CamerasUpdated` a los viewers.
- `JoinAsViewer()` — lo llama la app. Se suma al grupo `"viewers"` y
  devuelve de una el snapshot completo (`List<HouseSnapshot>`) para no
  esperar al próximo evento.

**Señalización WebRTC (relay ciego por `ConnectionId`):**

```
Viewer                          Server (Hub)                        Gateway
  │  RequestStream(houseId,camId)  │                                    │
  ├────────────────────────────────►                                    │
  │                                 │  StreamRequested(viewerConnId,camId)
  │                                 ├───────────────────────────────────►│
  │                                 │                                    │ arma RTCPeerConnection
  │                                 │                                    │ + oferta SDP
  │                                 │   SendOffer(viewerConnId,camId,offer)
  │                                 │◄───────────────────────────────────┤
  │      ReceiveOffer(camId,offer) │                                    │
  │◄────────────────────────────────┤                                    │
  │ setRemoteDescription, createAnswer                                   │
  │  SendAnswer(houseId,camId,answer)                                    │
  ├────────────────────────────────►                                    │
  │                                 │  ReceiveAnswer(viewerConnId,camId,answer)
  │                                 ├───────────────────────────────────►│
  │  SendIceCandidateTo{Viewer,Gateway}(…) ◄──── trickle ICE, ambos lados, mientras dure la conexión ────►│
```

Métodos: `RequestStream`, `SendOffer`, `SendAnswer`,
`SendIceCandidateToViewer`, `SendIceCandidateToGateway`,
`ReportStreamError`. El Server nunca inspecciona el contenido SDP; solo
resuelve a qué `ConnectionId` reenviar según `HouseRegistry.GetConnectionId`.

### `HouseRegistry`
Implementación 100% en memoria con `ConcurrentDictionary` (no hay
persistencia — si el proceso del Server se reinicia, el estado se
reconstruye solo apenas los Gateways reconectan, gracias a su reintento
automático). Tres diccionarios: `connectionByHouse`, `houseByConnection`
(inverso, para resolver `OnDisconnectedAsync` en O(1)), `camerasByHouse`.

### `ApiKeyAuthenticationHandler`
Busca la key, en este orden: query string `access_token` → header
`X-Api-Key` → header `Authorization: Bearer <token>`. Esto cubre tanto al
cliente `HubConnection` de .NET (que manda `Authorization: Bearer`) como al
cliente JS de navegador (que solo puede mandar `access_token` en la query,
por limitaciones de WebSocket en browser). Si `Auth:ApiKey` no está
configurada en el servidor, **se rechaza todo** (fail-closed), para no
dejar el Hub abierto por un descuido de configuración.

### `Embed/PlayerPage.cs`
HTML/JS embebido como *raw string literal* en C# (no un archivo `.html`
separado — se sirve tal cual desde memoria). Es un reproductor WebRTC
mínimo: lee `houseId`/`cameraId`/`token` de la query string, pide
`/api/ice-servers`, conecta al Hub, maneja `ReceiveOffer` /
`ReceiveIceCandidate` / `StreamError`, y expone un puente
JS→nativo (`notifyNativeStatus`) navegando a URLs `app://status?...` que
`CameraPlayerPage.xaml.cs` intercepta del lado MAUI.

## 6. Gateway (`CeleCamIp.Gateway`)

### `Program.cs`
1. Inicializa `FFmpegInit.EnsureBinariesRegistered()` de
   `SIPSorceryMedia.FFmpeg` — **aunque el pipeline de video real hoy usa
   `FFmpegProcessSource` (proceso externo), no este binding**. Este paso
   queda porque el binding se sigue usando en `DiagnoseAsync()` para probar
   `FFmpegFileSource` como chequeo de salud. Si falla, no frena el arranque,
   solo lo loguea (ver comentario extenso en el `.csproj` sobre por qué las
   DLLs nativas de libav deben copiarse a mano — no vienen con el NuGet).
2. Arma un `Host` genérico (Worker Service), registra `GatewayOptions`
   leyendo `Server`, `House`, `IceServers`, `Auth:ApiKey` de la
   configuración.
3. Registra `RtspCameraAdapter` como único `ICameraAdapter`,
   `CameraDetectionService`, y `GatewayConnectionService` como
   `HostedService` único.

### `GatewayConnectionService` (el corazón del Gateway)
`BackgroundService` que:
- Mantiene una `HubConnection` hacia `Server:HubUrl`, con
  `AccessTokenProvider` devolviendo la API key, y reconexión automática con
  backoff (`0s, 2s, 5s, 10s, 30s`). Si `Closed` dispara (falla definitiva),
  reintenta manualmente cada 5s en un loop propio (doble capa de resiliencia).
- Al conectar/reconectar, llama `RegisterHouse(houseId)` y dispara
  `DetectAndReportCamerasAsync` **sin bloquear** el registro (la detección
  puede tardar varios segundos por IP).
- `DetectAndReportCamerasAsync` corre `CameraDetectionService.DetectAsync`
  contra cada IP de `House:CameraIps`, resuelve la URL normalizada con
  `adapter.ConnectAsync`, guarda el resultado en `_knownCameras`
  (diccionario en memoria, clave = IP) y reporta todo junto con
  `ReportCameras`.
- Maneja la señalización entrante: `StreamRequested` → arma una
  `WebRtcCameraSession` nueva (clave `"{viewerConnectionId}:{cameraId}"` en
  `_activeSessions`) y devuelve la oferta con `SendOffer`. `ReceiveAnswer` y
  `ReceiveIceCandidate` se enrutan a la sesión activa correspondiente.
- `DiagnoseAsync()` — método de diagnóstico manual (no expuesto por ningún
  endpoint todavía) que loguea estado de SignalR, cámaras conocidas,
  sesiones activas, y prueba cada `StreamUrl` con `FFmpegFileSource` del
  binding de SIPSorceryMedia.

### `WebRtcCameraSession`
Una instancia por par (viewer, cámara). Responsabilidades:
- Crea el formato de video **H264 manualmente** (no autodetectado) con
  `packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1`,
  payload type dinámico `96`. Ver `DECISIONES.md` sobre por qué esto es
  necesario (el navegador rechaza el SDP sin esos parámetros fmtp).
- Arma la `RTCPeerConnection` con los ICE servers de configuración, agrega
  un `MediaStreamTrack` `SendOnly`.
- Arranca `FFmpegProcessSource.StartVideo()` **en paralelo a la negociación
  ICE** (no espera a `connected`), para que FFmpeg ya tenga frames listos
  cuando el ICE cierre.
- Cada muestra H264 que entrega `FFmpegProcessSource` se manda directo con
  `peerConnection.SendVideo(durationRtpUnits, sample)`.
- Expone una batería de eventos para observabilidad: `OnLocalIceCandidate`,
  `OnConnectionStateChanged`, `OnVideoStartFailed`, `OnFrameEncoded`,
  `OnFirstFrameElapsed`, `OnClosedWithElapsed`, `OnBitrateUpdated` (calculado
  cada 50 frames), `OnFrameSent`, `OnFFmpegError`.
- `DisposeAsync` cierra video, libera la fuente y cierra la
  `RTCPeerConnection`, cada paso con su propio try/catch (no deja que un
  fallo de limpieza tumbe el resto).

### `FFmpegProcessSource` — el componente más complejo del sistema
En vez de usar el binding nativo `SIPSorceryMedia.FFmpeg`, este componente
**invoca `ffmpeg.exe`/`ffmpeg` como proceso externo** y parsea manualmente
su salida H264 Annex-B por `stdout`. Argumentos clave:

```
-fflags nobuffer -flags low_delay -probesize 65536 -analyzeduration 300000
-err_detect ignore_err -rtsp_transport udp -i "<rtsp-url>"
-map 0:v:0 -c:v copy -an -f h264 pipe:1
```

- `-c:v copy`: no recodifica — reenvía el H264 tal cual viene de la cámara
  (menor latencia y CPU, pero significa que el `profile-level-id` real
  puede no coincidir con el anunciado en el SDP; de ahí
  `level-asymmetry-allowed=1`).
- `-rtsp_transport udp`: **decisión deliberada**, ver `DECISIONES.md` — con
  TCP interleaved, algunas cámaras completan el handshake RTSP pero nunca
  entregan datos.
- `-probesize 65536 -analyzeduration 300000`: valores intermedios elegidos
  tras dos iteraciones de bugs (ver `DECISIONES.md` — historial de bugs 4 y
  5), para no pagar los 5-7s de latencia del default de FFmpeg pero sin ser
  tan agresivo como para que falle con cámaras que mandan SPS/PPS in-band.

El parser de bajo nivel (`ProcessPendingBuffer`, `FindStartCode`,
`AppendToPendingBuffer`) reconstruye NALs Annex-B (delimitados por start
codes `0x000001`/`0x00000001`) desde un buffer circular contiguo (no una
`List<byte>` con `RemoveRange`, que era el origen de un bug de latencia
creciente — ver `DECISIONES.md`), agrupa NALs no-VCL (SPS/PPS/SEI) con el
NAL de slice (`tipo 1` o `5`) que cierra el *access unit*, y calcula la
duración RTP real a partir del tiempo transcurrido entre *access units*
(clamped entre 1/60s y 1s como defensa ante huecos anómalos).

## 7. App (`CeleCamIp.App`)

### Navegación (Shell)
`AppShell` registra rutas para `CamerasPage` y `CameraPlayerPage`;
`HousesPage` es la página raíz (definida en el `.xaml` del Shell, no vista
en este repo como archivo separado más allá de su code-behind).

```
HousesPage  →  CamerasPage  →  CameraPlayerPage
 (lista de     (lista de       (WebView con
  casas)        cámaras de     /embed/player.html)
                una casa)
```

Los parámetros de navegación pasan objetos completos por
`Shell.Current.GoToAsync(route, IDictionary<string,object>)` con
`[QueryProperty]` (`House` → `CamerasPage`, `Camera`+`HouseId` →
`CameraPlayerPage`) — MAUI serializa/pasa la referencia directamente, sin
pasar por query string real.

### `ServerViewerService` (singleton)
Encapsula la `HubConnection` del lado viewer. Expone
`ObservableCollection<HouseSnapshot> Houses` para bindear directo en la UI,
y un enum `ServerConnectionState` con evento `StateChanged`. Al conectar,
llama `JoinAsViewer` y puebla `Houses`; después escucha `HouseOnline` /
`HouseOffline` / `CamerasUpdated` y actualiza in-place (usa `record with`
para actualizar inmutablemente cada `HouseSnapshot`). Todas las
actualizaciones de la colección pasan por `MainThread.BeginInvokeOnMainThread`
(obligatorio: `ObservableCollection` no es thread-safe con el binding de UI).

### `HousesViewModel`
Envuelve `ServerViewerService` para exponer `INotifyPropertyChanged` (el
servicio en sí no lo implementa). Traduce el enum de estado a
`StatusText`/`StatusColor` para la UI.

### `CameraPlayerPage`
Cliente del reproductor embebido: arma la URL
`{EmbedPlayerBaseUrl}?houseId=...&cameraId=...&token=...` y la carga en un
`WebView`. Escucha navegaciones a `app://status?state=...` (puente
JS→nativo desde `PlayerPage.cs`) para mostrar/ocultar el loading indicator y
disparar `DisplayAlert` en error. Tiene manejo especial de orientación
(`OnSizeAllocated`) para pantalla completa en landscape, y limpieza en
`OnDisappearing` (`Source = "about:blank"` para cortar la
`RTCPeerConnection`/SignalR del WebView y no seguir consumiendo
datos/batería en segundo plano).

También conserva un botón **"Abrir en VLC"** que usa `_camera.StreamUrl`
(RTSP directo) vía `VlcLauncher` — con implementación real **solo en
Windows** (busca `vlc.exe` en dos rutas típicas de instalación); en
cualquier otra plataforma devuelve un error explicativo.

### Condicionales de plataforma relevantes
- `LibVLCSharp.MAUI` y `VideoLAN.LibVLC.Android` solo se referencian
  `Condition="$(TargetFramework.Contains('android'))"` — referenciar el
  paquete `.MAUI` en Windows rompe WinUI al arrancar porque MAUI escanea
  todos los ensamblados referenciados para auto-registrar handlers, y ese
  paquete no tiene implementación para Windows.
- `Core.Initialize()` + `builder.UseLibVLCSharp()` en `MauiProgram.cs` están
  dentro de `#if ANDROID`.

## 8. Herramientas auxiliares

### `CeleCamIp.CameraDiagnostic`
App de consola standalone que pide IP/usuario/contraseña por `Console.ReadLine`
y corre `RtspCameraAdapter` directo (sin Server ni Gateway), con timeout de
30s. Útil para depurar cámaras nuevas antes de darlas de alta en
`House:CameraIps`. Tiene un `publish/` ya generado en el repo (self-contained,
~70MB) — ver `DEPLOYMENT.md`.

### `tools/ViewerTestClient`
Cliente de consola que se conecta al Hub **sin autenticación** (no setea
`AccessTokenProvider`), llama `JoinAsViewer` y `RequestStream`, e imprime
todos los eventos crudos como JSON. Sirve para depurar el protocolo de
señalización sin la complejidad de la app. **No está incluido en
`CeleCamIp.sln`** — hay que abrir su `.csproj` directamente o agregarlo a
mano. Como no manda API key, solo funciona contra un Server local que no
tenga `Auth:ApiKey` reforzada de forma real (ver `DECISIONES.md` sobre por
qué esto en realidad falla siempre que el servidor tenga la key
configurada).

## 9. Configuración — de dónde sale cada valor

| Valor | Server | Gateway | App |
|---|---|---|---|
| API key | `Auth:ApiKey` en `appsettings*.json` | `Auth:ApiKey` en `appsettings.json` / env `Auth__ApiKey` | `AppSecrets.ApiKey` (constante compilada) |
| URL del Hub | — (es quien lo hostea) | `Server:HubUrl` / env `Server__HubUrl` | `AppConfig.ServerHubUrl` (constante compilada) |
| ICE servers (STUN/TURN) | `IceServers` en `appsettings.json`, expuestos por `/api/ice-servers` | `IceServers` en `appsettings.json` / env `IceServers__N__*` | los pide al Server vía `/api/ice-servers` desde el JS de `PlayerPage` |
| IPs de cámaras | — | `House:CameraIps` / env `House__CameraIps__N` | — (las recibe del Server) |
| Credenciales de cámara | — | `House:CameraUsername` / `House:CameraPassword` (mismas para todas las cámaras de la casa) | — |

No existe (todavía) una pantalla de configuración en la app ni un mecanismo
de gestión de secretos real en ningún componente — ver `SECURITY.md` y
`DECISIONES.md`.
