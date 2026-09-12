# El Gateway: por qué existe, qué hace, y por qué ni siquiera un TURN lo reemplaza — en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: entender
> a fondo el componente `Gateway` — no como "el cliente de la casa" sino
> como una pieza con un rol híbrido (cliente del Server, servidor de medios
> para el Viewer, y traductor de protocolo) que ningún servicio de
> señalización o relay (ni siquiera un TURN) puede reemplazar. Es el
> componente con más lógica propia de todo el sistema, y el que más se
> aparta del modelo cliente-servidor clásico.
>
> Fuente: `CeleCamIp.Gateway/Program.cs`,
> `CeleCamIp.Gateway/Services/GatewayConnectionService.cs`,
> `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`,
> `CeleCamIp.Gateway/Services/FFmpegProcessSource.cs`,
> `Documentacion/ARQUITECTURA.md` (§6), `Documentacion/DECISIONES.md`
> (D1, D13, D14).
>
> Complementa: `Senalizacion-WebRTC-y-NAT-Traversal-sin-Port-Forwarding-en-CeleCamIp.md`
> (el protocolo que el Gateway habla con el Server) y
> `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md` (el detalle del pipeline
> de video que el Gateway ejecuta).

---

## 1. Por qué el modelo cliente-servidor de "siempre" no alcanza acá

En el modelo cliente-servidor típico que se suele desarrollar (una API REST,
una app que la consume), hay dos partes: un servidor con IP conocida y
siempre disponible, y un cliente que inicia cada request cuando el usuario
lo pide. Ese modelo asume implícitamente que **el servidor puede recibir
conexiones entrantes** — es la definición misma de "servidor".

CeleCamIp tiene un problema distinto: las cámaras están detrás de un router
doméstico, frecuentemente detrás de **CGNAT** (ver
`Senalizacion-WebRTC-y-NAT-Traversal-sin-Port-Forwarding-en-CeleCamIp.md`
§1), así que la casa **no puede recibir conexiones entrantes de ningún
tipo** — ni siquiera si quisiera. No hay forma de que la App, ni el Server,
inicien una conexión hacia la casa. La única dirección posible es: **la
casa llama hacia afuera**.

Esa restricción de red, no una preferencia de diseño, es la razón de
existencia del Gateway: **alguien tiene que vivir físicamente en la LAN de
la casa y ser quien inicia la conexión saliente**, porque es la única
dirección que la red doméstica permite.

## 2. Qué es el Gateway, en una frase

El Gateway es un **Worker Service** (`Microsoft.NET.Sdk.Worker`, sin UI, sin
HTTP entrante propio) que corre dentro de la LAN de una casa — hoy pensado
para Windows en desarrollo y Docker en una Raspberry Pi en producción — con
tres responsabilidades que, en cualquier otro sistema, normalmente estarían
repartidas en componentes separados:

1. **Cliente de presencia**: mantiene una conexión SignalR saliente
   permanente hacia el Server, y se re-anuncia si se cae.
2. **Agente de descubrimiento**: prueba las cámaras configuradas contra los
   adaptadores disponibles (ver `Deteccion-de-Camaras-con-Chain-of-Responsibility-en-CeleCamIp.md`)
   y reporta el resultado.
3. **Servidor de medios bajo demanda**: cuando un Viewer pide ver una
   cámara, arma una `RTCPeerConnection` propia, decodifica el RTSP real de
   esa cámara (ver `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md`) y
   *sirve* ese video por WebRTC — es decir, actúa como el extremo servidor
   de la negociación WebRTC, aunque técnicamente sea "un cliente" del punto
   de vista de su conexión SignalR con el Server.

**Aprendizaje clave:** un mismo proceso puede ser "cliente" respecto de un
componente (Server, por SignalR saliente) y "servidor" respecto de otro
(Viewer, por WebRTC) al mismo tiempo. El rol de cliente/servidor no es una
propiedad fija del proceso — depende del protocolo y la relación que se
esté mirando en cada momento. Etiquetar a un componente solo como "el
cliente" puede esconder que, en otra capa del mismo sistema, se comporta
exactamente al revés.

