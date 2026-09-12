# Decisiones de diseño — CeleCamIp

Este documento reconstruye el "por qué" detrás de las decisiones técnicas
más importantes del proyecto, tomado directamente de comentarios extensos
que ya existían en el código (una práctica que vale la pena mantener: el
código de este repo documenta sus propios bugs resueltos con mucho detalle)
y de lo verificado durante esta auditoría.

## Estado del repo al documentar (importante para no confundirse)

Al momento de generar esta documentación, el repositorio tenía:
- **Un solo commit** (`02bc17c`, "Baseline: estado actual del proyecto antes
  de limpiar Program.cs..."), sin remoto configurado (`git remote -v` vacío).
- **Cambios sin commitear** en el working tree que son justamente los que
  implementan el puente RTSP→WebRTC de punta a punta:
  `CameraPlayerPage.xaml.cs`, `CamerasPage.xaml.cs`, `Program.cs` (Gateway),
  `WebRtcCameraSession.cs`, `Embed/PlayerPage.cs`, `appsettings.json`
  (Server), y `FFmpegProcessSource.cs` como archivo nuevo sin trackear.

**Esto importa** porque `Alcance_funcional.md` (el `.md` que ya existía en
la raíz del repo antes de esta auditoría) afirma que *"la señalización [WebRTC]
ya está construida en el Server y el Gateway, pero todavía no está conectada
a la pantalla de reproducción de la app"*. Verificando el código real, **esto
ya no es así**: `CameraPlayerPage.xaml.cs` construye la URL del embed
(`houseId`+`cameraId`+`token`) y la carga en el `WebView`, y `CamerasPage`
navega ahí pasando esos datos. La rama de reproducción vía WebRTC está
efectivamente cableada de punta a punta en el código actual.

**Conclusión:** ese `.md` describe un estado de dos-tres pasos atrás del
código real (probablemente escrito antes de la sesión de trabajo que dejó
los cambios sin commitear). `ALCANCE_FUNCIONAL.md` en esta carpeta refleja
el estado verificado contra código, no la intención original. Cuando se
retome el proyecto, conviene **hacer commit de estos cambios cuanto antes**
para que el historial de git deje de estar desalineado con la realidad —
ver el checklist de `DEPLOYMENT.md`.

## D1 — El Gateway inicia la conexión, no el Server

**Decisión:** la casa (Gateway) abre una conexión SignalR saliente hacia el
Server; nunca hay una conexión entrante hacia la casa.

**Por qué:** las cámaras están detrás del router doméstico y, en la mayoría
de los ISP argentinos/latinoamericanos, detrás de CGNAT (sin IP pública
real asignada al hogar). Pedirle al usuario que abra puertos o configure
DDNS es fricción que mata la usabilidad para un producto doméstico. El
patrón "conexión saliente hacia un rendez-vous point" es el mismo que usan
soluciones comerciales de cámaras (Ring, TP-Link Tapo, etc.) y es también,
estructuralmente, el mismo patrón que usa la señalización de cualquier
sistema WebRTC.

**Trade-off aceptado:** el Server se vuelve un punto único de fallo y de
disponibilidad — si se cae, ninguna casa puede ser vista aunque las cámaras
y el Gateway estén perfectamente sanos. No hay fallback P2P directo
Gateway↔App sin pasar por el Server para la señalización inicial (aunque el
video en sí, una vez negociado, sí puede terminar viajando P2P si ICE
encuentra un candidato directo).

## D2 — Un único Hub para dos roles (Gateway y Viewer)

**Decisión:** `GatewayHub` atiende tanto a los Gateways de las casas como a
los viewers (la app), diferenciando el rol por qué métodos invoca cada
cliente, no por una ruta o Hub separado.

**Por qué:** simplifica el registro de servicios y el enrutamiento — el
mismo mecanismo de `ConnectionId` sirve para resolver a quién reenviar tanto
el estado de casas/cámaras como la señalización SDP/ICE. El costo es que el
Hub mezcla dos responsabilidades bastante distintas (directorio de
casas + relay de señalización WebRTC) en una sola clase, lo cual ya se nota
en el tamaño de `GatewayHub.cs`.

## D3 — Autenticación por API key única y compartida (no hay usuarios)

**Decisión:** todo cliente (Gateway o App) que quiera hablar con el Server
debe mandar la misma API key configurada en `Auth:ApiKey`.

**Por qué:** es lo mínimo viable para que el Hub no quede completamente
abierto a cualquiera en internet, sin construir un sistema de cuentas para
lo que hoy es, en esencia, un proyecto personal/familiar de una sola casa (o
pocas casas de confianza). Ver `SECURITY.md` para los riesgos concretos que
trae esta decisión tal como está implementada hoy (la clave está además
versionada en texto plano en git, lo cual **no** era parte de la decisión
original — es un descuido operativo, no arquitectónico).

## D4 — RTSP directo primero, WebRTC como puente para WAN

**Decisión:** la primera versión de `CameraPlayerPage` reproducía el RTSP
directo con `LibVLCSharp`; la versión actual reproduce vía el puente
RTSP→WebRTC, dejando LibVLC/RTSP directo como opción manual ("Abrir en
VLC").

**Por qué:** un `rtsp://192.168.0.25:554/...` solo es alcanzable si el
dispositivo que reproduce está en la misma LAN (o una red con ruta) que la
cámara. Apenas el celular sale de esa Wi-Fi (datos móviles, otra red), la
IP privada deja de existir para él. WebRTC, vía ICE con STUN/TURN, sí
atraviesa NAT/redes distintas, igual que cualquier videollamada.

**Trade-off aceptado:** mucha más complejidad (un Gateway que decodifica y
reempaqueta video, dependencia de un servidor TURN externo, latencia
adicional del relay cuando ICE no encuentra un camino directo) a cambio de
que el "ver cámaras desde cualquier lado" realmente funcione. El botón
"Abrir en VLC" se mantiene como vía de escape de menor latencia para cuando
el usuario sabe que está en la LAN de la casa.

## D5 — FFmpeg como proceso externo, no el binding `SIPSorceryMedia.FFmpeg`

**Decisión:** `FFmpegProcessSource` invoca `ffmpeg.exe`/`ffmpeg` como
proceso de sistema y parsea su salida H264 Annex-B a mano, en vez de usar
`FFmpegFileSource` del paquete `SIPSorceryMedia.FFmpeg` (que sigue
referenciado en el `.csproj` y se usa solo para
`FFmpegInit.EnsureBinariesRegistered()` y como prueba de salud en
`DiagnoseAsync`).

**Por qué (según el comentario del código):** "evita el bug de decodificación
de SIPSorceryMedia.FFmpeg". No se documentó en el código el detalle exacto
del bug del binding, pero el resultado práctico es que todo el pipeline de
video real de producción depende de un parser de NALs escrito a mano, con
todo el historial de bugs de bajo nivel que eso conlleva (ver D6-D10).

**Trade-off aceptado:** se ganó control total sobre el pipeline (se pudo
resolver bugs de timestamps, agrupación de NALs y transporte que hubieran
sido cajas negras con el binding), a costa de mantener manualmente un
parser de bitstream H264 — código de bajo nivel, sensible a variaciones de
firmware entre marcas de cámaras.

## D6 a D10 — Historial de bugs resueltos en `FFmpegProcessSource`

Documentados en detalle en el propio código (`FFmpegProcessSource.cs`,
comentario de clase). Se resumen acá porque son la memoria institucional
más valiosa del proyecto para no reintroducir estos bugs:

### D6 — Buffer de lectura reseteado en cada `ReadAsync`
Un NAL de un frame 1080p (cientos de KB) casi nunca entra en una sola
lectura de 64KB del pipe. La versión original vaciaba el buffer después de
cada lectura, así que cualquier NAL cortado a mitad de camino se emitía
"completo" con lo que hubiera hasta ese momento (a veces 1 solo byte).
**Fix:** buffer pendiente persistente entre lecturas (`_pendingBuffer`); solo
se descarta lo que ya se confirmó como NAL completo.

### D7 — Cada NAL individual mandado como "frame" separado, timestamp congelado
SPS/PPS/slice se mandaban por separado con `durationRtpUnits=0` fijo, lo que
dejaba el timestamp RTP congelado para siempre — el navegador nunca podía
reconstruir la línea de tiempo y el video "conectaba" pero nunca decodificaba
nada. **Fix:** agrupar NALs no-VCL con el slice VCL que cierra el *access
unit* y emitirlos juntos, con una duración RTP calculada por tiempo real
transcurrido (clock rate 90000 Hz, estándar de video en RTP/WebRTC).

### D8 — Latencia creciente por `List<byte>` + `RemoveRange`
El buffer de lectura era un `List<byte>` al que se agregaba byte por byte
(hasta 65536 veces por lectura) y del que se removían NALs con
`RemoveRange` (un `Array.Copy` de todo el resto del buffer en cada NAL
extraído). Con FFmpeg entregando decenas de lecturas por segundo, esto
consumía más CPU de la que el pipe podía vaciarse: el buffer del SO se
llenaba, FFmpeg se bloqueaba escribiendo, y la latencia crecía con el
tiempo de reproducción (visible como `speed=0.5x/0.7x` en el log de FFmpeg
pese a usar `-c:v copy`, que no debería ir lento). **Fix:** buffer contiguo
`byte[]` con punteros `_pendingStart`/`_pendingEnd`; agregar datos es un
solo `Buffer.BlockCopy`; consumir un NAL ya procesado es O(1) (solo avanza
`_pendingStart`); el buffer solo se compacta cuando no queda espacio libre
al final.

### D9 — Latencia de arranque de 5-7s por probing por defecto de FFmpeg
FFmpeg analiza el stream de entrada (`probesize`/`analyzeduration`) antes de
largar salida. Para un pase directo RTSP donde el codec ya se conoce por el
SDP (`DESCRIBE`), ese análisis es innecesario. **Fix (primera iteración):**
`-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0`.

### D10 — El fix de D9 resultó demasiado agresivo (crash silencioso, 0 frames)
`-probesize 32` solo funciona si la cámara manda SPS/PPS en el SDP de la
respuesta `DESCRIBE` (campo `sprop-parameter-sets`). Cámaras que los mandan
**in-band** (dentro del propio stream, común en cámaras IP baratas) hacen
que FFmpeg no llegue a ver ni el primer NAL con 32 bytes de probing, no
pueda determinar el tamaño del video, descarte el stream de video de la
salida, y el proceso muera con código `-22` (EINVAL) medio segundo después
de arrancar — sin ningún timeout, sin un solo frame enviado, y con la sesión
WebRTC llegando igual a `connected` porque el problema era 100% de FFmpeg,
no de ICE. **Fix:** `-probesize 65536 -analyzeduration 300000` (moderados:
órdenes de magnitud más chicos que el default de FFmpeg de 5MB/5s, pero con
margen real para encontrar SPS/PPS in-band), más `-map 0:v:0` explícito
(no depender de que la autodetección de stream "adivine" bien) y
`-err_detect ignore_err` (no abortar por errores menores de bitstream,
frecuentes en cámaras baratas con paquetes RTP perdidos).

### D11 — RTSP sobre TCP interleaved falla en silencio con algunas cámaras
Verificado a mano contra una cámara real: con `-rtsp_transport tcp`, FFmpeg
completaba DESCRIBE/SETUP/PLAY sin ningún error visible (SDP, resolución,
framerate correctos) pero el canal de datos interleaved sobre TCP nunca
entregaba un solo paquete RTP — 5 segundos conectado, 0 frames, "Output
file is empty, nothing was encoded". Es un bug de firmware común en cámaras
IP genéricas/clones de Hikvision: anuncian soporte de RTP-sobre-TCP
interleaved pero no lo implementan bien. Con `-rtsp_transport udp`, la
**misma** cámara entregó 30 frames en los mismos 5 segundos, sin cambiar
nada más. **Decisión:** forzar UDP. Como el Gateway corre en la LAN de la
casa (misma red que las cámaras), UDP no tiene problemas de NAT/firewall que
justifiquen forzar TCP — eso solo importaría si el Gateway estuviera fuera
de la LAN de la cámara, que no es el caso de este diseño.

## D12 — Formato H264 armado a mano en el SDP (no autodetectado)

**Decisión:** `WebRtcCameraSession.CreateH264Formats()` construye el
`VideoFormat` explícitamente con `packetization-mode=1`,
`profile-level-id=42e01f` (Baseline Profile, Level 3.1) y
`level-asymmetry-allowed=1`, en vez de dejar que la librería infiera el
formato.

**Por qué:** verificado por reflexión sobre `SIPSorceryMedia.Abstractions`
10.0.16, la firma real de `VideoFormat` requiere el parámetro `parameters`
(la línea `fmtp` del SDP). Sin esos atributos, el navegador rechaza el SDP
con `"Failed to parse codecs correctly"` — H264 los necesita para poder
negociar el formato. `level-asymmetry-allowed=1` es necesario porque, como
FFmpeg usa `-c:v copy` (no recodifica), el profile real de la cámara puede
no coincidir con el anunciado; ese flag le dice al navegador que igual
acepte el stream aunque declare un profile/level distinto.

## D13 — FFmpeg arranca en paralelo a la negociación ICE, no después

**Decisión:** `videoSource.StartVideo()` se llama **antes** de que la
`RTCPeerConnection` llegue a `connected`, no como reacción a ese evento.

**Por qué:** conectar al RTSP + esperar el primer keyframe tiene su propia
latencia. Si eso se hace *después* de terminar la negociación ICE (que ya
de por sí puede tardar segundos con un TURN de por medio), toda la demora se
suma en serie. Si además el ICE nunca llegaba a `connected` (por ejemplo,
por la latencia extra de relayar por un TURN lejano), la sesión se cerraba
sin haber arrancado FFmpeg ni una sola vez. Arrancando ya mismo, FFmpeg
tiene tiempo de conectar al RTSP y tener frames listos **mientras** el ICE
todavía está negociando.

## D14 — TURN sin puntos de presencia regionales (limitación conocida, sin resolver)

**Decisión/estado actual:** se usa el tier gratuito de ExpressTURN
(`free.expressturn.com`), que es un único datacenter, no anycast
geo-distribuido. El comentario en `appsettings.json` del Server documenta
explícitamente que esto **no resuelve el problema de fondo** si casas y
viewers están lejos de esa región: agrega latencia y aumenta el riesgo de
que la negociación ICE no cierre a tiempo.

**Ya se corrigió una regresión relacionada:** el Server llegó a apuntar a
IPs fijas de un TURN en Francia; se corrigió para usar el mismo dominio que
el Gateway (`free.expressturn.com`) en vez de IPs fijas de un solo
datacenter — pero el problema de fondo de latencia geográfica sigue
abierto.

**Opciones documentadas en el propio código para resolverlo a futuro:**
(a) contratar un TURN con puntos de presencia en la región real de las
casas/viewers (ej. Twilio, Xirsys, Metered con anycast), o (b) levantar un
`coturn` propio en un VPS cerca de los usuarios reales y listarlo antes que
el de ExpressTURN, para que ICE priorice el de menor RTT.

## D15 — `appsettings.Production.json` fuerza `Auth:ApiKey` fuera del `.csproj` por el mecanismo de web.config

**Decisión:** `EnvironmentName=Production` se define en el `.csproj` del
Server (hardcodeado) para que el SDK incruste
`ASPNETCORE_ENVIRONMENT=Production` en el `web.config` generado al publicar,
porque el hosting compartido (MonsterASP) no ofrece panel de variables de
entorno. La API key en sí, sin embargo, **no** se pone ahí — va en
`appsettings.Production.json` — porque el mecanismo de variables por
`web.config` trunca valores que terminan en `=` (como el padding de
base64, que es exactamente el formato de esta API key).

**Consecuencia práctica documentada en DEPLOYMENT.md:** cualquier secreto
nuevo que se agregue en este hosting específico debe evitar terminar en
`=` si se piensa pasar por variables de entorno de IIS/web.config, o debe
ir directo en un `appsettings.{Environment}.json` como se hizo acá.

## D16 — `ViewerTestClient` no está en la solución ni tiene autenticación

**Hallazgo (no una decisión deliberada, a confirmar con quien retome el
proyecto):** `tools/ViewerTestClient/Program.cs` no setea
`AccessTokenProvider` al construir su `HubConnection`. Contra el `Server`
tal como está implementado (`ApiKeyAuthenticationHandler` rechaza todo si no
hay key configurada, y rechaza si la key no matchea), esta herramienta
**fallará siempre** que el servidor de destino tenga `Auth:ApiKey`
configurada — es decir, siempre, salvo que alguien la modifique para mandar
el token. Tampoco está referenciada en `CeleCamIp.sln`, así que hay que
abrir su `.csproj` a mano o agregarla a la solución para usarla desde
Visual Studio. Queda como deuda técnica menor a resolver si se la sigue
usando para depurar.

## D17 — Migración del Gateway a Raspberry Pi (Linux/Docker): FFmpeg 8.1 por binarios estáticos + `libPath` explícito en `FFmpegInit.Initialise()`

**Contexto:** el Gateway corría en desarrollo sobre Windows nativo (ver
§2.1 de `DEPLOYMENT.md`), leyendo FFmpeg 8.1 "full build shared" instalado
con WinGet. Al pasar el destino real de producción a una Raspberry Pi 3B
(aarch64) corriendo el Gateway en un contenedor Docker (ver §2.2 de
`DEPLOYMENT.md`), aparecieron dos ajustes nuevos, específicos de
Linux/contenedor, que no hacían falta en Windows. Ninguno de los dos es un
bug de la migración ni algo mal documentado antes — es la adaptación
esperable de pasar de "un binario Windows que lee DLLs locales" a "un
binario Linux en contenedor que resuelve librerías nativas por convención
del SO".

**Decisión 1 — FFmpeg 8.1.x se instala en el contenedor descargando el
build estático de BtbN/FFmpeg-Builds (según `$TARGETARCH`), no vía
`apt-get install ffmpeg`.**

**Por qué:** el proyecto fija `FFmpeg.AutoGen 8.1.0`, que busca los
símbolos nativos por el número de versión mayor exacto de cada librería
(`avcodec.so.62`, `avformat.so.62`, `avutil.so.60`, `avdevice.so.62`,
`avfilter.so.11`, `swscale.so.9`, `swresample.so.6`). El repositorio
oficial de Debian (base de la imagen `mcr.microsoft.com/dotnet/aspnet:9.0`)
va sistemáticamente atrás de la última mayor de FFmpeg — instalar
`ffmpeg` por `apt` trae una serie de versión distinta, y el binding falla
sin excepción clara (`EnsureBinariesRegistered()` devuelve `false`, o
revienta al no encontrar los símbolos con el sufijo de versión esperado).
Se optó por descargar el build "shared" de la serie 8.1
(`ffmpeg-n8.1-latest-<arch>-gpl-shared-8.1`) directamente del release de
BtbN/FFmpeg-Builds y copiar sus `.so*` a `/usr/lib/`, corriendo `ldconfig`
después — esto garantiza que la versión mayor coincida exactamente con la
que pide `FFmpeg.AutoGen`, sin depender de qué versión tenga Debian en su
repo en un momento dado.

**Trade-off aceptado:** el Dockerfile ya no depende solo del gestor de
paquetes de la distribución base — agrega una descarga externa (GitHub
Releases de BtbN) como dependencia del build. Si ese release cambia de URL
o deja de publicarse, el build del Gateway se rompe. Mitigación parcial: el
propio Dockerfile corre `ldd` sobre `libavcodec.so.62` durante el build
para confirmar que no falten dependencias transitivas — un build roto por
este motivo falla ruidosamente en `docker compose build`, no ya en
producción.

**Decisión 2 — `FFmpegInit.Initialise()` recibe `libPath` explícito en
Linux (`/usr/lib`), en vez de `null`.**

**Por qué:** en Windows, `libPath = null` funcionaba porque
`SIPSorceryMedia.FFmpeg` auto-detecta las DLLs en rutas relativas conocidas
(entre otras, la carpeta del propio ejecutable, adonde el target
`CopyFFmpegNativeDlls` del `.csproj` las copia). En Linux, esa misma
auto-detección con `libPath = null` **no** consulta el cache del linker
dinámico (`ldconfig`/`/etc/ld.so.cache`): aunque los `.so` de FFmpeg 8.1
estén instalados y correctamente registrados en `/usr/lib` (confirmado con
`ldd` sin dependencias faltantes), `RegisterFFmpegBinaries(null)` no las
encuentra y tira `System.ApplicationException: Unable to find FFMPEG
binaries`. Es un comportamiento conocido de proyectos que envuelven
`FFmpeg.AutoGen`: en Linux hay que fijar el `RootPath`/`LibrariesPath` a
mano, no alcanza con que las libs estén instaladas en el sistema.
**Fix aplicado:** `Program.cs` arma la ruta según el SO en tiempo de
ejecución (`OperatingSystem.IsLinux() ? "/usr/lib" : null`) y se la pasa
explícitamente a `FFmpegInit.Initialise(...)`.

**Verificado en producción real (Raspberry Pi 3B, aarch64, `abatee.local`):**
con ambos cambios, el log de arranque del Gateway pasó de
`❌ Error inicializando FFmpeg: System.ApplicationException: Unable to find
FFMPEG binaries` a `✅ FFmpeg inicializado correctamente`, sin tocar nada
del pipeline de video real (`FFmpegProcessSource`, ver D5), que no depende
de este binding.

## D18 — El Dockerfile del Gateway copiaba las libs de FFmpeg pero no el binario `ffmpeg`

**Decisión/hallazgo:** al descargar el build estático de BtbN/FFmpeg-Builds
(ver D17), el Dockerfile copiaba `lib/*.so*` a `/usr/lib/` pero nunca
`bin/ffmpeg` a ningún lado del `PATH`. Esto pasó desapercibido en el primer
smoke test porque D17 solo verificaba el binding `SIPSorceryMedia.FFmpeg`
(que únicamente necesita las libs `.so`, no el binario) — el Gateway
arrancaba, se conectaba al Server y reportaba las cámaras sin problema. El
síntoma solo apareció al pedir video real desde la app: `FFmpegProcessSource`
(el pipeline de video real, D5) invoca `ffmpeg` como proceso de sistema, y
`FindFFmpeg()` — que ya busca correctamente en `/usr/bin/ffmpeg` y
`/usr/local/bin/ffmpeg` en Linux, sin ningún bug — no encontraba nada ahí
porque el binario nunca se había copiado, tirando
`FileNotFoundException: No se encontró ffmpeg.exe en el PATH`.

**Por qué pasó (y por qué no es contradictorio con D17):** son dos
consumidores de FFmpeg completamente distintos dentro del mismo proyecto,
cada uno con su propia forma de resolver los binarios:
1. `SIPSorceryMedia.FFmpeg` (binding P/Invoke, solo diagnóstico/health
   check) — necesita los `.so`, resueltos por `libPath` explícito (D17).
2. `FFmpegProcessSource` (pipeline de video real) — necesita el **binario**
   `ffmpeg` ejecutable, resuelto por búsqueda en el `PATH`/rutas conocidas.

Arreglar el primero no arregla el segundo porque no comparten mecanismo de
resolución ni verificación — de ahí que el error apareciera en dos rondas
separadas en vez de una sola.

**Fix:** el Dockerfile ahora también copia `bin/ffmpeg` y `bin/ffprobe` del
mismo paquete BtbN ya descargado hacia `/usr/local/bin/` (con `chmod +x`),
que es exactamente una de las rutas que `FindFFmpeg()` ya sabía probar en
Linux — no hizo falta tocar el código C#, solo completar lo que el Dockerfile
dejaba afuera. Se agregó además una verificación de build (`ffmpeg -version`)
para que, si esto se vuelve a romper, falle ruidosamente en
`docker compose build` y no recién al pedir una cámara desde la app.

**Lección para la checklist de despliegue:** cuando se prueba un cambio de
infraestructura de FFmpeg en este proyecto, no alcanza con confirmar que
`FFmpegInit.Initialise()` (el binding) no tire excepción — hay que probar
también el pedido real de una cámara desde la app (o el visor web), porque
son dos caminos de código independientes que fallan por separado.

## D19 — Secretos sacados del control de versiones (sin rotar keys ni tocar historial todavía)

**Decisión:** `src/CeleCamIp.Gateway/appsettings.json`,
`src/CeleCamIp.Server/appsettings.Production.json` y
`src/CeleCamIp.App/Services/AppSecrets.cs` — los tres con secretos reales en
texto plano (`Auth:ApiKey`, credenciales de `IceServers`,
`House:CameraUsername/Password`) — pasan a `.gitignore` y se sacan del
índice con `git rm --cached` (no se borran del disco: siguen ahí para que
Gateway, Server y App sigan compilando/corriendo igual que antes). Cada uno
tiene un `.example` al lado, trackeado, con la misma estructura pero
placeholders en vez de valores reales — sirve de plantilla para levantar el
proyecto en otra máquina sin copiar secretos a mano.

**Por qué este alcance y no más (por ahora):** se evaluaron tres niveles —
(1) solo sacar los secretos del control de versiones, (2) eso + rotar las
keys actuales, (3) eso + purgar el historial de git. Se optó por (1)
únicamente: rotar antes de tener este mecanismo armado hubiera significado
rotar dos veces: una ahora "a ciegas" a los mismos archivos versionados que
recién se están arreglando, después de nuevo si algo salía mal. Y purgar el
historial de git no es urgente hoy — el repo no tiene remoto configurado
(nadie fuera de esta PC lo vio nunca, ver "Estado del repo al documentar"
más arriba); es un paso a hacer el día que el repo se vuelva público o se
comparta, no antes.

**Caso especial del Server — por qué no son "variables de entorno" puras:**
D15 ya había documentado que el mecanismo de variables de entorno vía
`web.config` de MonsterASP trunca valores que terminan en `=` (el padding
tipico de una key en base64). Por eso ahora, en vez de variables de entorno,
`appsettings.Production.json` (gitignored) sigue siendo el vehiculo del
secreto real para el Server — solo que ahora vive fuera de git en vez de
versionado. `appsettings.json` (el base, trackeado) quedo con
`Auth:ApiKey: ""` y el `IceServers[0].Username/Credential` vacios; ASP.NET
Core carga el base primero y `appsettings.Production.json` despues, pisando
esos valores vacios con los reales solo en el entorno Production.

**Pendiente para cuando se decida seguir:** rotar `Auth:ApiKey` y las
credenciales de `IceServers`/camaras (van a tener que actualizarse en los
tres lugares a la vez: Server, Gateway, App) y, si el repo se vuelve
público, purgar el historial de git de los commits que tienen los secretos
viejos (con `git filter-repo` o similar) antes de publicarlo.

## D20 — Timeout explicito en la conexion RTSP Gateway<->camara (independiente del ICE/TURN)

**Decision:** se agregaron `-rw_timeout 10000000 -timeout 10000000` (10s) al
comando de `ffmpeg` en `FFmpegProcessSource.cs`. Sin esto, si una camara no
respondia (apagada, colgada, credenciales cambiadas, IP incorrecta), el
proceso `ffmpeg` quedaba esperando indefinidamente en el handshake RTSP
(`DESCRIBE`/`SETUP`/`PLAY`, que siempre va por TCP aunque el video despues
vaya por UDP con `-rtsp_transport udp`) sin ningun timeout de por medio, y
`MonitorProcessAsync()` solo detecta cuando el proceso YA termino - no
puede hacer nada si el proceso nunca llega a terminar solo.

**Aclaracion importante (por una confusion real durante el trabajo):** este
timeout es de la conexion **Gateway<->camara** (LAN de la casa), totalmente
independiente del timeout/latencia de **Gateway<->viewer via TURN** (WAN,
por Francia - ver D2 del informe de madurez, pausado). En un intento previo
(fuera de esta sesion) se habia agregado un timeout que causaba que la
sesion WebRTC se cerrara por ICE failed/disconnected, y se habia quitado
por eso - pero ese sintoma (cierre de sesion WebRTC) es de un timeout
distinto, no de este. `FFmpeg` ya arranca en paralelo a la negociacion ICE
(ver comentario `[FIX LATENCIA/TIMEOUT]` en `WebRtcCameraSession.cs`), asi
que un timeout mas largo en la conexion RTSP no puede interferir con el
cierre de la sesion WebRTC por ICE.

**Por que 10s y no menos:** es generoso para una camara en la misma LAN que
el Gateway (deberia responder en milisegundos si esta viva) sin llegar a
ser un timeout "eterno" que deje la sesion colgada minutos si la camara
esta realmente caida.
