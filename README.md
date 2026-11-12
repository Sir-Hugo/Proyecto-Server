# Proyecto de servidor de juego

## Etapa 1: dominio inicial

El proyecto usa C# sobre .NET 10 LTS. Aunque los requisitos aceptan .NET 8 o posterior, .NET 8 llega al fin de soporte el 10 de noviembre de 2026, por lo que iniciamos con .NET 10. El modelo del juego vive en `src/Game.Domain`, separado de cualquier transporte, almacenamiento o framework de servidor. Nullable y warnings como errores están activados en `Directory.Build.props`.

Reglas del juego ya incorporadas: dos jugadores, salud máxima de 50, 1 punto de energía al inicio, acciones normales con costo de 1, ataque base de 5, crítico triple, recuperación de 2 puntos al comienzo de cada ronda posterior y expiración tras 5 minutos sin actividad. La partida dura como máximo 10 rondas: si nadie queda sin salud antes, gana quien tenga más salud al resolver la décima ronda; si tienen la misma salud, hay empate. Cada acción de reservar convierte un punto de energía actual en un punto guardado para la siguiente ronda. No hay tope de reserva; la duración máxima mantiene acotadas las acciones a 19 por jugador por ronda. Cada jugador puede usar hasta 5 bloqueos por partida. Un bloqueo cancela por completo un ataque, incluido un crítico o el ataque especial.

El ataque especial se desbloquea desde la ronda 5, cuesta 3 puntos de energía, hace 20 de daño fijo y solo se puede usar una vez por jugador por partida. No puede ser crítico. Si un jugador bloquea en una ronda donde el rival usa tanto el ataque especial como ataques normales, el bloqueo cancela primero el ataque especial.

Los planes de ambos jugadores se consideran simultáneos. Cada jugador debe asignar toda su energía disponible a ataques, bloqueos, reservas o al ataque especial. Los bloqueos cancelan ataques antes de resolver el daño restante. La probabilidad de crítico acordada es 10% para los ataques normales. Si ambos jugadores llegan a cero de salud en la misma ronda, el resultado es empate.

Si un jugador no envía un plan válido durante 5 minutos y el otro sí lo envió, gana el jugador activo. Si ninguno envía un plan dentro del plazo, el resultado por inactividad es empate. El servidor deberá llevar el control de actividad por jugador para aplicar esta regla.

## Etapa 2: Servidor

El host de ASP.NET Core está en `src/Game.Server` y referencia el dominio. Expone `GET /health`, publica la documentación OpenAPI 3.1 en `/docs` (con el JSON en `/openapi/v1.json`) y genera respuestas ProblemDetails para errores HTTP y excepciones no controladas. El contrato `round.plan.submit` (versión 1 en un campo independiente) y su validador estructural están en `Contracts/V1`; el protocolo en tiempo real está documentado en `docs/asyncapi.yaml`. El Hub autenticado de SignalR está en `/hubs/game`; recibe comandos de emparejamiento y planes de ronda, valida que la identidad pertenezca a la partida y envía snapshots privados. Las rondas se resuelven en el servidor cuando ambos jugadores envían planes válidos. Se validan las reglas, la secuencia esperada y la idempotencia por `requestId`: un reintento idéntico recibe el acuse y resultado ya calculados, mientras que reutilizar el identificador con otro contenido se rechaza. Ambos jugadores reciben su resultado individual, sin revelar el plan del rival. También están implementados el cierre por inactividad y el abandono voluntario, que otorga la victoria al rival y comunica el motivo por SignalR. Las conexiones requieren un JWT válido. Para clientes web que usan WebSocket, el servidor acepta `access_token` en la cadena de consulta únicamente para `/hubs/game`. El límite actual es de 8 KiB por mensaje entrante de SignalR. La configuración de autenticación se mantiene en `src/Game.Server/auth` y el proveedor OIDC elegido es Auth0. La autoridad y audiencia de la API se cargan localmente desde variables de entorno, sin guardar valores específicos del tenant en el repositorio.

Desde la Fase 2C, PostgreSQL es la fuente duradera para partidas, salas, membresías, preparación, plazos y notificaciones del outbox. Las conexiones SignalR siguen siendo efímeras y se reconstruyen con la identidad autenticada. La cola rápida se invalida al reiniciar; una pestaña aún abierta vuelve a inscribirse al reconectar.

