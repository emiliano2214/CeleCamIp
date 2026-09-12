# Detección de cámaras con Chain of Responsibility en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: explicar
> cómo `CeleCamIp` abstrae "conectarse a una cámara IP" detrás de un
> contrato común, cuando en la práctica cada fabricante/protocolo tiene un
> mecanismo distinto — y cómo un cliente RTSP mínimo hecho a mano evita
> depender de librerías pesadas para una operación simple.
>
> Fuente: `CeleCamIp.Shared/Cameras/ICameraAdapter.cs`,
> `CameraDetectionService.cs`, `Adapters/RtspCameraAdapter.cs`,
> `Adapters/RtspProbe.cs`, `CameraDescriptor.cs`, `CameraConnection.cs`.
>
> Complementa: `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md` (qué se
> hace con la URL de stream una vez que este proceso la encuentra).

---

## 1. El problema: no hay un único protocolo para "conectarse a una cámara IP"

Una cámara IP puede exponer su video por RTSP genérico, por ONVIF (un
estándar que agrega descubrimiento y control además del stream), o por un
protocolo propietario del fabricante (común en cámaras chinas baratas tipo
iCSee). Un sistema que quiere soportar "cualquier cámara IP" doméstica no
puede asumir un único mecanismo — pero tampoco quiere que el código que
consume una cámara (el puente WebRTC) sepa nada sobre estos detalles.

## 2. El contrato: `ICameraAdapter`

```csharp
public interface ICameraAdapter
{
    Task<bool> CanHandleAsync(string ipAddress, string? username, string? password);
    Task<CameraConnection?> ConnectAsync(CameraDescriptor descriptor);
}
```

Cada estrategia de conexión (RTSP genérico, ONVIF, propietaria) implementa
este mismo contrato. `CanHandleAsync` responde "¿yo sé hablarle a esta IP?"
sin comprometerse a nada más; `ConnectAsync` hace el trabajo real de
resolver una URL de stream utilizable, asumiendo que ya se determinó que
este adaptador puede manejar la cámara.

**Aprendizaje clave:** definir el contrato en términos de **dos preguntas
separadas** ("¿puedo manejar esto?" y "conectate y decime cómo") permite
que el orquestador (§3) pruebe estrategias sin comprometerse a ninguna
hasta que una confirme que funciona — es la esencia del patrón *Chain of
Responsibility*: una cadena de manejadores candidatos, cada uno decidiendo
si es su responsabilidad procesar la solicitud.

## 3. El orquestador: `CameraDetectionService`

```csharp
public async Task<CameraConnection?> DetectAsync(CameraDescriptor descriptor)
{
    foreach (var adapter in _adapters) // orden: Onvif → Rtsp → propietarios
    {
        try
        {
            if (await adapter.CanHandleAsync(descriptor.IpAddress, descriptor.Username, descriptor.Password))
            {
                var connection = await adapter.ConnectAsync(descriptor);
                if (connection is not null) return connection;
            }
        }
        catch
        {
            // un adaptador que falla no frena la cascada — se sigue con el próximo
        }
    }
    return null;
}
```

Recorre los adaptadores inyectados **en un orden fijo** (pensado para
probar primero los protocolos "más ricos" — que traerían más metadata, como
ONVIF — antes de caer al genérico RTSP) y el primer adaptador que confirma
`CanHandleAsync == true` y logra conectar, gana. Hoy solo `RtspCameraAdapter`
está registrado en el sistema real (`DECISIONES.md`/`ARQUITECTURA.md`
documentan que ONVIF y adaptadores propietarios están contemplados en el
modelo pero no implementados todavía) — el orden y la cascada ya están
listos para cuando se agreguen.

**Aprendizaje clave — tolerancia a fallos por adaptador:** un adaptador que
lanza excepción al probar (por ejemplo, un timeout de red contra una IP que
no corresponde a ese protocolo) **no debe frenar la cascada completa** — se
atrapa la excepción de ese adaptador puntual y se sigue probando con el
siguiente. Sin esto, una sola cámara "rara" podría bloquear la detección de
todas las demás cámaras de la casa si se procesan en el mismo loop.

## 4. Separar "descubrir/configurar" de "conectar en cada uso"

Dos modelos distintos marcan una decisión de diseño importante:

- **`CameraDescriptor`** — resultado de la detección, que se hace **una
  sola vez** (al agregar la cámara) y se persiste (campo `AdapterUsed`)
  para no tener que re-detectar en cada conexión posterior.
- **`CameraConnection`** — resultado de una conexión exitosa puntual: URL
  normalizada, soporte de audio/PTZ. Es lo único que el resto del sistema
  (el puente WebRTC) necesita para hacer su trabajo, sin saber qué
  protocolo hay detrás.

**Aprendizaje clave:** cuando "averiguar cómo hablarle a algo" es costoso
(probar rutas RTSP, contactar varios protocolos) pero "hablarle una vez que
ya se sabe cómo" es barato, vale la pena separar esas dos operaciones en
modelos distintos y persistir el resultado de la primera — evita pagar el
costo de descubrimiento en cada uso real.

