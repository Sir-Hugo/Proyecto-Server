# Fase 2A — PostgreSQL y persistencia inicial

Verificación: 10 de noviembre de 2026. Se preservó el trabajo no confirmado de Fase 1 encontrado al iniciar. No se hicieron commits, push, despliegues, cambios de secretos locales ni integración del juego con PostgreSQL.

## Resultado y alcance

Se incorporó infraestructura PostgreSQL reproducible y probada. **El servidor del juego sigue usando sus servicios en memoria.** `Game.Domain`, contratos SignalR, REST, Auth0, reglas, probabilidades y cliente web no recibieron cambios durante esta fase.

El criterio de infraestructura se verificó contra PostgreSQL real en Docker: creación desde cero, migraciones repetibles/concurrentes, persistencia, restricciones, privacidad por filtro de jugador y rollback. Las 52 pruebas originales siguen pasando. Se agregaron **35 casos nuevos**, todos de integración PostgreSQL, sin mocks ni sustitución por SQLite/in-memory.

## Archivos creados

- `compose.yaml`: PostgreSQL oficial 17.11-bookworm, volumen, health check, puerto configurable y bind localhost.
- `env/postgres.env.example`: plantilla sin contraseña real; el archivo local `env/postgres.env` está ignorado.
- `src/Game.Infrastructure/Game.Infrastructure.csproj`.
- `src/Game.Infrastructure/Migrations/001_initial.sql`: esquema, restricciones, índices y triggers.
- `src/Game.Infrastructure/PostgresServices.cs`: configuración externa y registro DI opcional.
- `src/Game.Infrastructure/SchemaMigrator.cs`: historial, checksum, bloqueo y transacción de migración.
- `src/Game.Infrastructure/PostgresMatchStore.cs`: acceso asíncrono parametrizado y transaccional básico.
- `src/Game.Infrastructure/StoredMatch.cs`: modelos de almacenamiento separados del dominio.
- `src/Game.Infrastructure/PersistenceException.cs`: error controlado sin detalles privados del proveedor.
- `tools/Game.Database/Game.Database.csproj` y `tools/Game.Database/Program.cs`: comandos `migrate` y `status`.
- `tests/Game.Postgres.Tests/Game.Postgres.Tests.csproj`.
- `tests/Game.Postgres.Tests/PostgresFixture.cs`: Testcontainers, base vacía por caso y habilitación explícita.
- `tests/Game.Postgres.Tests/PostgresPersistenceTests.cs`: los 35 casos nuevos.
- `docs/phase2a-validation.md`: este informe.

## Archivos modificados

- `.gitignore`: permite versionar únicamente el nuevo ejemplo de PostgreSQL dentro de `env`.
- `env/.env.example`: ejemplo comentado de configuración externa de conexión; sin tocar valores locales.
- `ProyectoServer.slnx`: agrega infraestructura, herramienta de migración y proyecto de integración.
- `src/Game.Server/Game.Server.csproj`: referencia infraestructura.
- `src/Game.Server/Program.cs`: registra los servicios PostgreSQL cuando existe una conexión configurada; no migra ni sustituye servicios existentes.
- `README.md`: preparación, comandos, pruebas y operación local sin perder datos.
- `docs/persistence-design.md`: refleja decisiones implementadas y distingue lo pendiente de 2B.
- `ROADMAP.md`: estado de 2A, espera de instrucciones para 2B y corrección de la mención obsoleta sobre inexistencia de Git.

Los cambios preexistentes de Fase 1 en AsyncAPI, validador, snapshots, App.tsx y sus pruebas fueron conservados. El diff de Git también los muestra porque todavía no estaban confirmados.

## Estrategia de acceso y migraciones

Se eligió **Npgsql 10.0.0 + SQL versionado embebido**, sin EF Core. Encaja con el dominio existente de records y permite usar JSONB y triggers PostgreSQL sin introducir entidades alternativas. Las únicas dependencias de producción adicionales son Npgsql y abstracciones de configuración, DI y logging de Microsoft; la herramienta agrega logging de consola. Testcontainers.PostgreSql 4.16.0 solo se usa en pruebas.

Un `NpgsqlDataSource` singleton administra el pool y DI lo desecha al cerrar el host. Las operaciones usan APIs asíncronas, `await using`, parámetros posicionales y CancellationToken. La lectura de una partida y sus participantes se realiza en un único SELECT consistente. Los planes se consultan por matchId y playerId y no existen endpoints que publiquen estas tablas.

La herramienta requiere `ConnectionStrings__GameDatabase` y no necesita Auth0. `status` es de lectura y muestra versión 0 si el historial no existe. `migrate` toma un advisory lock transaccional, comprueba historia/checksums, aplica solo scripts pendientes y confirma DDL e historial juntos. Se normalizan saltos de línea a LF antes de calcular SHA-256 para reproducibilidad Windows/Linux. Una versión desconocida, checksum cambiado o fallo SQL produce error; la CLI devuelve código 1. No hay EnsureCreated, borrado/downgrade ni migración automática al arrancar ASP.NET.

