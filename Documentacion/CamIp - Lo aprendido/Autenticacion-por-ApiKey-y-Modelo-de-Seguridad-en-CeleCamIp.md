# Autenticación por API key y modelo de seguridad en CeleCamIp

> Documento de aprendizaje — serie "Documentacion/CamIp". Objetivo: explicar
> cómo `CeleCamIp` implementa un esquema de autenticación mínimo viable
> (una única API key compartida) para un Hub de SignalR que debe atender
> tanto a un cliente .NET como a JavaScript en un navegador embebido, y qué
> hallazgos de seguridad reales dejó esta decisión al auditar el código —
> con foco en lecciones generalizables sobre secretos y diseño
> "fail-closed".
>
> Fuente: `CeleCamIp.Server/Auth/ApiKeyAuthenticationHandler.cs`,
> `CeleCamIp.Server/Program.cs`, `Documentacion/DECISIONES.md` (D3, D15),
> `Documentacion/SECURITY.md` (completo).
>
> Complementa: `Senalizacion-WebRTC-y-NAT-Traversal-sin-Port-Forwarding-en-CeleCamIp.md`
> (qué protege exactamente esta autenticación).

---

## 1. La decisión: una única API key compartida, no un sistema de usuarios

**Decisión (`DECISIONES.md` D3):** todo cliente (Gateway o App) que quiera
hablar con el `Server` manda la misma API key configurada en `Auth:ApiKey`,
comparada por igualdad exacta de string. No hay usuarios, roles,
expiración ni rotación automática.

**Por qué:** para un proyecto doméstico/personal (una casa, o pocas casas de
confianza), construir un sistema de cuentas completo sería sobre-ingeniería
— el objetivo mínimo es que el Hub no quede completamente abierto a
cualquiera en internet, no aislar a múltiples usuarios entre sí con
distintos niveles de acceso.