La gestión de salas REST está en `src/Game.Server/Rooms`: permite crear salas públicas o privadas, listar, consultar, unirse, salir y cambiar preparación. Salas y miembros se recuperan desde PostgreSQL; el límite es de dos jugadores. La actividad renueva la expiración y las salas de espera vencen tras 5 minutos. Cuando ambos miembros están listos, la base impide asignaciones activas duplicadas y la sala conserva su `matchId`. La cola rápida espera hasta 4 minutos y se mantiene deliberadamente por proceso; el cliente recibe estado y se reinscribe tras una reconexión mientras la pestaña continúe abierta.

Para desarrollo local, `src/Game.Server/appsettings.Development.json` permite el origen `http://localhost:5173` para las solicitudes del cliente web y las conexiones WebSocket. En otros entornos, configura `Cors:AllowedOrigins` con los orígenes exactos de confianza; el servidor no inicia si no se configura ninguno. La conexión del cliente usa un access token de Auth0 y no debe registrarse ni compartirse.

### Configuración de Auth0 para el servidor

El servidor usa la validación JWT estándar de ASP.NET Core; no necesita un SDK específico de Auth0. Al configurar Auth0, se registrará una API con el algoritmo de firma RS256 y se usarán los siguientes valores:

- `Authentication:Authority`: el dominio del tenant Auth0, con esquema HTTPS y `/` al final, por ejemplo `https://mi-tenant.us.auth0.com/`.
- `Authentication:Audience`: el identificador de la API registrado en Auth0, por ejemplo `https://game.example.com`.

Los valores reales para Authority y Audience se encuentran en la carpeta `env`

El cliente deberá solicitar a Auth0 un *access token* para el identificador de esta API. `Authority` y `Audience` no son contraseñas ni claves privadas, pero dependen del tenant y se mantienen fuera del repositorio junto con la configuración local. La carpeta `env` está excluida en `.gitignore`.

## Pruebas automatizadas — Fase 1

La solución `ProyectoServer.slnx` agrupa el dominio, el servidor y los dos proyectos de pruebas xUnit para .NET 10. Desde la raíz:

```powershell
dotnet build src/Game.Server/Game.Server.csproj --configuration Release
dotnet test --configuration Release
```

Las pruebas no necesitan Azure, PostgreSQL ni credenciales Auth0. El dominio usa una fuente de críticos determinista y las pruebas de componentes capturan los mensajes SignalR en memoria. No sustituyen las pruebas de integración con sesiones reales.

Para el cliente, dentro de `web-client`:

```powershell
npm.cmd ci
npm.cmd run lint
npm.cmd run build
```

En PowerShell se usa `npm.cmd` para evitar depender de la política de ejecución de scripts del lanzador `npm.ps1`. El informe de resultados y límites de esta fase está en [docs/phase1-validation.md](docs/phase1-validation.md).

## PostgreSQL local — Fases 2A y 2B

Requisitos: .NET 10, Docker con contenedores Linux y Docker Compose. PostgreSQL es obligatorio para el servidor de juego. Las migraciones y pruebas no necesitan credenciales Auth0; el servidor conserva Auth0 para autenticar a usuarios reales.

1. Copia `env/postgres.env.example` a `env/postgres.env` **solo si no existe** y configura una contraseña local. El archivo real está ignorado por Git. No sobrescribas configuraciones existentes. Cambia `POSTGRES_PORT` si 5433 está ocupado; la publicación está limitada a `127.0.0.1`.
2. Inicia la base desde la raíz:

```powershell
docker compose --env-file env/postgres.env up -d --wait
docker compose --env-file env/postgres.env ps
```

La imagen oficial está fijada a `postgres:17.11-bookworm`, con health check y volumen nombrado `postgres_data`. No se ejecutan migraciones mediante scripts de inicialización del contenedor.

3. Configura `ConnectionStrings__GameDatabase` en el entorno del proceso. ASP.NET Core la lee mediante configuración estándar; los archivos `.env` no se importan automáticamente. Para los valores por defecto del ejemplo, en PowerShell:

```powershell
$pgSecret = Read-Host 'Contraseña configurada en env/postgres.env' -AsSecureString
$pgQuoted = [System.Net.NetworkCredential]::new('', $pgSecret).Password.Replace('"', '""')
$env:ConnectionStrings__GameDatabase = 'Host=localhost;Port=5433;Database=game;Username=game;Password="' + $pgQuoted + '"'
Remove-Variable pgQuoted, pgSecret
```

Si cambiaste puerto, base o usuario, ajusta esos campos. No pongas contraseñas en argumentos de comandos ni publiques la cadena. Usa la misma variable para el servidor y para la herramienta de migraciones.

