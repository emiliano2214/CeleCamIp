# Puente RTSP→WebRTC con FFmpeg como proceso externo en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: explicar
> cómo `CeleCamIp` convierte un stream RTSP de una cámara IP en video WebRTC
> reproducible en un navegador, invocando FFmpeg como **proceso externo** y
> parseando su salida H264 a mano — y documentar el historial de bugs de
> bajo nivel que dejó esa decisión, porque es la memoria institucional más
> valiosa del proyecto para no reintroducirlos.
>
> Fuente: `CeleCamIp.Gateway/Services/FFmpegProcessSource.cs`,
> `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`,
> `Documentacion/DECISIONES.md` (D5 a D13).
>
> Complementa: `Senalizacion-WebRTC-y-NAT-Traversal-sin-Port-Forwarding-en-CeleCamIp.md`
> (qué pasa antes de que este pipeline arranque).

---

## 1. El problema: el navegador no habla RTSP

Un `<video>` o `RTCPeerConnection` de un navegador no puede consumir
`rtsp://` directamente — WebRTC espera H264/VP8/VP9 empaquetado sobre RTP,
negociado por SDP. Una cámara IP típica habla RTSP (`DESCRIBE`/`SETUP`/
`PLAY`) y entrega H264 crudo. Alguien en el medio tiene que hacer de puente:
recibir RTSP de un lado y entregar frames a una `RTCPeerConnection` del
otro. Ese "alguien" es el `Gateway`, corriendo en la misma LAN que la
cámara.

## 2. Decisión: FFmpeg como proceso externo, no un binding nativo

**Decisión (`DECISIONES.md` D5):** en vez de usar `FFmpegFileSource` del
paquete `SIPSorceryMedia.FFmpeg` (un binding P/Invoke sobre las librerías
`libav*` de FFmpeg), `FFmpegProcessSource` **invoca `ffmpeg.exe`/`ffmpeg`
como un proceso de sistema separado** y parsea su `stdout` a mano.

```csharp
var psi = new ProcessStartInfo
{
    FileName = "ffmpeg",
    Arguments = "-fflags nobuffer -flags low_delay -probesize 65536 " +
                "-analyzeduration 300000 -err_detect ignore_err " +
                "-rtsp_transport udp -i \"<rtsp-url>\" " +
                "-map 0:v:0 -c:v copy -an -f h264 pipe:1",
    RedirectStandardOutput = true,
    UseShellExecute = false,
};
```

**Por qué:** el comentario original en el código dice que evita "el bug de
decodificación de `SIPSorceryMedia.FFmpeg`" — no se documentó el detalle
exacto del bug, pero el resultado práctico importa más: **usar el binario
de FFmpeg tal cual, sin una capa P/Invoke en el medio, da control total
sobre el comportamiento** (parámetros, timing, manejo de errores) a costa
de tener que reconstruir a mano todo lo que el binding hubiera dado gratis
(parseo de NALs, timestamps RTP).

**Aprendizaje clave:** cuando una librería/binding de terceros tiene un bug
que no se puede diagnosticar ni parchear rápido, invocar la herramienta
subyacente como proceso externo (con su interfaz de línea de comandos, que
suele estar mucho más probada y documentada que un binding específico) es
una salida de escape válida — el costo es que hay que reimplementar el
"pegamento" (en este caso, un parser de bitstream) que el binding
normalmente resuelve.

## 3. `-c:v copy`: por qué no se recodifica

```
-c:v copy
```

