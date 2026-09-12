# Despliegue multi-entorno: Docker/Raspberry Pi y hosting compartido en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: explicar
> las decisiones de despliegue de un sistema con tres componentes que corren
> en entornos completamente distintos (hosting compartido Windows/IIS,
> Linux embebido en Docker, y una app móvil/desktop), y las trampas
> concretas de compatibilidad que aparecen al mezclar Windows y Linux en el
> mismo pipeline de video.
>
> Fuente: `Dockerfile.gateway`, `docker-compose.gateway.yml`,
> `CeleCamIp.Gateway.csproj`, `Documentacion/DEPLOYMENT.md`,
> `Documentacion/DECISIONES.md` (D15).
>
> Complementa: `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md` (por qué
> FFmpeg tiene que estar disponible en cada entorno de despliegue del
> Gateway).

---

## 1. Tres componentes, tres entornos de despliegue distintos

| Componente | Entorno real | Por qué |
|---|---|---|
| `Server` | Hosting compartido Windows/IIS (MonsterASP) | Es lo que ya se tenía disponible; sin acceso a variables de entorno propias del panel |
| `Gateway` | Windows nativo (dev) → Docker en Raspberry Pi (producción prevista) | Debe correr 24/7 dentro de la LAN de la casa, con acceso directo a las cámaras |
| `App` | .NET MAUI, Android (sideload) + Windows (dev) | Cliente final del usuario |

**Aprendizaje clave:** no todos los componentes de un sistema tienen que
compartir el mismo entorno de despliegue — cada uno se elige según sus
restricciones reales (el Gateway necesita estar físicamente en la LAN de la
casa; el Server necesita alta disponibilidad e IP pública fija; la App
necesita llegar al dispositivo del usuario). Diseñar el sistema asumiendo
heterogeneidad de entornos desde el principio (en vez de forzar todo a
Docker, por ejemplo) evita fricción innecesaria.

## 2. El Gateway en Windows: DLLs nativas copiadas a mano

El `.csproj` del Gateway tiene un target de MSBuild que copia DLLs nativas
de FFmpeg al build:

```xml
<Target Name="CopyFFmpegNativeDlls" AfterTargets="Build">
  <!-- copia desde una ruta local de WinGet, específica de la máquina de desarrollo -->
</Target>
```

Esto es necesario porque el binding `SIPSorceryMedia.FFmpeg` (usado solo
para inicialización y un chequeo de salud, no para el pipeline de video
real — ver `Puente-RTSP-a-WebRTC-con-FFmpeg-en-CeleCamIp.md`) espera
encontrar las DLLs nativas de `libav*` junto al ejecutable en Windows, y el
paquete NuGet no las trae incluidas.

**Riesgo documentado explícitamente:** esta ruta es específica de la
máquina donde se desarrolló originalmente. En cualquier otra PC, hace falta
instalar FFmpeg y **actualizar la ruta en el `.csproj`** a mano.
Adicionalmente, el pipeline de video real (`FFmpegProcessSource`, un
proceso separado) busca `ffmpeg.exe` en el `PATH` del sistema — un
requisito **distinto** del anterior (las DLLs del binding vs el ejecutable
del proceso), que hay que satisfacer por separado.

**Riesgo de silencio:** si la versión de FFmpeg instalada no coincide con
la que espera el binding P/Invoke, este falla en silencio — el programa
arranca igual, y como el pipeline de video real no depende de ese binding,
el sistema puede *parecer* funcionar bien. Solo se nota si alguien invoca
manualmente la rutina de diagnóstico.

**Aprendizaje clave:** cuando un componente depende de un binario/librería
nativa externa por **dos vías distintas** (un binding P/Invoke que necesita
DLLs en una ubicación, y un proceso externo que necesita el ejecutable en
el `PATH`), documentar ambos requisitos por separado explícitamente —
"tener FFmpeg instalado" no es una afirmación suficientemente específica
si hay dos mecanismos de acceso distintos con distintos puntos de fallo. Y
cuando una dependencia puede fallar en silencio, vale la pena tener una
rutina de diagnóstico explícita y ejecutarla como parte del checklist de
despliegue, no solo confiar en que "si arrancó, funciona".

## 3. El Gateway en Docker: `network_mode: host` no es opcional

```yaml
services:
  gateway:
    build:
      context: .
      dockerfile: Dockerfile.gateway
    network_mode: host   # necesario para llegar directo a las cámaras de la LAN
    env_file: .env
```

**Por qué:** el Gateway necesita alcanzar las cámaras por su IP de LAN
(`192.168.x.x`) directamente. Si Docker le hiciera NAT/bridge normal al
contenedor (el comportamiento por defecto), el contenedor viviría en una
subred virtual distinta a la de las cámaras reales, y no podría llegar a
ellas sin configuración de red adicional.

**Limitación de plataforma documentada:** `network_mode: host` funciona
directo en Linux (Raspberry Pi OS, el destino real de producción), pero
**tiene soporte limitado o nulo en Docker Desktop para Windows/Mac** (que
corren contenedores dentro de una VM Linux, por lo que "host" termina
siendo el host de la VM, no la máquina física). La recomendación explícita
del proyecto es: para desarrollo en Windows, usar el Gateway nativo (§2),
no Docker — Docker queda reservado para el despliegue real en Linux.

**Aprendizaje clave:** una opción de configuración de Docker que "funciona"
en la documentación oficial puede comportarse de forma completamente
distinta según el sistema operativo host real — verificar el comportamiento
específico de la plataforma de destino (no asumir portabilidad total de
Docker entre Windows/Mac/Linux) es esencial cuando el contenedor necesita
acceso de red no estándar.