## 5. `RtspProbe`: un cliente RTSP mínimo sin librerías externas

En vez de traer una librería RTSP completa (con soporte de todo el
protocolo, `SETUP`/`PLAY`/RTP), `RtspProbe` implementa **solo lo mínimo
necesario para confirmar que hay un servidor RTSP real respondiendo**:
abre un socket TCP crudo y manda un `DESCRIBE`.

```csharp
using var client = new TcpClient();
await client.ConnectAsync(ip, 554);
using var stream = client.GetStream();
var request = $"DESCRIBE {url} RTSP/1.0\r\nCSeq: 1\r\n" +
              (credenciales is not null ? $"Authorization: Basic {BuildBasicAuthHeader(credenciales)}\r\n" : "") +
              "\r\n";
await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
var response = await LeerRespuesta(stream);
// éxito si la respuesta es 200 (describe OK) o 401 (pide auth, pero confirma servidor RTSP real)
```

**Aprendizaje clave:** para una operación de *probing* simple (¿hay algo
real del otro lado?), no siempre hace falta una implementación completa del
protocolo — un cliente mínimo hecho a mano, que solo entiende lo justo para
confirmar presencia, es más liviano de mantener y de auditar que traer una
dependencia externa completa para una sola operación puntual. El límite de
esta decisión: **solo soporta Basic Auth** (RFC 2326) — cámaras que exigen
Digest Auth simplemente no son detectadas hoy, una limitación conocida y
documentada, no un intento fallido.

## 6. `RtspCameraAdapter`: probar lo conocido, después una lista de rutas comunes

```csharp
public async Task<CameraConnection?> ConnectAsync(CameraDescriptor descriptor)
{
    // 1. si ya se conoce una StreamUrl de una detección previa, probarla primero
    if (descriptor.StreamUrl is not null && await _probe.ProbeAsync(descriptor.StreamUrl))
        return BuildConnection(descriptor.StreamUrl);

    // 2. si no, iterar rutas comunes por convención de fabricante
    string[] rutasComunes =
    {
        "/stream1",
        "/h264/ch1/main/av_stream",              // Hikvision-like
        "/cam/realmonitor?channel=1&subtype=0",   // Dahua
        "/videoMain",
        "/live/ch0",
        "/11",
        "/",
    };
    foreach (var ruta in rutasComunes)
    {
        var url = $"rtsp://{descriptor.IpAddress}:554{ruta}";
        if (await _probe.ProbeAsync(url, descriptor.Username, descriptor.Password))
            return BuildConnection(url);
    }
    return null;
}
```

**Aprendizaje clave:** cuando no existe un mecanismo de descubrimiento
estandarizado y confiable (como sería ONVIF si estuviera implementado),
una lista curada de **convenciones conocidas por fabricante** (rutas RTSP
típicas de Hikvision, Dahua, y genéricas) es una heurística pragmática y
razonablemente efectiva para cubrir el universo real de cámaras baratas del
mercado — no es elegante, pero es la solución que realmente funciona contra
hardware real sin depender de que cada fabricante implemente bien un
estándar.

## 7. Cómo reproducir este patrón en otro proyecto

1. **Definí el contrato de "puede manejar esto" y "conectate" por
   separado** cuando tengas múltiples estrategias candidatas para la misma
   tarea — permite orquestar una cascada sin comprometerse de antemano.
2. **Un fallo de un candidato en la cascada no debe frenar a los demás** —
   atrapá excepciones por adaptador/estrategia individual, no alrededor de
   todo el loop.
3. **Separá el resultado de "descubrir cómo conectar" (costoso, se hace
   una vez) del resultado de "conectar en un uso puntual" (barato,
   reusa lo ya descubierto)** — persistí el primero para no repetir el
   costo de descubrimiento en cada uso.
4. **Para un simple *probing* de protocolo, un cliente mínimo hecho a
   mano puede ser preferible a una librería completa** — sobre todo si
   solo necesitás confirmar presencia/tipo de servidor, no implementar el
   protocolo entero.
5. **Cuando no hay estándar confiable, una lista curada de convenciones
   conocidas por proveedor/fabricante** (rutas, formatos, endpoints
   típicos) es una heurística válida — documentala como tal, no como un
   descubrimiento genérico, para que quien la mantenga sepa que hay que
   agregar entradas nuevas a mano cuando aparezca hardware distinto.

## 8. Referencias a archivos fuente del proyecto

- `CeleCamIp.Shared/Cameras/ICameraAdapter.cs`
- `CeleCamIp.Shared/Cameras/CameraDetectionService.cs`
- `CeleCamIp.Shared/Cameras/Adapters/RtspCameraAdapter.cs`
- `CeleCamIp.Shared/Cameras/Adapters/RtspProbe.cs`
- `CeleCamIp.Shared/Cameras/CameraDescriptor.cs`, `CameraConnection.cs`
- `CeleCamIp.CameraDiagnostic/Program.cs` (consola que ejercita
  `RtspCameraAdapter` directo, sin Server ni Gateway)
