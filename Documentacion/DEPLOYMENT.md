# Deployment — CeleCamIp

Este documento cubre cómo compilar y desplegar cada uno de los 3 componentes
desplegables: **Server**, **Gateway** y **App**. Incluye tanto el flujo de
desarrollo local como el de producción real usado hoy (MonsterASP + Docker
en Raspberry Pi + APK de sideload).

## 0. Requisitos previos

| Herramienta | Uso |
|---|---|
| .NET 9 SDK | Compilar Server, Gateway, Shared, CameraDiagnostic, ViewerTestClient |
| .NET MAUI workload (`dotnet workload install maui`) | Compilar la App |
| Docker + Docker Compose | Desplegar el Gateway en Linux/Raspberry Pi |
| FFmpeg 8.1.x ("full build", con `avcodec-62`, `avformat-62`, `avutil-60`, `avdevice-62`, `avfilter-11`, `swscale-9`, `swresample-6`) | Runtime del Gateway en Windows (dev) — ver §2.1. En el contenedor Docker/Raspberry Pi el Dockerfile descarga y registra esta misma serie automáticamente, no hace falta instalarla a mano — ver §2.2 y `DECISIONES.md` D17 |
| Visual Studio 2022 17.x o superior (para firmar/publicar la App fácilmente) | Opcional pero recomendado para MAUI |

## 1. Server (`CeleCamIp.Server`)

### 1.1 Desarrollo local
```powershell
cd src\CeleCamIp.Server
dotnet run
```
Escucha en `http://0.0.0.0:5151` (perfil `http` de `launchSettings.json`; el
perfil `https` agrega `https://localhost:7022` además). En `Development`
sirve también `wwwroot/index.html` como viewer de debug.

Configurar `appsettings.Development.json` si se necesita una `Auth:ApiKey`
distinta a la de producción para pruebas locales (hoy ese archivo no la
define, así que **toda autenticación fallará en local** hasta agregarla —
ver `DECISIONES.md` D16 y `SECURITY.md`).

### 1.2 Producción — hosting compartido MonsterASP (estado actual real)

El proyecto usa un **perfil de publicación Web Deploy** ya presente en el
repo:
`src/CeleCamIp.Server/Properties/PublishProfiles/site89907-WebDeploy.pubxml`
(+ su `.pubxml.user` con credenciales, que **no debería estar en git** — ver
`SECURITY.md`).

Pasos típicos (desde Visual Studio, más simple que la CLI para Web Deploy):
1. Abrir `CeleCamIp.sln` en Visual Studio.
2. Click derecho en `CeleCamIp.Server` → **Publicar** → seleccionar el
   perfil `site89907-WebDeploy`.
3. Verificar antes de publicar que `appsettings.Production.json` tiene la
   `Auth:ApiKey` correcta (la que también deben usar el Gateway y la App).
4. Publicar. El SDK genera un `web.config` que fuerza
   `ASPNETCORE_ENVIRONMENT=Production` (por el `<EnvironmentName>` fijado en
   el `.csproj` — ver `DECISIONES.md` D15), necesario porque este hosting
   compartido no tiene panel propio de variables de entorno.

**Importante — por qué la API key va en `appsettings.Production.json` y no
en variables de entorno del hosting:** el mecanismo de variables de entorno
vía `web.config` de este hosting **trunca valores que terminan en `=`**
(el padding típico de una key en base64, como la que usa este proyecto).
Cualquier secreto nuevo que deba pasar por ese mecanismo debe evitar
terminar en `=`, o ir directo en el `appsettings.{Environment}.json` como se
hizo acá.

### 1.3 Publicar por CLI (alternativa sin Visual Studio)
```powershell
cd src\CeleCamIp.Server
dotnet publish -c Release -p:PublishProfile=site89907-WebDeploy
```

## 2. Gateway (`CeleCamIp.Gateway`)

El Gateway tiene dos escenarios de despliegue documentados en el propio
repo: **Windows nativo** (usado en desarrollo) y **Docker en Linux /
Raspberry Pi** (el destino real de producción, verificado y corriendo hoy
en una Raspberry Pi 3B aarch64 — ver `DECISIONES.md` D17).

### 2.1 Windows (desarrollo)