4. Consulta/aplica migraciones explícitamente, sin arrancar el servidor ni configurar Auth0:

```powershell
dotnet run --project tools/Game.Database -- status
dotnet run --project tools/Game.Database -- migrate
dotnet run --project tools/Game.Database -- status
```

Una base vacía informa versión 0; el esquema actual instala versiones 1 a 4. Las migraciones 001–003 se conservan intactas. La 004 agrega suspensiones de usuarios y auditoría administrativa append-only. Una base con partidas activas duplicadas por usuario debe corregirse explícitamente antes de migrar: el proceso rechaza el conflicto sin borrar datos. Repetir `migrate` conserva los datos. La tabla `schema_migrations` registra versión, nombre, SHA-256 y fecha. Un checksum incompatible o un error SQL hace fallar el comando con código 1 y rollback; no hay `EnsureCreated`, migración automática al arrancar, ni comando de borrado/downgrade.

5. Compila y ejecuta pruebas:

```powershell
dotnet build ProyectoServer.slnx --configuration Release
dotnet test ProyectoServer.slnx
```

Sin habilitación explícita, las 52 pruebas de dominio y servidor se ejecutan y las pruebas PostgreSQL figuran como omitidas. Para ejecutar también las **93 pruebas PostgreSQL actuales**:

```powershell
$env:GAME_POSTGRES_TESTS = '1'
dotnet test ProyectoServer.slnx
# Solo integración, si se desea ejecutarla por separado:
dotnet test tests/Game.Postgres.Tests/Game.Postgres.Tests.csproj --configuration Release
Remove-Item Env:GAME_POSTGRES_TESTS
```

Testcontainers crea PostgreSQL 17.11 con contraseña aleatoria, puerto dinámico y una base vacía por caso; elimina sus contenedores al finalizar. No usa el volumen de Compose ni lee tu conexión local. Con la variable habilitada, Docker no disponible provoca un fallo real; no se convierte en éxito ni en omisión silenciosa. En CI, define siempre `GAME_POSTGRES_TESTS=1` para exigir esta verificación.

6. Detén o reinicia PostgreSQL conservando datos:

```powershell
docker compose --env-file env/postgres.env stop
docker compose --env-file env/postgres.env start
docker compose --env-file env/postgres.env restart postgres
# También conserva el volumen; luego puedes ejecutar up de nuevo:
docker compose --env-file env/postgres.env down
```

No uses `down --volumes` con datos que quieras conservar. Las variables de inicialización de PostgreSQL solo se aplican al crear un volumen vacío; editar la contraseña en el archivo no cambia la de una base ya inicializada. Mantén el mismo nombre de proyecto Compose para reutilizar su volumen.

La conexión es obligatoria en 2B; no existe fallback de partidas en memoria. DI administra el pool Npgsql y se cargan los datos de la partida en cada operación. Las migraciones siguen siendo explícitas y no se ejecutan al arrancar. El usuario de Compose es solo para desarrollo; Azure requerirá TLS y roles distintos de migración/ejecución. Consulta [el diseño](docs/persistence-design.md) y [el informe de verificación](docs/phase2a-validation.md).

## Partidas persistentes — Fase 2B

El flujo de salas públicas/privadas y cola rápida confirma la creación y los dos participantes antes de anunciar el matchId. La base impide que un mismo subject tenga varias partidas activas, incluso si solicitudes concurrentes provienen de salas diferentes. Una partida terminada libera esa asignación y su resultado queda protegido.

`MatchRoundService` usa `PostgresMatchSessionStore`: bloqueo `SELECT ... FOR UPDATE` por partida, validación del dominio, clave idempotente persistente y una sola transacción para estado, evento, resultados privados y eliminación de planes. La primera entrega de un plan persiste sin avanzar secuencia; la segunda resuelve la ronda. Un reintento idéntico recupera acuse/resultado incluso con otra instancia del servicio; otro contenido con el mismo requestId se rechaza. Nicknames, abandono e inactividad también son duraderos.

Los eventos internos conservan estado anterior/posterior, planes y tiradas críticas para reproducir las reglas sin volver a sortear. Nunca se publican esos payloads por SignalR: cada jugador recibe su vista privada existente. Un fallo de base devuelve `database_unavailable` (Problem Details HTTP 503 en salas), sin confirmar la operación; una asignación activa duplicada devuelve `player_already_in_match` (HTTP 409 en salas).

Para iniciar el servidor, configura la conexión y las variables Auth0 existentes, aplica `migrate` y ejecuta:

```powershell
dotnet run --project src/Game.Server
```