**Aprendizaje clave:** la complejidad de un sistema de autenticación debería
ser proporcional al número real de partes que necesitan distinguirse entre
sí. Una key compartida es una decisión razonable **mientras el supuesto que
la sostiene siga siendo cierto** ("todos los clientes son de confianza
mutua") — el propio documento de decisiones lo marca explícitamente como un
trade-off a revisar si el proyecto creciera a múltiples casas de dueños
distintos (ver `SECURITY.md` hallazgo #3).

## 2. El handler: tres fuentes posibles para la misma credencial

```csharp
protected override Task<AuthenticateResult> HandleAuthenticateAsync()
{
    var configuredKey = _configuration["Auth:ApiKey"];
    if (string.IsNullOrEmpty(configuredKey))
        return Task.FromResult(AuthenticateResult.Fail("Auth:ApiKey no configurada")); // fail-closed

    var providedKey =
        Request.Query["access_token"].FirstOrDefault() ??
        Request.Headers["X-Api-Key"].FirstOrDefault() ??
        ExtraerBearerToken(Request.Headers["Authorization"]);

    if (providedKey != configuredKey)
        return Task.FromResult(AuthenticateResult.Fail("API key inválida"));

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "authenticated-client") }, Scheme.Name);
    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
}
```

**Por qué tres fuentes distintas para la misma key:** el `HubConnection` de
.NET (usado por el Gateway y por la App) manda credenciales como header
`Authorization: Bearer <token>`. El cliente JS de SignalR corriendo dentro
del `WebView`/navegador embebido, en cambio, **no puede** mandar headers
custom en la conexión WebSocket inicial por limitaciones del navegador — su
única vía práctica es la query string (`access_token`). Soportar las tres
formas en el mismo handler permite que **el mismo Hub** atienda a ambos
tipos de cliente sin duplicar lógica de autenticación.

**Aprendizaje clave:** al integrar autenticación en un protocolo de tiempo
real (WebSockets/SignalR) que va a ser consumido tanto por un cliente
nativo como por JavaScript de navegador, verificar de antemano **qué
mecanismos de transporte de credenciales soporta cada entorno** — no asumir
que "headers custom" funciona en todos lados. Diseñar el handler para
aceptar la credencial desde múltiples ubicaciones (con un orden de
prioridad claro) es más simple que forzar un único mecanismo y después
tener que hacer excepciones.

## 3. Fail-closed: rechazar todo si falta configuración

Si `Auth:ApiKey` no está configurada en el servidor, el handler **rechaza
absolutamente toda solicitud**, en vez de, por ejemplo, tratar "sin key
configurada" como "sin autenticación requerida".

**Aprendizaje clave — fail-closed vs fail-open:** ante un estado de
configuración ambiguo o faltante, un sistema de autenticación debería
**negar acceso por defecto** (fail-closed), no otorgarlo (fail-open). Un
descuido de despliegue (olvidarse de configurar la key en un entorno nuevo)
se traduce en "el sistema no funciona hasta que se corrija" — un problema
visible e inmediato — en vez de "el sistema queda completamente abierto sin
que nadie lo note" — un problema silencioso y mucho más peligroso. Esta es
la propiedad de diseño de seguridad más barata de aplicar y una de las más
efectivas: cuando dudes de qué hacer ante configuración faltante, negá el
acceso.

## 4. El endpoint que intencionalmente no requiere auth propia

`GET /embed/player.html` sirve el HTML/JS del reproductor **sin exigir
autenticación en el endpoint HTTP en sí** — es una decisión deliberada
(la app lo necesita cargar en producción sin fricción), no un descuido. La
autenticación real ocurre un paso después: ese JS usa el `token` recibido
por query string para autenticar sus propias llamadas al Hub y a
`/api/ice-servers`.

**Aprendizaje clave:** no todo endpoint necesita el mismo nivel de
protección — lo que importa es que el **dato sensible** (poder pedir video
real de una cámara) esté protegido, no necesariamente el HTML/JS estático
que arma la página que lo va a pedir. Documentar explícitamente por qué un
endpoint queda abierto (y qué es lo que sí sigue protegido detrás) evita que
alguien lo "arregle" después sin entender el diseño original.

## 5. El hallazgo más grave encontrado en la auditoría: secretos versionados en git

Al inspeccionar `git ls-files`, se confirmó que varios archivos con
secretos reales en texto plano estaban trackeados por git — es decir, ya
formaban parte del historial del repositorio:

| Archivo | Secreto expuesto |
|---|---|
| `AppSecrets.cs` (App) | API key real |
| `appsettings.json` / `appsettings.Production.json` (Server) | API key + credencial TURN |
| `appsettings.json` (Gateway) | API key + credencial TURN + credenciales de cámara por defecto |

Lo notable no es solo el hallazgo en sí, sino que **contradice explícitamente
comentarios que ya existían en el propio código**: tanto
`AppSecrets.cs.example` como las opciones de credenciales de cámara tenían
comentarios diciendo que esos valores "no deberían versionarse en texto
plano". El mecanismo pensado para evitarlo (`.example` como plantilla, el
archivo real fuera de git) **no se aplicó** porque `.gitignore` no excluía
el archivo real.

**Aprendizaje clave — la intención documentada no es una garantía:** un
comentario en el código que dice "esto no debería estar en git" no impide
que termine estando en git si el mecanismo técnico correspondiente
(`.gitignore`, hooks de pre-commit, escaneo de secretos en CI) no está
efectivamente configurado y verificado. La intención buena y la
configuración correcta son cosas distintas — solo la segunda previene el
problema. Vale la pena, en cualquier proyecto, correr `git ls-files` contra
la lista real de archivos con secretos esperados y confirmar que ninguno
aparezca, en vez de confiar en que el `.gitignore` "debería" estar
cubriéndolos.

## 6. Cuando la mitigación de contenido no alcanza: hay que purgar el historial

La recomendación del propio documento de seguridad no es solo "actualizar
el valor" sino:

```bash
git rm --cached src/CeleCamIp.App/Services/AppSecrets.cs
echo "src/CeleCamIp.App/Services/AppSecrets.cs" >> .gitignore
```

y, si el repo tuviera más de un commit o remoto, reescribir el historial
(`git filter-repo` o equivalente) — **un commit nuevo que borra el secreto
no lo saca del historial**, sigue recuperable desde cualquier commit
anterior.

**Aprendizaje clave:** un secreto commiteado no se "arregla" rotándolo en
el archivo actual — hace falta (a) rotar el secreto en el sistema real
(porque el valor viejo ya está potencialmente comprometido, sin importar
qué se haga con el repo) y (b) tratar el problema del repo como un problema
de historial completo, no de estado actual.

## 7. Una decisión de infraestructura que casi introduce un bug de seguridad: `web.config` trunca `=`

**Decisión (`DECISIONES.md` D15):** en el hosting compartido usado
(MonsterASP), el mecanismo de variables de entorno vía `web.config`
**trunca valores que terminan en `=`** — que es exactamente el padding
típico de una API key en base64. Por eso la key de producción se puso
directo en `appsettings.Production.json` en vez de pasarla por ese
mecanismo.

**Aprendizaje clave:** el mecanismo "estándar" para pasar secretos
(variables de entorno) no es universalmente seguro contra todos los formatos
de secreto en cualquier plataforma de hosting — vale la pena probar el
mecanismo elegido con el formato real del secreto (incluyendo caracteres
especiales, padding, etc.) antes de asumir que funciona, especialmente en
hosting compartido con comportamientos no estándar.

## 8. Cómo reproducir este modelo (y evitar sus errores) en otro proyecto

1. **Elegí el nivel de autenticación proporcional al número real de partes
   de confianza distintas** — una key compartida es válida para pocos
   clientes de confianza mutua; documentá explícitamente cuándo dejaría de
   serlo.
2. **Diseñá el punto de autenticación para aceptar la credencial desde
   todas las ubicaciones de transporte que tus clientes reales necesiten**
   (header, query string, bearer) — sobre todo si vas a tener clientes
   nativos y JS de navegador consumiendo el mismo canal en tiempo real.
3. **Fail-closed siempre que la configuración de seguridad esté ausente o
   sea ambigua** — negar acceso por defecto es más barato de diagnosticar
   que un sistema abierto por accidente.
4. **No todo endpoint necesita el mismo nivel de protección** — identificá
   qué es lo realmente sensible (la operación, no necesariamente el HTML
   estático que la solicita) y protegé eso específicamente.
5. **Verificá con una herramienta real (`git ls-files`, un scanner de
   secretos) que los archivos con secretos reales no estén trackeados** —
   no confíes en comentarios de código ni en la existencia de un
   `.gitignore` sin confirmarlo contra el estado real del repositorio.
6. **Un secreto commiteado se resuelve rotándolo en el sistema real y
   purgando el historial de git**, no con un commit que borra el valor
   actual.
7. **Probá el mecanismo de secretos de tu plataforma de hosting con el
   formato real de tu secreto** antes de asumir que un estándar (variables
   de entorno) se comporta igual en todos lados.

## 9. Referencias a archivos fuente del proyecto

- `CeleCamIp.Server/Auth/ApiKeyAuthenticationHandler.cs`
- `CeleCamIp.Server/Program.cs`
- `Documentacion/DECISIONES.md` (D3, D15)
- `Documentacion/SECURITY.md` (completo, incluida la tabla priorizada de
  hallazgos)
