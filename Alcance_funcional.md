# Alcance funcional — CeleCamIp

## Objetivo del proyecto

Permitir ver, desde una app móvil (Android), las cámaras IP instaladas en una
casa, sin depender de que el usuario tenga que abrir puertos en su router ni
lidiar con CGNAT del ISP. La idea de fondo: la casa (el "Gateway") es quien
inicia la conexión hacia afuera, no al revés.

## Qué funciona hoy

- **Descubrimiento de casas y cámaras por WAN.** El Gateway, corriendo en la
  red de la casa, se conecta hacia un Server público (hoy en MonsterASP) y le
  reporta qué cámaras encontró (probando rutas RTSP comunes contra las IPs
  configuradas). La app móvil, desde cualquier red con internet, se conecta a
  ese mismo Server y ve en tiempo real qué casas están online y qué cámaras
  tiene cada una. **Esta parte sí funciona por datos móviles / cualquier red.**
- **Autenticación básica del Hub** con una API key compartida, para que no
  cualquiera en internet pueda conectarse al Server y listar casas/cámaras.
- **Video en vivo de una cámara, reproductor embebido en la app**, usando
  LibVLCSharp para reproducir el stream RTSP directo de la cámara.
- **Botón "Abrir en VLC"** como alternativa manual si el reproductor
  embebido falla (hoy solo tiene implementación real en Windows).
- **APK instalable por sideload** (sin pasar por Google Play), para probar en
  cualquier celular Android.

## Qué NO funciona todavía (limitación importante)

**Ver el video de una cámara por datos móviles / fuera de la LAN de la casa
no funciona todavía.** El reproductor de la app se conecta **directo** a la
IP privada de la cámara (`rtsp://.../192.168.0.25:554/`). Esto solo funciona
si el celular está en la misma red (o una red con ruta) que las cámaras. Por
eso las pruebas hechas hasta ahora, aunque el Server esté en internet, siguen
necesitando que el celular esté en la Wi-Fi de la casa (o una red con acceso
a esa LAN) para que el video efectivamente cargue.

Existe un plan para resolver esto (puente RTSP → WebRTC con relay TURN,
descripto en `Arquitectura.md`), y su parte de señalización ya está
construida en el Server y el Gateway, pero **todavía no está conectada a la
pantalla de reproducción de la app**. Ver `Desiciones.md` para el porqué de
esta secuencia.

## Flujos de usuario actuales

1. Abrir la app → se conecta automáticamente al Server.
2. Ver la lista de casas conocidas, con su estado (online/offline) y cantidad
   de cámaras.
3. Entrar a una casa → ver la lista de sus cámaras.
4. Tocar "Ver en vivo" en una cámara → se abre la pantalla de reproducción,
   que intenta reproducir el RTSP directo con el reproductor embebido.
5. Si el reproductor embebido falla, hay un botón para intentar abrirlo en
   VLC (hoy solo funcional en Windows).

## Fuera de alcance (por ahora)

- Grabación / DVR de las cámaras.
- Notificaciones push por movimiento.
- Múltiples usuarios / cuentas con permisos distintos (la API key es única y
  compartida por todos los clientes).
- Soporte iOS (la app es multiplataforma a nivel de código pero solo se probó
  y empaquetó para Android hasta ahora).