Si falla un envío SignalR después del commit, la jugada sigue confirmada; repetir el requestId o `JoinMatch` con matchId conocido recupera resultado/estado. No hay outbox, descubrimiento automático de partidas al reiniciar el navegador ni restauración de salas/cola/conexiones. Tampoco se amplió el snapshot con un indicador de plan pendiente: la recuperación completa corresponde a 2C.

Las pruebas de 2B incluyen concurrencia PostgreSQL real, upgrade desde esquema 1 y un recorrido REST + SignalR LongPolling completo con dos clientes y JWT de prueba firmado con clave efímera. No sustituyen una prueba de navegador con Auth0/WebSocket reales. Resultados y límites en [docs/phase2b-validation.md](docs/phase2b-validation.md).

## Recuperación y resiliencia — Fase 2C

La migración `003_session_recovery.sql` agrega salas y miembros duraderos, plazo de ronda, causa terminal y un outbox privado por destinatario. `ResumeMatch` descubre la partida más reciente del `sub` autenticado, vuelve a autorizar la pertenencia, registra la conexión nueva y entrega un snapshot con secuencia, plan pendiente, plazo y resultado terminal. El navegador conserva únicamente el `matchId` no secreto; los tokens continúan bajo el manejo de Auth0.

Cada resolución o cierre inserta sus notificaciones en la misma transacción que el estado y los eventos. `OutboxDeliveryService` las publica después del commit con reintento acotado. La entrega es al menos una vez: el cliente descarta secuencias repetidas. El plazo de ronda reside en PostgreSQL y el proceso de expiración usa el mismo bloqueo de fila que los comandos, por lo que una carrera no resuelve dos veces. Se conserva la regla del sketch: al vencer, gana quien presentó plan o hay empate si nadie lo hizo; no se añadió una acción automática distinta.

Configuración: `MatchLifecycle:RoundTimeoutMinutes`, `MatchLifecycle:DisconnectGraceSeconds`, `SignalR:KeepAliveSeconds` y `SignalR:ClientTimeoutSeconds`. El informe y los comandos exactos están en [docs/phase2c-validation.md](docs/phase2c-validation.md).

## Historial y administración

`GET /api/history` devuelve el historial paginado del `sub` autenticado. El detalle y la reproducción de eventos responden 404 si ese usuario no participó en la partida. La reproducción proyecta cada evento a la perspectiva del solicitante y excluye payloads internos, planes, tiradas críticas y subjects de autenticación.

Los endpoints bajo `/api/admin` consultan partidas activas y conexiones de la instancia, terminan partidas, suspenden o rehabilitan usuarios y consultan auditoría. Exigen el permiso exacto `admin:game` en la claim `permissions`; un nombre de rol por sí solo no concede acceso. En Auth0 se debe crear ese permiso para la API, asignarlo mediante RBAC y habilitar **Add Permissions in the Access Token**. Las terminaciones y suspensiones se registran en `admin_audit_log`, protegido contra UPDATE, DELETE y TRUNCATE. Una suspensión se comprueba tanto en HTTP como en invocaciones SignalR.

La implementación y su validación se describen en [docs/history-administration-validation.md](docs/history-administration-validation.md). Aplicarla en Azure requiere ejecutar primero la migración 004 mediante el Migration Job y luego desplegar una imagen nueva; estas operaciones no se realizan automáticamente.

## Controles de seguridad

Las rutas REST autenticadas aplican límites por `sub` y las solicitudes anónimas por la IP del par directo, sin confiar en headers reenviados. El proxy compartido solo agrupa intentos anónimos contra rutas protegidas: las rutas públicas de SPA, configuración y health checks no tienen esta política, y un JWT válido crea su partición por usuario antes de autorizar. El servidor devuelve HTTP 429 con `Retry-After` al exceder 120 solicitudes por minuto por usuario o 30 anónimas. SignalR limita el transporte, cada conexión, el total por usuario aunque se reconecte y cinco conexiones simultáneas por usuario. Estos contadores son locales a la instancia; el despliegue mantiene una sola réplica y necesita coordinación distribuida antes de escalar.

Kestrel limita los cuerpos HTTP a 64 KiB y SignalR los mensajes a 8 KiB. Container Apps impone HTTPS/WSS mediante `allowInsecure=false`; el servidor no confía en forwarded headers ni redirige el HTTP interno posterior a la terminación TLS. Producción emite HSTS y todas las respuestas incluyen encabezados contra MIME sniffing, framing y filtración de referrer. Los valores se configuran bajo `Http` y `RateLimiting` en `appsettings.json`. La matriz completa, pruebas y limitaciones están en [docs/phase4a-security-validation.md](docs/phase4a-security-validation.md).