Le dice a FFmpeg que **reenvíe el H264 tal cual viene de la cámara**, sin
decodificar y volver a codificar. Esto es central para la latencia: cada
transcodificación agrega decenas o cientos de milisegundos y consume CPU
del Gateway (que puede ser una Raspberry Pi). El costo es que el Gateway ya
no controla el *profile*/*level* real del H264 — viaja el que la cámara
decidió usar, que puede no coincidir exactamente con lo que se anuncia en
el SDP (ver §6, `level-asymmetry-allowed`).

**Aprendizaje general:** en cualquier puente de video de baja latencia,
preguntarse primero "¿hace falta decodificar esto de verdad, o alcanza con
reempaquetarlo?" — evitar una transcodificación innecesaria suele ser la
optimización de latencia más grande disponible, más que cualquier tuning
fino de parámetros de codificación.

## 4. El parser de NALs Annex-B escrito a mano

FFmpeg entrega H264 en formato **Annex-B**: una secuencia continua de bytes
donde cada NAL (Network Abstraction Layer unit — un fragmento de video,
como SPS, PPS o un slice) está delimitado por un *start code*
(`0x000001` o `0x00000001`), sin ningún framing adicional. `FFmpegProcessSource`
tiene que:

1. Leer bytes del pipe (`stdout` de FFmpeg) en bloques.
2. Encontrar los *start codes* dentro del buffer acumulado
   (`FindStartCode`).
3. Agrupar los NALs que forman una misma "unidad de acceso" (un frame:
   típicamente SPS + PPS + slice, o solo el slice en frames intermedios).
4. Calcular una duración RTP real y entregar cada unidad de acceso completa
   a `RTCPeerConnection.SendVideo(...)`.

Este parser de bajo nivel es, según el propio código, **el componente más
complejo del sistema** — y también el que acumuló más bugs de producción,
documentados en detalle en `DECISIONES.md`. Vale la pena repasarlos porque
son generalizables a cualquier pipeline de streaming de bajo nivel escrito
a mano.

## 5. Historial de bugs (la memoria institucional del proyecto)

### D6 — Buffer reseteado en cada lectura → NALs truncados
La primera versión vaciaba el buffer de lectura después de procesar cada
bloque leído del pipe. Un NAL de un frame 1080p (cientos de KB) casi nunca
entra en una sola lectura de 64KB, así que cualquier NAL cortado a mitad de
camino se "cerraba" con lo que hubiera hasta ese momento — a veces un solo
byte.

**Fix:** mantener un buffer pendiente **persistente entre lecturas**; solo
descartar lo que ya se confirmó como NAL completo.

**Aprendizaje general:** al parsear un stream continuo en bloques de
tamaño fijo, nunca asumir que una unidad lógica (NAL, línea, mensaje)
termina dentro del mismo bloque en que empezó — el estado de "lo que
todavía no se cerró" tiene que sobrevivir entre lecturas.

### D7 — NALs sueltos con timestamp congelado → nunca decodifica
SPS/PPS/slice se mandaban como "frames" separados con `durationRtpUnits=0`
fijo. El navegador recibía datos pero **nunca podía reconstruir la línea de
tiempo** — el video "conectaba" (ICE en `connected`) pero jamás mostraba
nada.

**Fix:** agrupar los NALs no-VCL (SPS/PPS/SEI) con el NAL de slice VCL que
cierra esa unidad de acceso, y calcular la duración RTP real a partir del
tiempo transcurrido entre unidades de acceso (clock rate de 90000 Hz,
estándar en video RTP/WebRTC).

**Aprendizaje general:** "los datos llegan" y "el receptor puede
reproducirlos" son cosas distintas — un consumidor de video necesita
también metadata de *timing* correcta, no solo el payload.

### D8 — Latencia creciente por `List<byte>` + `RemoveRange`
El buffer era un `List<byte>` al que se agregaba byte por byte (hasta
65536 veces por lectura) y del que se removían NALs ya procesados con
`RemoveRange` — una operación que internamente hace un `Array.Copy` de
**todo el resto del buffer** cada vez. Con FFmpeg entregando decenas de
lecturas por segundo, el costo de CPU de esas copias superaba la velocidad
a la que el pipe se podía vaciar: el buffer del sistema operativo se
llenaba, FFmpeg se bloqueaba escribiendo, y la latencia crecía con el
tiempo de reproducción (visible como `speed=0.5x` en el log de FFmpeg pese
a `-c:v copy`, que no debería ir lento nunca).

**Fix:** buffer contiguo `byte[]` con punteros `_pendingStart`/`_pendingEnd`
en vez de una lista dinámica:

```csharp
// agregar datos nuevos: un solo BlockCopy, no un loop de Add()
Buffer.BlockCopy(datosLeidos, 0, _pendingBuffer, _pendingEnd, datosLeidos.Length);
_pendingEnd += datosLeidos.Length;

// "consumir" un NAL ya procesado: O(1), solo avanza el puntero
_pendingStart = offsetDelSiguienteNal;

// el buffer solo se compacta (mover lo que queda al inicio) cuando no
// queda espacio libre al final — no en cada NAL extraído
```

**Aprendizaje general:** en un hot path que procesa datos continuamente a
alta frecuencia, una estructura que parece "simple y segura"
(`List<T>.RemoveRange`) puede esconder un costo O(n) por operación que se
vuelve un cuello de botella real solo bajo carga sostenida — vale la pena
medir con datos reales (`speed=` en el log de FFmpeg, en este caso) antes
de descartar la hipótesis de que "el código de alto nivel está bien, el
problema debe ser de red".

### D9 y D10 — El costo-beneficio de `probesize`/`analyzeduration`
FFmpeg por defecto analiza el stream de entrada antes de largar salida
(`probesize`/`analyzeduration`), lo cual agrega 5-7 segundos de latencia de
arranque innecesarios cuando el codec ya se conoce de antemano (por el
`DESCRIBE` RTSP). La primera iteración del fix fue agresiva
(`-probesize 32 -analyzeduration 0`) y funcionó... hasta que se probó
contra una cámara que manda SPS/PPS **in-band** (dentro del propio stream,
no en el SDP de `DESCRIBE`, común en cámaras IP baratas): con solo 32 bytes
de probing, FFmpeg no llegaba a ver ni el primer NAL, no podía determinar
el tamaño del video, descartaba el stream, y el proceso moría en silencio
(`código -22`, sin ningún frame enviado) — con el ICE llegando igual a
`connected`, porque el problema era 100% de FFmpeg, no de la negociación.

**Fix final:** valores intermedios (`-probesize 65536
-analyzeduration 300000`) — órdenes de magnitud más chicos que el default
de FFmpeg (varios MB / varios segundos), pero con margen real para
encontrar SPS/PPS in-band, más `-map 0:v:0` explícito (no depender de
autodetección de stream) y `-err_detect ignore_err` (no abortar por
errores menores de bitstream, frecuentes en cámaras baratas con paquetes
RTP perdidos).

**Aprendizaje general:** un fix de latencia "todo o nada" (llevar un
parámetro a su valor más agresivo) suele romper el caso menos común pero
real (cámaras con SPS/PPS in-band). El patrón correcto es buscar el punto
intermedio con datos reales de varios dispositivos, no el extremo teórico.
Y un fallo silencioso (proceso que muere sin excepción visible ni timeout)
es el peor tipo de bug para diagnosticar — vale la pena loguear
explícitamente el código de salida y `stderr` de cualquier proceso externo
que se invoque.

### D11 — RTSP sobre TCP falla en silencio con ciertas cámaras
Con `-rtsp_transport tcp` (interleaved), FFmpeg completaba
`DESCRIBE`/`SETUP`/`PLAY` sin ningún error visible — SDP correcto,
resolución y framerate correctos — pero el canal de datos nunca entregaba
un solo paquete RTP ("Output file is empty, nothing was encoded"). Es un
bug de firmware conocido en cámaras IP genéricas/clones: anuncian soporte
de RTP-sobre-TCP interleaved pero no lo implementan bien. Con
`-rtsp_transport udp`, la **misma** cámara entregó 30 frames en los mismos
5 segundos, sin cambiar nada más.

**Decisión:** forzar UDP. Como el Gateway corre en la misma LAN que las
cámaras, UDP no tiene los problemas de NAT/firewall que normalmente
justificarían forzar TCP — esa preocupación solo aplicaría si el Gateway
estuviera fuera de la red de la cámara, que no es el caso de este diseño.

**Aprendizaje general:** cuando un protocolo estándar (RTSP/RTP) se
implementa contra hardware de bajo costo con variabilidad de firmware, "el
protocolo lo permite" y "el dispositivo lo implementa bien" no son lo
mismo — vale la pena tener un plan B de transporte y validar contra
hardware real, no solo contra la especificación.

## 6. `D12` — El SDP de H264 armado a mano

`WebRtcCameraSession.CreateH264Formats()` construye el `VideoFormat`
explícitamente con `packetization-mode=1`, `profile-level-id=42e01f`
(Baseline Profile, Level 3.1) y `level-asymmetry-allowed=1`, en vez de
dejar que la librería infiera el formato — porque, sin esos atributos
`fmtp`, el navegador rechaza el SDP con `"Failed to parse codecs
correctly"`. `level-asymmetry-allowed=1` es la pieza que conecta con la
decisión D5/`-c:v copy`: como no se recodifica, el profile real de la
cámara puede no coincidir con el anunciado, y ese flag le dice al navegador
que acepte el stream de todas formas.

## 7. `D13` — Arrancar FFmpeg en paralelo a la negociación ICE

`videoSource.StartVideo()` se llama **antes** de que la `RTCPeerConnection`
llegue a `connected`, no como reacción a ese evento — para que, mientras
ICE todavía negocia (potencialmente con latencia extra de un TURN de por
medio), FFmpeg ya esté conectado al RTSP y tenga el primer keyframe listo.
Si se hiciera en serie, ambas latencias se sumarían; si además el ICE nunca
llegara a cerrar, FFmpeg nunca habría arrancado ni una vez.

## 8. Cómo reproducir este pipeline en otro proyecto

1. **Si un binding nativo de una librería de video tiene un bug que no se
   puede diagnosticar rápido, considerá invocar el binario real como
   proceso externo** — perdés comodidad de API a cambio de usar la
   interfaz más probada y documentada de la herramienta.
2. **Preferí `copy`/passthrough sobre recodificar** cuando el destino
   puede aceptar el codec de origen — la transcodificación suele ser el
   mayor costo de latencia y CPU de cualquier pipeline de video.
3. **Un parser de streaming necesita estado persistente entre lecturas**:
   nunca asumas que una unidad lógica cabe en un solo bloque leído.
4. **Usá un buffer circular/contiguo con punteros, no una lista dinámica
   con remociones por rango**, en cualquier hot path de alta frecuencia —
   medí con datos reales de carga sostenida, no solo con una prueba corta.
5. **Los metadatos de timing (duración/timestamp) son tan necesarios como
   el payload** — datos sin timing correcto pueden "llegar" sin que el
   receptor pueda reproducirlos nunca.
6. **Ajustá parámetros de probing/análisis con el caso menos común real en
   mente** (el dispositivo "raro" de tu universo de hardware), no solo con
   el caso feliz — un valor demasiado agresivo puede fallar en silencio.
7. **Tené un plan B de transporte** (UDP vs TCP, u otro) cuando trabajés
   contra hardware de terceros con firmware variable, y validá contra
   dispositivos reales antes de confiar en la especificación del
   protocolo.
8. **Arrancá el trabajo pesado de preparar el payload en paralelo a la
   negociación de transporte**, no en serie después de que el transporte
   ya esté listo.

## 9. Referencias a archivos fuente del proyecto

- `CeleCamIp.Gateway/Services/FFmpegProcessSource.cs` (comentario de clase
  con el historial completo de bugs)
- `CeleCamIp.Gateway/Services/WebRtcCameraSession.cs`
- `Documentacion/DECISIONES.md` (D5 a D13)
- `Documentacion/ARQUITECTURA.md` (§6, descripción de `FFmpegProcessSource`)
