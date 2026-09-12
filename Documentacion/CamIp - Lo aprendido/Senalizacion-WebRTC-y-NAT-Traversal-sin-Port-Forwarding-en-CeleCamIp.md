# Señalización WebRTC y NAT traversal sin abrir puertos en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: explicar
> el patrón de **rendezvous point** (conexión saliente + servidor
> intermedio) que permite ver cámaras de una casa desde cualquier red sin
> abrir puertos en el router ni depender de IP pública, y cómo se implementa
> el **protocolo de señalización WebRTC** (SDP/ICE) sobre un Hub de
> SignalR — con el detalle suficiente para reproducirlo en otro stack.
>
> Fuente: `CeleCamIp.Server/Hubs/GatewayHub.cs`,
> `CeleCamIp.Server/Services/HouseRegistry.cs`,
> `CeleCamIp.Gateway/Services/GatewayConnectionService.cs`,
> `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`,
> `CeleCamIp.Shared/WebRtc/SignalingModels.cs`, `Documentacion/DECISIONES.md`
> (D1, D2, D14).
>
> Complementa: `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md` (qué pasa
> con el video una vez que la señalización cierra) y
> `Autenticacion-por-ApiKey-y-Modelo-de-Seguridad-en-CeleCamIp.md` (cómo se
> protege este mismo Hub).

---

## 1. El problema de fondo: CGNAT y por qué "abrir un puerto" no alcanza

Para ver una cámara IP que está en la LAN de una casa desde afuera de esa
red, la forma ingenua es exponerla directo: abrir un puerto en el router y
apuntarlo a la IP local de la cámara (port forwarding), más algún mecanismo
de DNS dinámico si la IP pública cambia. Esto se rompe en la práctica para
un producto doméstico por dos motivos:

1. **Fricción de configuración**: pedirle a un usuario final que entre al
   panel de su router, entienda qué es un puerto, y configure DDNS, es una
   barrera de adopción real.
2. **CGNAT (Carrier-Grade NAT)**: muchos ISPs (particularmente en
   Argentina/Latinoamérica, según el propio comentario del código) no le
   asignan una IP pública real a cada hogar — varios clientes comparten una
   misma IP pública a través de NAT del operador. En ese escenario, **no
   hay puerto que abrir**: el router del usuario ni siquiera tiene una IP
   pública propia sobre la cual escuchar.

**Aprendizaje general:** cualquier sistema que necesite que un dispositivo
detrás de un NAT doméstico sea *alcanzable* desde internet debe asumir que
"exponer un puerto entrante" no es una opción confiable. Hace falta que la
iniciativa de conexión salga siempre desde adentro de la LAN.

## 2. El patrón: conexión saliente hacia un rendezvous point

**Decisión (`DECISIONES.md` D1):** la casa (llamada `Gateway` en el código)
abre una conexión saliente permanente hacia un servidor público (`Server`).
Nunca hay una conexión entrante hacia la casa. El cliente móvil (`Viewer`)
también se conecta al mismo `Server`, que actúa de intermediario para que
ambos "se encuentren":

```
Gateway (casa)  ──conexión saliente (SignalR/WSS)──►  Server  ◄──conexión saliente (SignalR/WSS)──  App (Viewer)
```

Esto es exactamente el mismo patrón que usa **cualquier sistema de
videollamadas WebRTC** (dos participantes detrás de NATs distintos que no
pueden conectarse directo entre sí sin ayuda) y el mismo que usan productos
comerciales de cámaras IP (Ring, TP-Link Tapo, etc.): ambos extremos abren
conexión saliente hacia un servidor con IP pública conocida, y ese servidor
resuelve el problema de "¿cómo se encuentran dos partes sin IP pública?".

**Trade-off aceptado explícitamente:** el `Server` se vuelve un punto único
de fallo — si se cae, ninguna casa es visible aunque Gateway y cámaras estén
sanos. No hay fallback de señalización P2P directa. Es un costo consciente
a cambio de resolver el problema real (CGNAT) con la mínima fricción para
el usuario.

**Aprendizaje clave:** cuando el requisito es "conectar dos partes detrás de
NAT sin configuración del usuario", la solución no es evitar el servidor
intermedio — es aceptarlo para la fase de *señalización* (que es liviana:
solo texto, SDP/ICE) y dejar que el video en sí intente ir P2P después, una
vez que ambas partes ya se "presentaron".

## 3. Un único Hub para dos roles distintos

**Decisión (`DECISIONES.md` D2):** en vez de tener un Hub de SignalR para
Gateways y otro separado para Viewers, `GatewayHub` atiende a los dos tipos
de cliente en la misma clase, diferenciando el rol **por qué métodos invoca
cada uno**, no por una ruta separada.

