# Seguridad — CeleCamIp

Este documento describe el modelo de seguridad actual del sistema y lista
hallazgos concretos detectados al auditar el código, con severidad y
recomendación. **Es un proyecto doméstico/personal**, así que el objetivo no
es un hardening de nivel empresarial, pero sí evitar exposición innecesaria
de credenciales y accesos no intencionados a las cámaras de una casa real.

## 1. Modelo de autenticación actual

- **Una única API key compartida** (`Auth:ApiKey`) protege: la conexión al
  Hub de SignalR (`GatewayHub`, usada tanto por Gateways como por la App) y
  el endpoint `GET /api/ice-servers`.
- No hay usuarios, no hay roles, no hay expiración ni rotación de la key: es
  una constante de configuración comparada por igualdad exacta de string
  (`ApiKeyAuthenticationHandler.HandleAuthenticateAsync`).
- Si la key no está configurada del lado del servidor, **se rechaza todo**
  (fail-closed) — esto es correcto y evita el escenario peor (Hub
  totalmente abierto por un olvido de configuración).
- El endpoint `GET /embed/player.html` **no requiere autenticación en sí
  mismo** — es intencional (la app lo necesita en producción, ver
  `ARQUITECTURA.md`), pero implica que cualquiera que consiga esa URL puede
  ver el HTML/JS del reproductor (sin secretos embebidos). La autenticación
  real ocurre cuando ese JS llama al Hub y a `/api/ice-servers` con el
  `token` que le llega por query string.

**Riesgo de fondo:** cualquiera que obtenga la API key (ver §2) puede: listar
todas las casas y cámaras registradas, ver sus IPs privadas y qué adaptador
usan, y — más importante — **solicitar streams de video en vivo de
cualquier cámara de cualquier casa registrada**, sin que el sistema pueda
distinguir "es el dueño de la casa" de "es cualquier otra persona con la
key". No hay scoping de la key por casa.

## 2. Hallazgo crítico — secretos versionados en texto plano en git

Confirmado con `git ls-files`: los siguientes archivos están **trackeados
por git** (es decir, forman parte del historial del repositorio) y
contienen secretos reales en texto plano:

| Archivo | Secreto expuesto |
|---|---|
| `src/CeleCamIp.App/Services/AppSecrets.cs` | `Auth:ApiKey` real (valor en base64) |
| `src/CeleCamIp.Server/appsettings.json` | `Auth:ApiKey` real + credencial TURN de ExpressTURN (`Username`/`Credential`) |
| `src/CeleCamIp.Server/appsettings.Production.json` | `Auth:ApiKey` real (mismo valor) |
| `src/CeleCamIp.Gateway/appsettings.json` | `Auth:ApiKey` real + credencial TURN + credenciales por defecto de cámara (`admin`/`admin`) |

Esto **contradice explícitamente** los propios comentarios del código: tanto
`AppSecrets.cs.example` como `GatewayOptions.CameraUsername`/`Password`
tienen comentarios diciendo que estos valores "no deberían versionarse en
texto plano" / "deberían ir en User Secrets / variables de entorno / vault".
El mecanismo pensado para evitarlo (`AppSecrets.cs.example` como plantilla,
`AppSecrets.cs` real fuera de git) **no se aplicó**: `.gitignore` no
excluye `AppSecrets.cs` (solo excluye `**/bin/`, `**/obj/`, `publish/`,
`*.user`, `gateway_log.txt`, `.vs/`), así que el archivo real quedó
commiteado igual que el de ejemplo.

**Mitigante actual:** el repositorio es local, con un solo commit
(`02bc17c`) y **sin remoto configurado** (`git remote -v` no devuelve
nada) al momento de esta auditoría. Es decir, hoy estos secretos no están
expuestos públicamente. **El riesgo se materializa en el momento en que
este repositorio se suba a un remoto** (GitHub, GitLab, etc.), incluso
privado, o se comparta de cualquier forma (zip, pendrive, etc.) sin
limpiar el historial.

### Recomendación (antes de compartir el repo de cualquier forma)
1. **Rotar `Auth:ApiKey`** — generar una nueva clave (ej.
   `openssl rand -base64 32`) y actualizarla en los 3 lugares que deben
   coincidir: `appsettings.Production.json` (Server), `.env` del Gateway
   (`AUTH_API_KEY`), `AppSecrets.cs` (App).
2. **Rotar la credencial TURN** en el dashboard de ExpressTURN.
3. **Sacar los archivos de git** (no solo actualizar su contenido — el
   valor viejo queda igual en el historial):
   ```bash
   git rm --cached src/CeleCamIp.App/Services/AppSecrets.cs
   echo "src/CeleCamIp.App/Services/AppSecrets.cs" >> .gitignore
   ```
   Si el repo ya tuviera más de un commit o remoto, además habría que
   reescribir el historial (`git filter-repo` o similar) para purgar el
   secreto viejo — no alcanza con un commit nuevo que lo borre.
4. Mover `Auth:ApiKey` del Server a **User Secrets** en desarrollo
   (`dotnet user-secrets`, el proyecto ya tiene `UserSecretsId` configurado
   en el `.csproj`) y a la variable de entorno/`appsettings.Production.json`
   solo en el servidor de destino, nunca en el repo.
5. Cambiar las credenciales por defecto de cámara (`admin`/`admin`) en
   cualquier instalación real — son las credenciales de fábrica más
   comunes en cámaras IP baratas y un objetivo obvio si alguna vez esa
   configuración se filtra.