## 3. Por qué el Gateway sigue siendo necesario incluso teniendo un TURN

Es tentador pensar: "si ya hay un servidor TURN que relaya tráfico a través
de NATs, ¿para qué necesito un proceso corriendo en la casa? ¿No podría el
Server o el TURN hablarle directo a la cámara?" La respuesta tiene tres
partes independientes, y las tres bastan por sí solas para que el Gateway
sea imprescindible:

### 3.1 Un TURN relaya *tráfico WebRTC ya negociado*, no abre conexiones nuevas hacia una LAN

TURN es un relay de **paquetes de media ya empaquetados como WebRTC**
(RTP sobre ICE) entre dos partes que **ya se autenticaron y negociaron una
sesión**. No es un proxy genérico que pueda "entrar" a una red doméstica
por su cuenta e ir a buscar una cámara — solo relaya lo que una de las
puntas ya decidió mandarle, después de que esa punta abrió su propia
conexión saliente hacia el TURN. Si no hay un proceso en la LAN que primero
decida conectarse (saliente) al TURN/Server, no hay ningún paquete que
relayar: **TURN resuelve el problema de "cómo llega el video de un lado a
otro cuando ninguno tiene IP pública", no el problema de "quién va a buscar
el video a la cámara en primer lugar"**. Son dos problemas distintos que se
resuelven en capas distintas.

### 3.2 Nadie más puede hablar RTSP en nombre de la cámara

Ni el Server ni un TURN entienden RTSP, ni tienen forma de llegar a la IP
privada de la cámara (`192.168.x.x`) — esa IP solo existe dentro de la LAN
de la casa. Alguien físicamente en esa red tiene que:

- Conectarse por RTSP a la cámara (protocolo que WebRTC no habla).
- Decodificar/reempaquetar ese H264 al formato que espera una
  `RTCPeerConnection` (ver `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md`).

Esta traducción de protocolo (RTSP→WebRTC) es trabajo de cómputo real (correr
FFmpeg, parsear NALs) que tiene que ejecutarse **en algún proceso real**, y
ese proceso solo puede estar donde la red se lo permite: dentro de la LAN.
Ningún relay de paquetes (TURN) hace transcodificación de protocolo — solo
mueve bytes ya formateados de un lado a otro.

### 3.3 El TURN es *un solo salto más*, no un reemplazo del extremo emisor

Incluso en el mejor caso (ICE encuentra un camino P2P directo entre Gateway
y Viewer, sin pasar por TURN en absoluto — ver §4), sigue existiendo
alguien que *origina* el stream: el Gateway. TURN es solo una alternativa
de *transporte* para cuando el camino directo no es posible; nunca es una
fuente de video en sí mismo. Quitar el Gateway del diagrama no cambia
cuánto se necesita un TURN — cambia si hay algo que transportar.

**Aprendizaje general (el más importante de este documento):** un servicio
de infraestructura de red (TURN, un CDN, un load balancer) resuelve
**cómo viaja** un dato entre dos puntas que ya existen — nunca resuelve
**quién genera ese dato** ni **quién traduce un protocolo a otro**. Antes
de asumir que un servicio de terceros elimina la necesidad de un componente
propio, hay que separar con precisión qué problema resuelve cada capa:
transporte (TURN/STUN/CDN) vs origen de datos y traducción de protocolo
(el Gateway). Confundir ambos lleva a diseños que "en el diagrama" parecen
más simples pero que en la práctica no pueden funcionar.

## 4. Arquitectura interna: cómo un solo Worker Service hace tres trabajos sin bloquearse entre sí

### 4.1 Composition root (`Program.cs`)

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<GatewayOptions>(builder.Configuration);
builder.Services.AddSingleton<ICameraAdapter, RtspCameraAdapter>();
builder.Services.AddSingleton<CameraDetectionService>();
builder.Services.AddHostedService<GatewayConnectionService>(); // el único HostedService