Métodos que solo tiene sentido que llame el Gateway: `RegisterHouse`,
`ReportCameras`, `SendOffer`, `SendIceCandidateToViewer`,
`ReportStreamError`. Métodos que solo tiene sentido que llame el Viewer:
`JoinAsViewer`, `RequestStream`, `SendAnswer`,
`SendIceCandidateToGateway`.

```csharp
public async Task RegisterHouse(string houseId)
{
    _registry.RegisterHouse(houseId, Context.ConnectionId);
    await Clients.Group("viewers").SendAsync("HouseOnline", houseId);
}

public async Task JoinAsViewer()
{
    await Groups.AddToGroupAsync(Context.ConnectionId, "viewers");
    var snapshot = _registry.GetSnapshot();
    await Clients.Caller.SendAsync("ViewerJoined", snapshot); // no espera al próximo evento
}
```

**Aprendizaje clave:** un único punto de entrada (Hub/endpoint) puede servir
a dos "personas" distintas del protocolo si lo que las distingue es el
conjunto de operaciones que cada una invoca, no la identidad de la conexión
en sí. El costo es que la clase mezcla dos responsabilidades (directorio de
presencia + relay de señalización); vale la pena separar en Hubs distintos
si el tamaño crece más allá de lo manejable — el propio código ya lo nota.

## 4. El registro de presencia: `HouseRegistry`

Estructura en memoria (`ConcurrentDictionary`, sin persistencia) con **tres
diccionarios**, elegidos para que cada operación del ciclo de vida sea O(1):

- `connectionByHouse` — resolver "¿a qué `ConnectionId` le mando esto para
  llegar a la casa X?" cuando un Viewer pide un stream.
- `houseByConnection` — el **inverso**, para resolver en O(1) "¿qué casa
  era esta conexión que se acaba de caer?" dentro de
  `OnDisconnectedAsync`, sin recorrer todo el diccionario anterior.
- `camerasByHouse` — último snapshot de cámaras reportado por cada casa.

```csharp
public override async Task OnDisconnectedAsync(Exception? exception)
{
    if (_registry.TryGetHouseByConnection(Context.ConnectionId, out var houseId))
    {
        // protección contra condición de carrera: solo remover si el
        // ConnectionId actual sigue siendo el que se está cayendo
        _registry.UnregisterIfCurrent(houseId, Context.ConnectionId);
        await Clients.Group("viewers").SendAsync("HouseOffline", houseId);
    }
}
```

**Aprendizaje clave — la condición de carrera de reconexión:** si un
Gateway viejo queda colgado (proceso zombie que no cerró limpio) y el mismo
Gateway arranca de nuevo y se reconecta antes de que el viejo termine de
caer, `OnDisconnectedAsync` del proceso viejo **no debe pisar** el registro
que ya dejó el proceso nuevo. La solución es comparar el `ConnectionId` que
se está desconectando contra el que está actualmente registrado para esa
casa, y solo actuar si coinciden — un patrón general para cualquier sistema
de presencia con reconexión automática.

## 5. El protocolo de señalización paso a paso (relay ciego)

El `Server` **nunca interpreta el contenido** del SDP o los candidatos ICE
— solo sabe a qué `ConnectionId` reenviar cada mensaje, resolviendo por
`HouseId`/`houseByConnection`. Esto lo mantiene agnóstico de WebRTC en sí:
podría relayar cualquier protocolo de señalización con la misma lógica.

```
Viewer                          Server (Hub)                        Gateway
  │  RequestStream(houseId,camId)  │                                    │
  ├────────────────────────────────►                                    │
  │                                 │  StreamRequested(viewerConnId,camId)
  │                                 ├───────────────────────────────────►│
  │                                 │                                    │ arma RTCPeerConnection + oferta SDP
  │                                 │   SendOffer(viewerConnId,camId,offer)
  │                                 │◄───────────────────────────────────┤
  │      ReceiveOffer(camId,offer) │                                    │
  │◄────────────────────────────────┤                                    │
  │ setRemoteDescription, createAnswer                                   │
  │  SendAnswer(houseId,camId,answer)                                    │
  ├────────────────────────────────►                                    │
  │                                 │  ReceiveAnswer(viewerConnId,camId,answer)
  │                                 ├───────────────────────────────────►│
  │  SendIceCandidateTo{Viewer,Gateway}(…) ◄── trickle ICE mientras dure la conexión ──►│
```

Los DTOs que viajan por este canal (`CeleCamIp.Shared/WebRtc/SignalingModels.cs`)
son un **espejo directo** de los tipos nativos de WebRTC (`RTCSessionDescription`,
`RTCIceCandidate`), sin transformación de forma:

```csharp
public record SdpDescriptionDto(string Type, string Sdp);
public record IceCandidateDto(string Candidate, string? SdpMid, int? SdpMLineIndex, string? UsernameFragment);
public record IceServerDto(string[] Urls, string? Username, string? Credential);
```

**Aprendizaje clave:** cuando el servidor de señalización no necesita
entender el contenido (solo reenviarlo), modelar los DTOs como espejo 1:1
del protocolo real (WebRTC en este caso) evita una capa de traducción
innecesaria — el mismo objeto que produce el navegador/librería WebRTC de
un lado se serializa, viaja, y se deserializa del otro lado sin mapeos
intermedios.

## 6. `Trickle ICE` y por qué arrancar el video antes de que cierre la negociación

Cada lado va descubriendo candidatos ICE (rutas de red posibles) de forma
incremental y los va mandando a medida que aparecen (`trickle ICE`), en vez
de esperar a tener todos antes de mandar la oferta. Esto acorta el tiempo
total de negociación porque ambas puntas empiezan a probar conectividad en
paralelo con el intercambio de SDP.

El Gateway además **no espera a que el ICE llegue a `connected`** para
arrancar el pipeline de video (ver `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md`,
D13) — arranca en paralelo, para no sumar en serie la latencia de conectar
al RTSP más la latencia de negociar ICE (que puede incluir relay por TURN).

## 7. STUN/TURN: cuándo el relay por Server no alcanza para el video

La señalización pasa siempre por el `Server`, pero el **video** intenta ir
directo (P2P) entre Gateway y Viewer usando ICE con servidores STUN/TURN
configurados en `IceServers` (expuestos vía `GET /api/ice-servers`, detrás
de auth). STUN ayuda a descubrir la IP pública propia cuando hay NAT
"normal" (no CGNAT); TURN releva el video byte a byte cuando ningún camino
directo es posible (típicamente, cuando alguna de las dos puntas está
detrás de un NAT simétrico o CGNAT real).

**Limitación conocida y sin resolver (`DECISIONES.md` D14):** el TURN usado
hoy es el tier gratuito de un único datacenter, sin distribución
geográfica. Si casa y viewer están lejos de esa región, el relay agrega
latencia real y aumenta el riesgo de que ICE no cierre a tiempo. Documentado
como deuda técnica, con dos rutas de solución ya evaluadas en el propio
código: contratar un TURN con anycast/multi-región, o levantar un `coturn`
propio cerca de los usuarios reales.

**Aprendizaje clave:** STUN/TURN no son un detalle de configuración menor —
son, en la práctica, la diferencia entre "funciona con buena latencia" y
"funciona pero con el video relayado desde el otro lado del mundo". La
ubicación geográfica del TURN debería ser una decisión de arquitectura, no
un valor por defecto que se deja sin revisar.

## 8. Cómo reproducir este patrón en otro proyecto

1. **Si necesitás conectar dos partes detrás de NAT sin configuración del
   usuario, no intentes evitar el servidor intermedio** — aceptalo para la
   fase de señalización (liviana, solo texto) y dejá que el payload pesado
   (video/datos) intente ir P2P después.
2. **La conexión siempre sale desde adentro de la red restringida hacia el
   servidor con IP pública conocida**, nunca al revés — esto es lo que
   evita depender de port forwarding o DDNS.
3. **Un Hub/canal de señalización puede servir a dos roles distintos** si
   la diferencia está en qué operaciones invoca cada uno; mantené un mapa
   de "conexión → identidad" y su inverso para que las bajas sean O(1).
4. **Modelá los DTOs de señalización como espejo directo del protocolo
   real** (WebRTC u otro) si el servidor no necesita interpretarlos — evita
   una capa de traducción que no aporta valor.
5. **Cuidado con condiciones de carrera en reconexión**: si un cliente
   puede reconectar antes de que su conexión vieja termine de cerrarse
   formalmente, comparar el identificador de conexión actual antes de dar
   de baja un registro.
6. **Arrancá el trabajo pesado (decodificar/preparar el payload) en
   paralelo a la negociación de transporte**, no como reacción a que el
   transporte ya esté listo — evita sumar latencias en serie.
7. **Si el sistema depende de TURN, tratá su ubicación geográfica como una
   decisión explícita**, no un default — es la diferencia real entre buena
   y mala latencia percibida por el usuario final.

## 9. Referencias a archivos fuente del proyecto

- `CeleCamIp.Server/Hubs/GatewayHub.cs`
- `CeleCamIp.Server/Services/HouseRegistry.cs`
- `CeleCamIp.Gateway/Services/GatewayConnectionService.cs`
- `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`
- `CeleCamIp.Shared/WebRtc/SignalingModels.cs`
- `Documentacion/DECISIONES.md` (D1, D2, D14)
- `Documentacion/ARQUITECTURA.md` (§5, diagrama de señalización)