El `.csproj` copia automáticamente las DLLs nativas de FFmpeg al build
(`Target Name="CopyFFmpegNativeDlls" AfterTargets="Build"`), leyendo desde:
```
C:\Users\emiab\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1-full_build-shared\bin
```
**Esta ruta es específica de esta máquina.** Para reproducir el entorno en
otra PC:
1. Instalar FFmpeg 8.1.x "full build shared" (ej. `winget install
   Gyan.FFmpeg.Shared`, o descarga manual desde gyan.dev).
2. Actualizar `<FFmpegNativeDir>` en
   `src/CeleCamIp.Gateway/CeleCamIp.Gateway.csproj` a la ruta real.
3. **Además**, `FFmpegProcessSource` (el pipeline de video real, distinto
   del binding `SIPSorceryMedia.FFmpeg`) busca `ffmpeg.exe` en el `PATH` del
   sistema, o junto al `.exe`, o en `/usr/bin` / `/usr/local/bin` en Linux.
   Asegurarse de que `ffmpeg.exe` esté en el `PATH` (no solo las DLLs).
4. Editar `appsettings.json` / `appsettings.Development.json` con:
   `Server:HubUrl`, `Auth:ApiKey`, `House:Id`, `House:CameraIps`,
   `House:CameraUsername`/`Password`, `IceServers`.
5. `dotnet run` desde `src\CeleCamIp.Gateway`.

**Riesgo de silencio si esto está mal configurado:** si la versión mayor de
FFmpeg instalada no coincide con la que espera `FFmpeg.AutoGen` (usado por
`SIPSorceryMedia.FFmpeg`), el binding falla en silencio — arranca, pero
`GetVideoSourceFormats()` devuelve vacío o lanza. Como el pipeline de video
real (`FFmpegProcessSource`) no depende de ese binding, el sistema puede
*parecer* funcionar igual; solo se nota si se llama `DiagnoseAsync()`
manualmente, o si se corre con el diagnóstico temporal descripto en §2.2.

### 2.2 Docker / Raspberry Pi (producción real, verificada)

Archivo relevante: `Dockerfile` dentro de la carpeta del Gateway (build
multi-stage), usado por el `docker-compose.yml` correspondiente.

**Build multi-stage:**
1. Etapa `build` sobre `mcr.microsoft.com/dotnet/sdk:9.0`: restaura y
   publica `CeleCamIp.Gateway.csproj` en modo `Release`.
2. Etapa final sobre `mcr.microsoft.com/dotnet/aspnet:9.0`: **descarga el
   build estático de FFmpeg 8.1 (serie exacta que pide `FFmpeg.AutoGen
   8.1.0`) desde los releases de BtbN/FFmpeg-Builds**, eligiendo el
   artefacto según `$TARGETARCH` (`linux64` para amd64, `linuxarm64` para
   arm64 — el caso de la Raspberry Pi 3B), y copia sus `.so*` a `/usr/lib/`
   (`ldconfig` después). **No se usa `apt-get install ffmpeg`**: el
   repositorio de Debian va atrás de la serie 8.1 y no expone los símbolos
   con el sufijo de versión mayor exacto que busca `FFmpeg.AutoGen` — ver
   el razonamiento completo en `DECISIONES.md` D17.
   El propio Dockerfile corre `ldd` sobre `libavcodec.so.62` durante el
   build para confirmar que no falten dependencias transitivas antes de
   seguir.

**Importante — `libPath` explícito en Linux:** además de tener los `.so`
correctos, `Program.cs` le pasa a `FFmpegInit.Initialise(...)` un `libPath`
explícito (`/usr/lib`) cuando `OperatingSystem.IsLinux()` es verdadero. En
Linux, la auto-detección de `SIPSorceryMedia.FFmpeg` con `libPath = null`
**no** consulta el cache del linker dinámico (`ldconfig`), así que aunque
las libs estén bien instaladas, sin este parámetro explícito el binding
falla con `System.ApplicationException: Unable to find FFMPEG binaries`.
Ver `DECISIONES.md` D17 para el detalle completo y la traza real de este
error tal como se dio en producción.

**Por qué `network_mode: host`:** el Gateway necesita llegar directo a las
cámaras de la LAN de la casa (IPs `192.168.x.x`) sin que Docker le haga
NAT/bridge en el medio. Esto funciona directo en Linux (Raspberry Pi OS);
**no funciona igual en Docker Desktop para Windows/Mac**, donde
`network_mode: host` tiene soporte limitado o nulo — para desarrollo en
Windows, usar el flujo nativo de §2.1, no Docker.