Los logs registran operación y SQLSTATE; no adjuntan excepciones originales ni datos privados. El data source fija esquema `public`, deshabilita detalles de error y no habilita logging de parámetros. Un error se propaga como `PersistenceException`, sin incluir cadena de conexión, contraseña, subjects ni payloads.

## Tablas, restricciones e índices

| Tabla | Integridad implementada |
| --- | --- |
| `matches` | UUID PK; estado active/completed; ronda 1–10; secuencia no negativa; jugadores distintos; salud 0–50; energía/reserva no negativas sin tope artificial; bloqueos 0–5; especiales booleanos; ganador participante o nulo; ganador nulo al estar activa; fecha terminal coherente con estado y creación; auditoría created/updated/completed. |
| `match_participants` | FK a matches; PK match/jugador; UNIQUE match/subject y match/slot; subject texto no vacío independiente de UUID; slot 1/2; nickname nullable previo a elegirlo o de 1–10 caracteres; auditoría created/updated. Constraint triggers diferidos exigen exactamente dos participantes con los IDs/slots declarados al commit. |
| `match_events` | PK match/secuencia; FK a matches; secuencia no negativa; tipo no vacío; versión positiva; payload JSONB objeto; timestamps de ocurrencia/creación; triggers rechazan UPDATE, DELETE y TRUNCATE. |
| `match_commands` | PK match/jugador/requestId; FK compuesta al participante; SHA-256 de 32 bytes; secuencia aceptada no negativa; acuse y resultado objetos JSONB; resultado y nombre de evento final presentes o ausentes juntos; auditoría created/updated. |
| `pending_round_plans` | PK match/ronda/jugador; UNIQUE match/jugador/requestId; FK al comando de ese participante; array JSONB privado de 1–19 acciones textuales válidas; trigger comprueba ronda activa y secuencia aceptada bajo bloqueo de la partida; timestamp de aceptación. |

Además de los índices de PK/UNIQUE, se agregaron `match_participants(auth_subject, match_id)` y `matches(status, last_activity_at)`; el orden de las PK cubre las consultas de eventos, comandos y planes por partida. `schema_migrations` registra versión, nombre único, checksum y fecha.

Se adaptó la recomendación inicial de FKs circulares a constraint triggers diferidos para poder insertar una partida y sus dos participantes en una transacción normal. Se agregaron reserva explícita para conservar todo PlayerState y `outcome_event_type` para reproducir el `CachedOutcome` real del servidor. Nickname nullable refleja el estado inicial existente antes de su elección. Los detalles y justificación están en el diseño actualizado.

## Comandos de uso

Desde la raíz, prepara `env/postgres.env` a partir del ejemplo sin sobrescribir un archivo existente y elige una contraseña local. Configura `ConnectionStrings__GameDatabase` en el entorno con esos mismos valores; README incluye una entrada de contraseña segura para PowerShell y manejo de comillas. Después:

```powershell
docker compose --env-file env/postgres.env up -d --wait
dotnet run --project tools/Game.Database -- status
dotnet run --project tools/Game.Database -- migrate
dotnet run --project tools/Game.Database -- status
dotnet build ProyectoServer.slnx --configuration Release
$env:GAME_POSTGRES_TESTS = '1'
dotnet test ProyectoServer.slnx
dotnet test tests/Game.Postgres.Tests/Game.Postgres.Tests.csproj --configuration Release
Remove-Item Env:GAME_POSTGRES_TESTS
docker compose --env-file env/postgres.env down
```

`down`, `stop`, `start` y `restart postgres` conservan el volumen. No usar `down --volumes` sobre datos que se quieran conservar. Cambiar variables de inicialización no cambia credenciales de un volumen ya creado.

## Pruebas y resultados verificables

| Comando / comprobación | Resultado final |
| --- | --- |
| `dotnet build ProyectoServer.slnx --configuration Release` | Exit 0; 0 advertencias, 0 errores. |
| `GAME_POSTGRES_TESTS=1` y `dotnet test ProyectoServer.slnx` | Exit 0; dominio 25/25, servidor 27/27, PostgreSQL 35/35. Total 87 aprobadas, 0 fallidas, 0 omitidas. |
| `GAME_POSTGRES_TESTS=1` y `dotnet test tests/Game.Postgres.Tests/Game.Postgres.Tests.csproj --configuration Release` | Exit 0; 35 aprobadas, 0 fallidas, 0 omitidas. |
| `npm.cmd run lint` en web-client | Exit 0; sin errores. |
| `npm.cmd run build` en web-client | Exit 0; TypeScript y Vite correctos; 45 módulos; advertencia de chunk grande preexistente. |
| Compose: config, up con health check, down/up | Correcto, con proyecto y volumen temporales exclusivos de verificación. |
| CLI: status vacío → migrate → migrate → down/up → status | Versión 0 → 1 → 1 → 1; checksum y fecha de aplicación conservados. |
| Versión SQL del contenedor Compose | PostgreSQL 17.11 (Debian 17.11-1.pgdg12+2), linux/amd64. |
| `git diff --check` | Sin errores de whitespace. |