FFmpegInit.EnsureBinariesRegistered(); // no bloqueante si falla, solo se loguea
var host = builder.Build();
await host.RunAsync();
```

Un `Worker Service` (`IHostedService`) es el patrón correcto acá: no hay
peticiones HTTP entrantes que atender (el Gateway no expone ningún
endpoint), solo un proceso de larga duración que mantiene estado y
reacciona a eventos de una conexión saliente. Es, en esencia, un daemon.

### 4.2 `GatewayConnectionService`: el corazón, con resiliencia en dos capas

Es un único `BackgroundService` que concentra toda la lógica de las tres
responsabilidades (§2), coordinadas alrededor de una sola `HubConnection`:

```csharp
_hubConnection = new HubConnectionBuilder()
    .WithUrl(options.HubUrl, opts => opts.AccessTokenProvider = () => Task.FromResult(options.ApiKey))
    .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2),
                                     TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
                                     TimeSpan.FromSeconds(30) })
    .Build();

_hubConnection.Reconnected += async (_) => await RegisterHouseAndDetectCamerasAsync();
_hubConnection.Closed += async (_) =>
{
    // segunda capa: si la reconexión automática de SignalR se agota y dispara Closed,
    // seguir reintentando manualmente cada 5s en un loop propio
    while (_hubConnection.State != HubConnectionState.Connected)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try { await _hubConnection.StartAsync(); }
        catch { /* seguir reintentando */ }
    }
};
```

**Aprendizaje clave — doble capa de resiliencia:** `WithAutomaticReconnect`
de SignalR cubre desconexiones transitorias con backoff creciente, pero
tiene un límite de intentos antes de disparar `Closed` definitivamente. Un
proceso que **debe** seguir vivo indefinidamente (un daemon en una casa,
sin nadie mirando la consola) necesita una segunda capa de reintento manual
por fuera de la librería, para el caso en que la primera capa se dé por
vencida. Confiar solo en el mecanismo por defecto de una librería de
reconexión es razonable para un cliente interactivo (un usuario puede
reintentar a mano); no lo es para un proceso desatendido.

### 4.3 Detección de cámaras: en paralelo al registro, no en serie

```csharp
public override async Task StartAsync(CancellationToken ct)
{
    await _hubConnection.StartAsync(ct);
    await _hubConnection.InvokeAsync("RegisterHouse", _options.HouseId, ct);
    _ = DetectAndReportCamerasAsync(ct); // fire-and-forget deliberado: no bloquea el arranque
}
```

Detectar cámaras (probar rutas RTSP contra cada IP) puede tardar varios
segundos por cámara. Si el registro de la casa (`RegisterHouse`) esperara a
que termine la detección completa, la App vería la casa como "offline" más
tiempo del necesario, aunque la conexión de presencia ya esté lista.

**Aprendizaje clave:** separar "estoy vivo y disponible" de "ya terminé de
inventariar todo lo que tengo para ofrecer" como dos señales
independientes, emitidas en paralelo — el consumidor (la App) puede
mostrar "casa online, cargando cámaras..." en vez de esperar a tener el
inventario completo para considerar la casa disponible.

### 4.4 `WebRtcCameraSession`: una instancia efímera por par (viewer, cámara)

Cada vez que llega `StreamRequested`, el Gateway crea una sesión nueva
(clave `"{viewerConnectionId}:{cameraId}"`), no una sesión compartida por
cámara. Esto significa que **dos viewers distintos viendo la misma cámara
generan dos procesos `ffmpeg` independientes**, cada uno con su propia
`RTCPeerConnection` — el Gateway no comparte ni cachea el video decodificado
entre sesiones.

**Trade-off implícito (no resuelto en el código actual, buen ejemplo de
deuda técnica real):** esto es simple de razonar (cada sesión es
independiente, sin estado compartido ni sincronización) pero no escala en
CPU/ancho de banda si muchos viewers miran la misma cámara a la vez — cada
uno paga el costo completo de un proceso FFmpeg propio. Para el caso de uso
real (pocos viewers de confianza, viendo cámaras de su propia casa) esto es
aceptable; sería el primer cuello de botella a resolver (por ejemplo,
compartir una sola sesión de FFmpeg y multiplexar el video a varios
`RTCPeerConnection`) si el proyecto creciera a más viewers concurrentes por
cámara.

**Aprendizaje general:** el diseño "una sesión nueva e independiente por
consumidor" es el punto de partida correcto por su simplicidad — evita
sincronización y estado compartido complejo — pero hay que ser consciente
de que no escala en recursos si el número de consumidores concurrentes por
recurso compartido (la cámara) crece. Identificar explícitamente ese límite
(como se hace acá) es mejor que no notarlo hasta que el sistema se caiga
bajo carga real.

## 5. El Gateway como traductor: RTSP adentro, WebRTC afuera

Vale la pena remarcar la forma general de lo que hace el Gateway, separado
de los detalles de FFmpeg: **actúa como un adaptador de protocolo entre un
mundo "hacia adentro" (RTSP, IPs privadas, credenciales de cámara, UDP en
LAN) y un mundo "hacia afuera" (WebRTC, señalización por SignalR, ICE/TURN,
internet público)**. Ninguno de los dos mundos necesita saber nada del
otro: la cámara no sabe que existe WebRTC; el Viewer no sabe que existe
RTSP. El Gateway es la única pieza que conoce ambos vocabularios.

**Aprendizaje clave:** cuando dos sistemas con protocolos/modelos de datos
incompatibles necesitan comunicarse, un componente traductor dedicado
(en vez de forzar a uno de los dos lados a entender el protocolo del otro)
mantiene ambos lados simples y desacoplados — el costo se concentra en un
solo lugar (acá, el pipeline de FFmpeg + parseo de NALs), en vez de
esparcirse como complejidad accidental en la cámara o en el cliente.

## 6. Cómo pensar el rol de "el Gateway" al reproducir esto en otro proyecto

1. **Identificá primero la restricción de red real**, no la preferencia de
   diseño: si una de las partes no puede recibir conexiones entrantes bajo
   ninguna circunstancia (CGNAT, firewall corporativo, dispositivo IoT sin
   IP pública), necesitás un agente que viva en esa red y conecte hacia
   afuera — no hay forma de rodear esa restricción desde el otro lado.
2. **Un servicio de transporte (TURN, un relay, un proxy) nunca reemplaza a
   quien origina o traduce el dato** — antes de eliminar un componente
   propio asumiendo que "el proveedor externo ya lo resuelve", separá
   explícitamente el problema de transporte del problema de origen/traducción
   de datos.
3. **Un mismo proceso puede ser cliente de un protocolo y servidor de otro
   al mismo tiempo** — no fuerces la etiqueta "cliente" o "servidor" como
   propiedad única de un componente si su rol cambia según la relación que
   mires.
4. **Para un proceso desatendido de larga duración, sumá una segunda capa
   de reintento manual por fuera de la reconexión automática de la
   librería** que uses — la librería cubre el caso común, el reintento
   manual cubre el caso en que la librería se rinde.
5. **Separá "estoy disponible" de "ya inventarié todo lo que tengo"** como
   señales independientes cuando el inventario sea costoso — no bloquees
   la disponibilidad básica esperando el inventario completo.
6. **Empezá con sesiones independientes por consumidor** (simple, sin
   estado compartido) y documentá explícitamente en qué punto ese diseño
   dejaría de escalar — no optimices prematuramente por compartir recursos
   entre consumidores si el caso de uso real no lo necesita todavía.
7. **Si tu sistema conecta dos protocolos incompatibles, aislá esa
   traducción en un único componente dedicado** — mantiene ambos extremos
   simples y concentra la complejidad de la traducción en un solo lugar
   auditable.

## 7. Referencias a archivos fuente del proyecto

- `CeleCamIp.Gateway/Program.cs`
- `CeleCamIp.Gateway/Services/GatewayConnectionService.cs`
- `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`
- `CeleCamIp.Gateway/Services/FFmpegProcessSource.cs`
- `Documentacion/ARQUITECTURA.md` (§6, descripción completa del Gateway)
- `Documentacion/DECISIONES.md` (D1 — por qué el Gateway inicia la
  conexión; D13 — arranque en paralelo a ICE; D14 — límites del TURN
  actual)
