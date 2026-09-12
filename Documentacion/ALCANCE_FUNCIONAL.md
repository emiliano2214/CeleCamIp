# Alcance funcional — CeleCamIp

> Esta versión reemplaza y corrige a `Alcance_funcional.md` (raíz del repo),
> que quedó desactualizado respecto del código real. Ver `DECISIONES.md` §
> "Estado del repo al documentar" para el detalle de la discrepancia
> encontrada y por qué esta versión es la vigente.

## Objetivo del proyecto

Permitir ver, desde una app móvil (Android, con soporte parcial en
Windows), las cámaras IP instaladas en una casa, sin depender de que el
usuario tenga que abrir puertos en su router ni lidiar con CGNAT del ISP.
La casa (el "Gateway") es quien inicia la conexión hacia afuera, no al
revés.

## Qué funciona hoy (verificado contra código)

- **Descubrimiento de casas y cámaras por WAN.** El Gateway, corriendo en la
  red de la casa, se conecta hacia un Server público (hoy en MonsterASP) y
  reporta qué cámaras encontró (probando rutas RTSP comunes contra las IPs
  configuradas en `House:CameraIps`). La app móvil, desde cualquier red con
  internet, se conecta a ese mismo Server y ve en tiempo real qué casas
  están online y qué cámaras tiene cada una.
- **Autenticación básica del Hub** con una API key compartida (una sola
  clave para todo el sistema — no hay usuarios ni permisos diferenciados).
- **Reproducción de video vía el puente RTSP→WebRTC.** A diferencia de lo
  que documentaba la versión anterior de este archivo, el código actual
  (incluyendo cambios sin commitear al momento de esta auditoría) **sí
  tiene esta pantalla cableada**: `CamerasPage` → "Ver en vivo" navega a
  `CameraPlayerPage`, que carga un `WebView` apuntando a
  `/embed/player.html` con `houseId`/`cameraId`/`token`. Esa página hace
  WebRTC real (RTCPeerConnection nativo del navegador embebido) contra el
  Gateway, vía señalización relayada por el Server. **Esto en principio
  funciona desde cualquier red**, no solo desde la LAN de la casa — sujeto
  a que ICE/TURN logren establecer la conexión (ver limitaciones abajo).
- **Botón "Abrir en VLC"** como alternativa manual usando el RTSP directo de
  la cámara — solo tiene implementación real en Windows (busca `vlc.exe` en
  rutas de instalación conocidas); en cualquier otra plataforma muestra un
  mensaje de "no implementado".
- **Herramienta de diagnóstico de cámaras** (`CeleCamIp.CameraDiagnostic`,
  consola standalone) para probar si una IP responde RTSP y con qué ruta,
  sin tener que levantar Server/Gateway/App completos.

## Qué NO funciona todavía o es frágil

- **El puente RTSP→WebRTC depende de un pipeline de video hecho a mano y
  sensible al firmware de cada cámara.** `FFmpegProcessSource` parsea NALs
  H264 manualmente e invoca FFmpeg con parámetros ajustados por prueba y
  error contra hardware específico (ver el historial detallado de bugs en
  `DECISIONES.md`, secciones D6-D11). Cámaras con comportamientos de
  firmware distintos a los ya probados (por ejemplo, que no soporten UDP,
  o que usen un profile H264 más exigente) pueden requerir ajustes nuevos.
- **Sin reconexión automática del video si se corta la sesión WebRTC.** Si
  el `RTCPeerConnection` cae (ICE `failed`/`disconnected`), la app solo
  muestra un alert; el usuario tiene que salir y volver a entrar a la
  pantalla para reintentar. No hay backoff ni retry automático de la sesión
  de video en sí (sí lo hay para la conexión SignalR de registro/listado).
- **Dependencia de un TURN gratuito de un solo datacenter** (ExpressTURN
  free tier), sin distribución geográfica. Si casa y viewer están lejos de
  ese datacenter, el relay agrega latencia real y aumenta el riesgo de que
  ICE no cierre a tiempo. Documentado como limitación conocida y sin
  resolver en `DECISIONES.md` (D14).
- **Sin pantalla de configuración en la app.** La URL del Server y la API
  key están hardcodeadas en `AppConfig.cs`/`AppSecrets.cs` (requieren
  recompilar para cambiar). No hay forma de que un usuario final apunte la
  app a su propio Server sin tocar código.
- **Modelo de credenciales de cámara plano.** `House:CameraUsername` /
  `House:CameraPassword` son un único usuario/contraseña aplicado a *todas*
  las cámaras de una casa (por defecto `admin`/`admin` en el ejemplo de
  `.env.gateway.example`) — no hay credenciales por cámara.
- **Solo hay un adaptador de cámara implementado (`RtspCameraAdapter`).** El
  modelo de dominio (`CameraDescriptor.OnvifSupported`,
  `CameraDetectionService`) está preparado para más adaptadores (ONVIF,
  protocolos propietarios como iCSee), pero ninguno más existe hoy en el
  código.

## Flujos de usuario actuales

1. Abrir la app → se conecta automáticamente al Server (`AppConfig.ServerHubUrl`).
2. Ver la lista de casas conocidas (`HousesPage`), con su estado
   (online/offline) y cantidad de cámaras. Pull-to-refresh disponible.
3. Entrar a una casa (`CamerasPage`) → ver la lista de sus cámaras
   detectadas, con IP y adaptador usado.
4. Tocar "Ver en vivo" en una cámara → se abre `CameraPlayerPage`, que arma
   la sesión WebRTC vía el Server/Gateway y reproduce el video en el
   `WebView`. Se muestra un loading indicator mientras conecta, y se rota
   automáticamente a pantalla completa en orientación horizontal.
5. Si la reproducción falla, aparece un alert nativo (con el mensaje real
   de error cuando está disponible) sugiriendo probar "Abrir en VLC" si el
   usuario está en la misma red que las cámaras.

## Fuera de alcance (por decisión, no por limitación técnica no resuelta)

- Grabación / DVR de las cámaras.
- Notificaciones push por movimiento.
- Múltiples usuarios / cuentas con permisos distintos (la API key es única
  y compartida por todos los clientes — ver `SECURITY.md`).
- Soporte iOS (la app es multiplataforma a nivel de código MAUI —
  `Platforms/iOS` existe con stubs — pero solo se probó y empaquetó para
  Android hasta ahora; Windows solo se usa en desarrollo).
- Detección automática de cámaras nuevas en la LAN (hoy las IPs son una
  lista fija en configuración; no hay escaneo de red).

## Recomendación para quien retome el proyecto

Antes de seguir agregando funcionalidad, conviene:
1. **Hacer commit de los cambios pendientes** que implementan el puente
   WebRTC (ver `DECISIONES.md`), para que el historial de git refleje el
   estado real y no quede una "baseline" engañosa.
2. **Rotar la API key y la credencial TURN** expuestas en git (ver
   `SECURITY.md`) antes de compartir el repositorio o subirlo a un remoto
   público.
3. Decidir si vale la pena invertir en reconexión automática del video y en
   un TURN con mejor ubicación geográfica antes de dar por "resuelto" el
   caso de uso de ver cámaras fuera de la LAN, dado que hoy es funcional
   pero fràgil.