**Flujo de despliegue real hoy (sin remoto de git configurado — ver
`DECISIONES.md` § "Estado del repo al documentar"):** como el repo todavía
no tiene un remoto, el código no se lleva a la Raspberry Pi por
`git clone`/`git pull`, sino copiando a mano los archivos modificados desde
la PC de desarrollo Windows al host Linux (`abatee.local`, usuario `emiab`)
vía `scp`, hacia `~/projectos/Gateway/CeleCamIp.Gateway/` y
`~/projectos/Gateway/CeleCamIp.Shared/`. Luego, ya en el Pi:
```bash
cd ~/projectos/Gateway
docker compose build --no-cache
docker compose up -d
docker compose logs -f celecamip-gateway
```
**Ojo con este orden:** si se edita el código en Windows pero no se hace el
`scp` de los archivos cambiados antes del `build`, el contenedor se
reconstruye igual (sin `--no-cache` a veces ni eso) pero con el código
viejo, y el síntoma es confuso — el log no refleja ningún cambio reciente.
Confirmar siempre que el `scp` corrió sin error antes de reconstruir.

El flujo alternativo con `git clone` + `.env.gateway.example` +
`docker-compose.gateway.yml` (documentado originalmente más abajo) sigue
siendo el destino deseable una vez que el repo tenga un remoto configurado,
pero **no es el flujo que se usa hoy en la práctica**:
```bash
# Flujo previsto (pendiente de que el repo tenga remoto configurado):
git clone <repo> celecamip
cd celecamip
cp .env.gateway.example .env
nano .env   # completar SERVER_HUB_URL, HOUSE_ID, CAMERA_IP_1..4,
            # CAMERA_USERNAME/PASSWORD, TURN_USERNAME/CREDENTIAL, AUTH_API_KEY
docker compose -f docker-compose.gateway.yml up -d --build
docker compose -f docker-compose.gateway.yml logs -f
```

Variables clave de `.env` (ver `.env.gateway.example` para el listado
completo con valores por defecto de ejemplo):
- `SERVER_HUB_URL` — en producción, la URL pública real del Server
  (`https://<tu-host>/hubs/gateway`).
- `HOUSE_ID` — debe ser único si hay más de una casa/Gateway apuntando al
  mismo Server.
- `CAMERA_IP_1..4`, `CAMERA_USERNAME`, `CAMERA_PASSWORD`.
- `TURN_USERNAME`, `TURN_CREDENTIAL` — del dashboard de expressturn.com (o
  el proveedor TURN que se elija — ver `DECISIONES.md` D14).
- `AUTH_API_KEY` — debe coincidir exactamente con `Auth:ApiKey` del Server.

**`.env` real nunca debe commitearse** (ya está fuera del alcance de
`.gitignore` actual porque no matchea ningún patrón — ver `SECURITY.md`
para agregarlo explícitamente).

## 3. App (`CeleCamIp.App`)

### 3.1 Configuración previa (obligatoria, para cualquier build)
1. Copiar `src/CeleCamIp.App/Services/AppSecrets.cs.example` →
   `AppSecrets.cs` y completar `ApiKey` con el mismo valor que
   `Auth:ApiKey` del Server.
   > **Atención:** en el estado actual del repo, `AppSecrets.cs` real
   > (con la key en texto plano) **ya está commiteado** — ver
   > `SECURITY.md`. Si se rota la key, hay que actualizar este archivo y
   > recompilar.
2. Revisar `src/CeleCamIp.App/Services/AppConfig.cs` →
   `ServerBaseUrl`. Por defecto apunta al Server público de MonsterASP
   (`http://camarasip.runasp.net`). Para probar contra un Server corriendo
   en la misma PC que el Gateway (Windows, desarrollo), cambiar a
   `http://localhost:5151` **y recompilar** (no hay configuración en
   runtime).

### 3.2 Build para Android (sideload, sin Google Play)
```powershell
cd src\CeleCamIp.App
dotnet build -f net9.0-android -c Release
```
Para generar un APK firmado listo para instalar por sideload, usar el
asistente de Visual Studio (**Crear archivo de la aplicación** /
*Publish → Android → Ad-hoc*) o:
```powershell
dotnet publish -f net9.0-android -c Release ^
  -p:AndroidPackageFormat=apk ^
  -p:AndroidKeyStore=true ^
  -p:AndroidSigningKeyStore=<ruta-al-keystore> ^
  -p:AndroidSigningKeyAlias=<alias> ^
  -p:AndroidSigningKeyPass=<pass> ^
  -p:AndroidSigningStorePass=<pass>
```
El `.apk` resultante se instala transfiriéndolo al celular y habilitando
"Instalar apps de orígenes desconocidos" para el instalador usado — no
requiere pasar por Google Play.