## 3. Transporte sin cifrar en desarrollo (aceptado, con matices)

- El Server **no usa `UseHttpsRedirection()`** deliberadamente (ver
  `DECISIONES.md`), y escucha en `http://0.0.0.0:5151` sin TLS.
- En producción (MonsterASP), TLS lo maneja la capa de hosting compartido
  por delante — pero esto **no está verificado en este código**: es una
  suposición de despliegue. Si el Server llegara a exponerse directo sin un
  proxy TLS delante, la API key viajaría en texto plano (query string o
  header) en cada request/negociación de SignalR.
- El Gateway se conecta al Server con `Server:HubUrl` configurable — nada
  impide que apunte a `http://` en producción si no se configura
  explícitamente `https://`. **Recomendación:** validar en el Gateway (o
  al menos documentar fuerte) que `Server:HubUrl` en producción sea
  siempre `https://`.
- El RTSP entre Gateway y cámaras es, por naturaleza del protocolo,
  texto plano dentro de la LAN de la casa (aceptable: es tráfico local, no
  cruza internet).

## 4. Autenticación RTSP débil (`RtspProbe`)

`RtspProbe.BuildBasicAuthHeader` **solo implementa Basic Auth** (RFC 2326),
explícitamente documentado en el código como limitación conocida. Muchas
cámaras IP exigen Digest Auth por defecto; esas cámaras simplemente no son
detectadas hoy (no es un problema de seguridad en sí — es una limitación
funcional — pero vale la pena que quien administre las cámaras sepa que, si
tiene que bajar el nivel de auth de una cámara a Basic para que este
sistema la detecte, está reduciendo la seguridad de esa cámara frente a
otros posibles clientes en la misma LAN).

## 5. Superficie expuesta por `wwwroot/index.html` en Development

El propio código comenta el riesgo: pedir la API key con un `prompt()` del
lado del navegador en el viewer de debug es "cosmético" — cualquiera con el
link llega igual a la página, ve el código fuente, y puede intentar
adivinar o probar claves. **La mitigación real ya implementada es correcta:**
esa página **no se sirve en `Production`** (`UseDefaultFiles`/`UseStaticFiles`
solo se mapean si `Environment.IsDevelopment()`). Verificar en cada
despliegue que `ASPNETCORE_ENVIRONMENT` efectivamente valga `Production` en
el servidor real (el `.csproj` lo fuerza vía `web.config`, ver
`DECISIONES.md` D15) — si por error quedara en `Development`, esta página
de debug quedaría accesible públicamente.

## 6. `.pubxml.user` con credenciales de publicación — verificado OK

`src/CeleCamIp.Server/Properties/PublishProfiles/site89907-WebDeploy.pubxml.user`
típicamente contiene usuario/contraseña de Web Deploy en texto plano (no se
inspeccionó el contenido exacto en esta auditoría por no forzar la lectura
de un archivo con alta probabilidad de contener credenciales activas, pero
el patrón `.pubxml.user` de Visual Studio casi siempre las incluye).

**Verificado con `git ls-files | Select-String pubxml.user`: no devuelve
resultados.** El patrón `*.user` de `.gitignore` está funcionando
correctamente para este archivo — a diferencia de `AppSecrets.cs` (§2), acá
el mecanismo de exclusión sí cumplió su objetivo. No requiere acción
adicional; solo mantener el patrón `*.user` en `.gitignore` si se edita ese
archivo en el futuro.

## 7. Falta de rate limiting / abuso del Hub

`GatewayHub` no tiene ningún límite de tasa sobre `RequestStream`,
`RegisterHouse`, etc. Un cliente autenticado (con la key válida) podría, en
teoría, generar muchas sesiones `WebRtcCameraSession` en el Gateway
(cada una lanza un proceso `ffmpeg` nuevo) simplemente invocando
`RequestStream` repetidamente. No hay un límite de sesiones concurrentes
por cámara ni por viewer. Para un sistema de una sola casa de confianza esto
es un riesgo bajo hoy, pero se vuelve relevante si la key llegara a
filtrarse (ver §2) — sería una vía sencilla de agotar recursos del Gateway
(CPU/red de la casa) con múltiples procesos `ffmpeg` simultáneos.

## 8. Resumen priorizado

| # | Hallazgo | Severidad | Acción |
|---|---|---|---|
| 1 | API key y credencial TURN commiteadas en texto plano | **Alta** (si el repo se comparte/publica) | Rotar + purgar del historial + `.gitignore` |
| 2 | Credenciales de cámara por defecto `admin`/`admin` en ejemplo | Media | Cambiar en cualquier instalación real |
| 3 | Sin scoping de la API key por casa (una key ve/controla todo) | Media | Aceptable para 1 casa; revisar si se agregan más casas de dueños distintos |
| 4 | Sin garantía de TLS en `Server:HubUrl` del Gateway ni validación de esquema | Media | Documentar/forzar `https://` en producción |
| 5 | Sin rate limiting en el Hub | Baja (hoy), sube si se filtra la key | Agregar límite de sesiones concurrentes por viewer/cámara |
| 6 | RTSP solo Basic Auth | Baja (limitación funcional, no de exposición) | Evaluar agregar Digest si hace falta soportar más cámaras |
| 7 | `.pubxml.user` versionado | Verificado sin hallazgo | No requiere acción — confirmado fuera de git |