Los 35 casos reales verifican los doce comportamientos pedidos: esquema vacío; migración repetida; inserción de partida y dos participantes; recuperación; evento con secuencia y estado completo; secuencia duplicada; claves duplicadas de participante; rangos de salud/energía/bloqueos; plan pendiente; requestId por partida/jugador; rollback completo; y lectura desde un pool/conexión nuevos. Incluyen también migración concurrente, rollback del DDL ante conflicto, detección de historial alterado, append-only, slots exactos, ganador/fechas, secuencia del plan, FK de jugador, plan duplicado y energía sin límite artificial.

Las pruebas usan PostgreSQL real de la misma versión de Compose, contraseña aleatoria en memoria, puerto asignado dinámicamente y base vacía por caso. Testcontainers limpia sus contenedores. La comprobación de Compose utilizó un proyecto `proyecto-phase2a-<id-aleatorio>` y se retiraron solo su contenedor, red y volumen sintéticos al finalizar. No se creó ni modificó `env/postgres.env`, ni se dejó un servicio de desarrollo configurado con contraseña inventada para el usuario.

## Incidencias resueltas y límites

- Docker Desktop estaba instalado pero apagado. Se inició en segundo plano; Docker 29.8.2 y su motor Linux permitieron ejecutar todas las pruebas. El sandbox requería permisos ampliados para acceder al motor y restaurar paquetes NuGet; ambas operaciones se completaron.
- Una versión inicial más antigua de Testcontainers traía SSH.NET vulnerable; NuGet bloqueó la restauración por warnings como errores. Se actualizó Testcontainers a 4.16.0, sin suprimir auditoría ni advertencias.
- La primera ejecución PostgreSQL tuvo 32 aprobadas y 1 fallida: una comparación textual de JSON ignoraba la normalización de JSONB. Se corrigió a igualdad estructural y las ejecuciones finales pasan. No se alteró el almacenamiento para conservar espacios de JSON.
- Sin `GAME_POSTGRES_TESTS=1`, las pruebas de base figuran como omitidas; esto permite ejecutar la suite original sin Docker. Una vez habilitadas, un motor no disponible provoca fallo real. CI debe habilitarlas explícitamente.
- No se probaron reconexiones reales del navegador, JWT/Auth0, múltiples instancias, recuperación del proceso del juego ni replay de rondas; el juego todavía no consume PostgreSQL. No hay bloqueos pendientes que impidan ejecutar la infraestructura verificada.
- La advertencia de Vite sigue siendo 530,38 kB de JavaScript (gzip 153,50 kB), sin impedir build. No se modificó el frontend.

## Trabajo necesario en Fase 2B — no implementado aquí

| Componente | Cambio pendiente |
| --- | --- |
| `MatchRoundService` | Sustituir fuente de verdad en memoria por transacciones que bloqueen la fila de partida; buscar idempotencia antes de comprobar secuencia; validar con el dominio; confirmar estado, evento versionado, resultados privados y borrado de planes juntos; emitir SignalR solo tras commit. Aplicar el mismo patrón a nickname, abandono e inactividad. Recuperar planes/resultados al reiniciar. |
| `InMemoryRoomService` | Persistir partida y participantes al estar ambos listos, antes de publicar matchId. Su operación actual es síncrona bajo lock: deberá pasar a flujo asíncrono sin mantener un lock de monitor durante I/O. Mantener las salas en memoria hasta definir su propia persistencia. |
| `MatchmakingService` | Confirmar creación duradera antes de retirar definitivamente la pareja y emitir match.found/match.started; definir cómo reponer/reintentar emparejamientos si falla la base. La cola puede seguir en memoria en la primera versión. |
| `GameHub` | Autorizar y recuperar pertenencia/estado desde PostgreSQL por subject, sin depender de que la sala/cola conserve la partida original; reconstruir snapshots privados, estado de plan pendiente y respuestas idempotentes al reconectar. Conservar contratos salvo ampliaciones explícitas compatibles. |

El store de 2A no es todavía una unidad de trabajo de resolución: sus operaciones individuales no deben encadenarse como sustituto de una única transacción de ronda. El formato de evento de ronda y la política de replay/retención deben definirse antes de habilitar la integración.

## Decisiones relevantes para Azure

El esquema usa PostgreSQL estándar sin extensiones obligatorias y conserva UUID internos separados del subject externo. No se requiere Redis en esta fase. Antes de producción hacen falta TLS con verificación de certificado, configuración externa de secretos, usuarios distintos de migración y ejecución, privilegios mínimos (sin DDL/TRUNCATE/modificación de eventos), dimensionamiento del pool, backups/restauración y retención explícita. El usuario inicial de Compose tiene privilegios de desarrollo y no es un modelo de rol de producción.

Los triggers append-only impiden purgar eventos por operaciones normales; un propietario podría deshabilitarlos. Cualquier retención futura debe ser mantenimiento autorizado y respetar idempotencia/auditoría. Las migraciones serán un paso controlado del despliegue, separado del arranque de cada réplica. No se desplegaron recursos externos.

La Fase 2A termina aquí; no se inició la Fase 2B.