## Partidas consecutivas

El cliente conserva en `localStorage` únicamente el identificador de una partida activa. Al completar una partida mantiene el resultado en pantalla, libera esa referencia activa y, al volver al lobby, descarta las referencias de navegación de la sala anterior. Los mensajes SignalR se validan por `matchId` y secuencia dentro de cada partida, de modo que una respuesta tardía de una partida terminada no puede reemplazar una partida nueva.

La recuperación sin un ID explícito consulta exclusivamente `active_match_players`. Una partida finalizada permanece disponible en historial, pero no se selecciona automáticamente como sesión activa. La causa, regresiones y procedimiento de actualización están en [docs/consecutive-matches-validation.md](docs/consecutive-matches-validation.md).

## Contenedor e infraestructura — Fase 3A

El `Dockerfile` multi-stage compila el cliente Vite, publica el servidor y la herramienta de migraciones, y genera una imagen Linux basada en ASP.NET Core 10 chiseled. La imagen expone el puerto 8080 y se ejecuta como usuario no-root. ASP.NET Core sirve la SPA desde el mismo origen que REST y SignalR; `/config/client.json` entrega únicamente el dominio, client ID y audience públicos de Auth0. En desarrollo, las variables `VITE_AUTH0_*` siguen disponibles para el servidor Vite.

Producción exige `ConnectionStrings__GameDatabase`, `Authentication__Authority`, `Authentication__Audience`, `Auth0__ClientId` y al menos un `Cors__AllowedOrigins`. Las migraciones permanecen separadas del arranque. Para construir y ejecutar con el PostgreSQL de Compose:

```powershell
docker build --pull --tag proyecto-server:local .
docker compose --env-file env/postgres.env up -d --wait postgres

$env:AUTH0_AUTHORITY = 'https://TENANT.us.auth0.com/'
$env:AUTH0_AUDIENCE = 'https://game-api'
$env:AUTH0_CLIENT_ID = 'PUBLIC_SPA_CLIENT_ID'
$env:PUBLIC_ORIGIN = 'http://localhost:8080'
# Aplicar migraciones una sola vez con la conexión interna de Compose.
docker compose --env-file env/postgres.env --profile app run --rm --no-deps --entrypoint dotnet server /app/database/Game.Database.dll migrate
docker compose --env-file env/postgres.env --profile app up -d --build --wait
```

El comando de migración reutiliza la configuración del servicio `server`, pero no inicia el servidor. No escribas la conexión ni contraseñas en el repositorio.

Los endpoints operativos son:

- `GET /health/live`: confirma que el proceso responde y no consulta PostgreSQL.
- `GET /health/ready`: ejecuta una consulta mínima a PostgreSQL y devuelve 503 si la dependencia no está disponible.
- `GET /health`: redirige temporalmente a readiness por compatibilidad.

`infra/main.bicep` declara Container Apps, su environment, ACR Basic, PostgreSQL Flexible Server 17, Key Vault, identidad administrada, Log Analytics y Application Insights. Los parámetros de dev y prod son ejemplos sin secretos y dejan la creación de datos, job y aplicación deshabilitada. La configuración inicial mantiene exactamente una réplica. El job manual ejecuta `Game.Database migrate` antes de desplegar una nueva revisión; el servidor nunca migra al arrancar.

CI está en `.github/workflows/ci.yml`. CD está en `.github/workflows/cd.yml` y no realiza ninguna operación Azure mientras `AZURE_DEPLOY_ENABLED` no sea `true`; usa OIDC y ambientes GitHub separados. La preparación, los límites y los pasos exactos para 3B están en [el informe de Fase 3A](docs/phase3a-validation.md), y el cálculo de gasto está en [costos Azure](docs/azure-costs.md).

El preflight local de Fase 3B.1, las correcciones de red privada y la secuencia de bootstrap están en [docs/phase3b-preflight.md](docs/phase3b-preflight.md). En esa subfase la validación quedó bloqueada hasta instalar Azure CLI; la cuenta ya se comprobó durante 3B.2.

La Fase 3B.2 dejó el servidor saludable en Azure Container Apps con PostgreSQL privado y las migraciones 001–003 aplicadas. La validación final con dos usuarios reales sigue esperando las URLs definitivas de Auth0. El estado, costos y comandos están en [docs/phase3b-deployment.md](docs/phase3b-deployment.md).