`ApplicationId` actual: `com.companyname.celecamip.app` (nombre de plantilla
por defecto de MAUI — considerar cambiarlo antes de una distribución más
amplia, para evitar colisiones si en algún momento se publica en una tienda).

### 3.3 Build para Windows (desarrollo/pruebas de escritorio)
```powershell
cd src\CeleCamIp.App
dotnet build -f net9.0-windows10.0.19041.0 -c Debug
```
`WindowsPackageType=None` en el `.csproj`: se genera un ejecutable
"unpackaged" (no un MSIX), más simple para iterar en desarrollo. Requiere
Windows 10 build 17763 (`TargetPlatformMinVersion`) o superior.

### 3.4 iOS / MacCatalyst / Tizen
Existen carpetas `Platforms/iOS`, `Platforms/MacCatalyst` y
`Platforms/Tizen` generadas por la plantilla estándar de MAUI, pero **nunca
se probaron ni empaquetaron** según `ALCANCE_FUNCIONAL.md`. `LibVLCSharp` no
tiene condicional para iOS en el `.csproj` (solo Android tiene
`LibVLCSharp.MAUI`/`VideoLAN.LibVLC.Android` explícitos), así que "Abrir en
VLC" tampoco tiene implementación ahí (cae en el `#else` de
`VlcLauncher.TryOpen`, que devuelve error). Habría que validar esas
plataformas desde cero antes de considerarlas soportadas.

## 4. Herramientas auxiliares

### `CeleCamIp.CameraDiagnostic`
Ya hay un build publicado (self-contained, ~70MB) en `publish/CameraDiagnostic/`
dentro del repo. Para regenerarlo:
```powershell
cd src\CeleCamIp.CameraDiagnostic
dotnet publish -c Release -r win-x64 --self-contained true -o ..\..\publish\CameraDiagnostic
```

### `tools/ViewerTestClient`
No está en `CeleCamIp.sln`. Para correrlo:
```powershell
cd tools\ViewerTestClient
dotnet run -- casa-dev-01 192.168.0.25
```
Recordar la limitación de `DECISIONES.md` D16: falla contra cualquier
Server con `Auth:ApiKey` configurada, porque no manda el token.

## 5. Checklist antes de un despliegue "limpio" de punta a punta

1. [ ] Rotar `Auth:ApiKey` y credenciales TURN si el repo circuló fuera de
   un entorno de confianza (ver `SECURITY.md`).
2. [ ] Confirmar que `Auth:ApiKey` coincide **exactamente** entre: Server
   (`appsettings.Production.json`), Gateway (`.env` → `AUTH_API_KEY`) y App
   (`AppSecrets.cs`).
3. [ ] Confirmar `Server:HubUrl` del Gateway apuntando al host público real
   (con `https://` si el Server queda detrás de un proxy con TLS).
4. [ ] Confirmar `ServerBaseUrl` de la App apuntando al mismo host.
5. [ ] Verificar que `ffmpeg` esté disponible donde lo necesita el Gateway:
   en Windows, DLLs nativas del `.csproj` + `ffmpeg.exe` en `PATH`; en
   Docker/Raspberry Pi, el Dockerfile descarga e instala la serie 8.1
   automáticamente en el build, y `Program.cs` le pasa `libPath="/usr/lib"`
   explícito en Linux (ver `DECISIONES.md` D17) — no requiere acción manual
   salvo que cambie la versión de `FFmpeg.AutoGen` referenciada.
6. [ ] Probar `CeleCamIp.CameraDiagnostic` contra cada IP de cámara antes de
   darla de alta en `House:CameraIps`, para descartar problemas de
   credenciales/ruta RTSP antes de involucrar todo el pipeline WebRTC.
7. [ ] Hacer commit de cualquier cambio pendiente en el working tree (ver
   `DECISIONES.md` § "Estado del repo al documentar") antes de considerar el
   despliegue "reproducible" desde el historial de git.