## 4. El mismo Dockerfile resuelve FFmpeg distinto que en Windows

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
# ... restaura y publica CeleCamIp.Gateway.csproj en Release ...

FROM mcr.microsoft.com/dotnet/runtime:9.0
RUN apt-get update && apt-get install -y ffmpeg
# ... copia el output publicado ...
```

En el contenedor Linux, FFmpeg viene del **paquete de Debian/Ubuntu**
(`apt-get install ffmpeg`), no de la ruta de WinGet que usa el `.csproj`
para Windows — esas rutas y ese mecanismo de copia de DLLs **no aplican
dentro del contenedor**. Es, en la práctica, dos estrategias de
instalación de la misma dependencia nativa, cada una resuelta con el
mecanismo idiomático de su plataforma.

**Aprendizaje clave:** al portar una dependencia nativa entre plataformas,
no hace falta (ni conviene) reusar el mismo mecanismo de instalación en
ambas — usar el gestor de paquetes nativo de cada plataforma de destino
(`apt` en Debian/Ubuntu, `winget`/descarga manual en Windows) suele ser más
simple y confiable que intentar replicar un mecanismo pensado para un
sistema operativo distinto.

## 5. `appsettings.json` vs variables de entorno vs `.env`: tres mecanismos de configuración para tres entornos

| Entorno | Mecanismo de configuración |
|---|---|
| Server en MonsterASP | `appsettings.{Environment}.json` versionado con valores reales (ver `Autenticacion-por-ApiKey-y-Modelo-de-Seguridad-en-CeleCamIp.md` §7 sobre por qué no variables de entorno) |
| Gateway en Windows (dev) | `appsettings.json` / `appsettings.Development.json` locales |
| Gateway en Docker | `.env` (nunca commiteado) leído por `docker-compose.gateway.yml`, mapeado a variables de entorno dentro del contenedor |
| App | Constantes compiladas en `AppConfig.cs`/`AppSecrets.cs` (sin configuración en runtime) |

**Aprendizaje clave:** un mismo tipo de valor (la API key, en este caso)
puede necesitar viajar por mecanismos de configuración completamente
distintos según el entorno de despliegue de cada componente — no hay una
única "fuente de verdad" de configuración universal cuando los componentes
corren en plataformas de hosting heterogéneas. Lo que sí debería ser único
es el **valor real** (la misma key en los tres lugares), documentado
explícitamente en un checklist de despliegue para evitar que uno de los
tres quede desactualizado tras una rotación.

## 6. El checklist de despliegue como documentación viva de todo lo anterior

El propio proyecto resuelve la coordinación entre estos mecanismos con un
checklist explícito antes de cualquier despliegue "limpio":

1. Rotar secretos si el repo circuló fuera de un entorno de confianza.
2. Confirmar que la API key coincide **exactamente** entre Server, Gateway y
   App (tres mecanismos de configuración distintos, mismo valor).
3. Confirmar `Server:HubUrl` del Gateway apuntando al host público real, con
   `https://` si corresponde.
4. Confirmar `ServerBaseUrl` de la App apuntando al mismo host.
5. Verificar que `ffmpeg` esté disponible en el `PATH` del entorno real del
   Gateway (distinto mecanismo en Windows vs Docker, ver §2 y §4).
6. Probar la herramienta de diagnóstico de cámaras contra cada IP antes de
   darlas de alta en producción.
7. Commitear cualquier cambio pendiente en el working tree antes de
   considerar el despliegue reproducible desde el historial de git.

**Aprendizaje general:** cuando un sistema tiene múltiples componentes con
mecanismos de configuración y entornos de despliegue distintos, un
checklist explícito y versionado (no memoria tribal) es lo que evita que
una rotación de secreto o un cambio de URL quede aplicado en dos de los
tres lugares y no en el tercero — un tipo de bug que no aparece en pruebas
locales (donde suele haber un solo entorno) y solo se manifiesta en
producción real.

## 7. Cómo reproducir este enfoque en otro proyecto

1. **Elegí el entorno de despliegue de cada componente según sus
   restricciones reales** (dónde necesita estar físicamente, qué
   disponibilidad necesita), no por uniformidad — está bien que distintos
   componentes de un mismo sistema corran en plataformas distintas.
2. **Cuando un componente dependa de una librería nativa por dos vías
   distintas** (binding + proceso externo, por ejemplo), documentá ambos
   requisitos de instalación por separado.
3. **No asumas que una opción de Docker (como `network_mode: host`) se
   comporta igual en todos los sistemas operativos host** — verificá contra
   la plataforma de destino real, especialmente para requisitos de red no
   estándar.
4. **Usá el mecanismo de instalación de dependencias nativas idiomático de
   cada plataforma de destino** en vez de forzar el mismo mecanismo en
   todos los Dockerfiles/entornos.
5. **Aceptá que la configuración pueda viajar por mecanismos distintos
   según el entorno**, pero mantené el **valor real** sincronizado
   explícitamente entre todos ellos vía un checklist versionado.
6. **Un checklist de despliegue escrito y versionado es la forma más barata
   de evitar bugs de coordinación** entre componentes con mecanismos de
   configuración heterogéneos — especialmente en sistemas que no tienen un
   pipeline de CI/CD automatizado end-to-end.

## 8. Referencias a archivos fuente del proyecto

- `Dockerfile.gateway`, `docker-compose.gateway.yml`, `.env.gateway.example`
- `CeleCamIp.Gateway/CeleCamIp.Gateway.csproj` (target `CopyFFmpegNativeDlls`)
- `CeleCamIp.Server/Properties/PublishProfiles/site89907-WebDeploy.pubxml`
- `Documentacion/DEPLOYMENT.md` (completo)
- `Documentacion/DECISIONES.md` (D15)
