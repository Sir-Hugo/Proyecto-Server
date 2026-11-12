# Fase 2B — Integración de partidas con PostgreSQL

Verificación realizada el 10 de noviembre de 2026. Esta fase integra el ciclo real de partidas; no inicia 2C. Se conservó el trabajo no confirmado de Fases 1 y 2A. No se hicieron commits, push, despliegues, cambios de secretos ni modificaciones visuales del cliente.

## 1. Archivos creados y modificados

**Creados:**

- `src/Game.Infrastructure/MatchSession.cs`: sesión transaccional, comandos/resultados, error tipado e interfaz de almacenamiento.
- `src/Game.Infrastructure/PostgresMatchSessionStore.cs`: implementación Npgsql de la unidad transaccional del juego.
- `src/Game.Infrastructure/Migrations/002_live_matches.sql`: fecha de inicio, asignaciones activas exclusivas y protección de partidas finalizadas.
- `tests/Game.Server.Tests/InMemoryMatchSessionStore.cs`: doble de almacenamiento exclusivo de pruebas unitarias.
- `tests/Game.Postgres.Tests/LiveMatchFixture.cs`: instrumentos de captura de mensajes y críticos para pruebas reales de base.
- `tests/Game.Postgres.Tests/LiveMatchTests.cs`: 31 casos de ciclo real, concurrencia, privacidad, idempotencia y fallos.
- `tests/Game.Postgres.Tests/HttpSignalRTests.cs`: 3 pruebas HTTP + SignalR + JWT de prueba + PostgreSQL.
- `tests/Game.Postgres.Tests/LiveMigrationTests.cs`: 2 pruebas de upgrade desde esquema 1, con conservación de datos o rechazo de conflictos.
- `docs/phase2b-validation.md`: este informe.

**Modificados en esta fase:**

- `src/Game.Domain/RoundResolver.cs`: se expuso como público el validador puro de planes existente; no se alteró ninguna condición ni regla.
- `src/Game.Infrastructure/PostgresMatchStore.cs`: helpers compartidos de inserción y lectura en la conexión/transacción del llamador; conserva la API de infraestructura de 2A.
- `src/Game.Infrastructure/PostgresServices.cs`: exige configuración de base y registra el store persistente por DI.
- `src/Game.Server/Rooms/MatchRoundService.cs`: usa PostgreSQL en todas las operaciones de partida; memoria solo para rutas de conexiones.
- `src/Game.Server/Rooms/InMemoryRoomService.cs`: creación asíncrona, semáforo por sala, confirmación de matchId/preparación final tras commit; lectura de cierre desde PostgreSQL.
- `src/Game.Server/Rooms/MatchmakingService.cs`: creación persistente antes de retirar la pareja y anunciar partida; elimina copias de estado/participantes de partida en memoria.
- `src/Game.Server/Rooms/RoomEndpoints.cs`: espera operaciones asíncronas, pasa cancelación, devuelve Problem Details y registra fallos de difusión posteriores al commit.
- `src/Game.Server/Rooms/MatchExpirationService.cs`: expiración basada en PostgreSQL; ante fallo de persistencia registra y reintenta en el siguiente intervalo sin detener el host.
- `src/Game.Server/Hubs/GameHub.cs`: autorización/snapshot por partida persistida, sin depender de la sala/cola original; propaga cancelación de conexión.
- `src/Game.Server/Program.cs`: punto de entrada accesible al host funcional de pruebas; conserva la configuración de producción y no ejecuta migraciones.
- `tests/Game.Server.Tests/MatchRoundServiceTests.cs`: adaptación del fixture a la nueva dependencia y lectura asíncrona, conservando los casos originales.
- `tests/Game.Postgres.Tests/PostgresPersistenceTests.cs`: espera esquema 2; para comprobar requestId entre partidas, termina la anterior antes de crear otra con los mismos sujetos, respetando la nueva exclusividad.
- `tests/Game.Postgres.Tests/Game.Postgres.Tests.csproj`: referencia servidor y agrega bibliotecas de prueba HTTP/SignalR 10.0.14, sin nuevas dependencias de producción.
- `README.md`, `ROADMAP.md`, `docs/persistence-design.md`: comportamiento y comandos actuales, límites de recuperación y siguiente fase.
- `docs/asyncapi.yaml`: documenta confirmación duradera y los errores nuevos de infraestructura/asignación activa; no cambia los payloads existentes.
- `env/.env.example`: la conexión deja de describirse como opcional para el servidor; no se editaron archivos reales de entorno.

El estado Git muestra también archivos de las fases anteriores, que ya estaban modificados o sin seguimiento al comenzar. No se descartó ese trabajo. En particular, `001_initial.sql` no fue reescrito.

## 2. Componentes que ahora usan PostgreSQL

El recorrido real es: sala pública/privada o cola → dos participantes válidos/listos → creación duradera y evento inicial → elección de nicknames → planes privados → resolución simultánea → resultado final, abandono o inactividad.

`MatchRoundService` depende de `IMatchSessionStore`, cuya única implementación de producción es `PostgresMatchSessionStore`. El servidor requiere `ConnectionStrings:GameDatabase`; no tiene fallback de partidas en memoria. DI administra el pool Npgsql existente, sin conectar o migrar como efecto de registrar servicios.

La base conserva partida, estado completo de jugadores, participantes y subjects externos separados de UUID, nicknames, fechas, planes pendientes, acuses, huellas, resultados privados y eventos. La creación desde salas/cola confirma participantes y evento inicial en una transacción antes de devolver/publicar matchId. GET/salida de salas consulta el cierre en PostgreSQL y no interpreta una copia inicial de MatchState como progreso actual.

`GameHub.JoinMatch` obtiene pertenencia y snapshot por matchId/subject directamente de la base. Puede usarse con un ID conocido tras crear otra instancia del servicio; no depende de que la sala/cola todavía recuerde la partida.

## 3. Operaciones y datos que siguen en memoria

Las salas, invitaciones, preparación antes de crear partida, pertenencia al lobby y su expiración siguen siendo temporales. También la cola rápida, su expiración y la asociación subject/conexión SignalR. En salas se conserva una referencia inicial a la partida como metadato de matchId, no como autoridad de su estado.

La recuperación automática de salas, colas, conexiones, sesiones de navegador y descubrimiento de una partida activa tras reinicio no está implementada. La base permite consultar el juego confirmado y procesar reintentos por ID, pero esto no equivale a recuperación integral de cliente/servidor.

## 4. Transacciones y concurrencia

Cada mutación toma `SELECT ... FOR UPDATE` sobre la fila de la partida dentro de READ COMMITTED. Se cargan el estado confirmado, participantes, comandos y planes; el callback ejecuta solo validación/reglas síncronas. Después se escriben los cambios y se confirma la transacción antes de devolver el resultado al servicio de transporte.

Para el primer plan se guardan comando y plan privado sin incrementar secuencia. Para el segundo se validan/resuelven ambos mediante `Game.Domain`, se actualiza el estado, se añade un evento, se completan ambos resultados privados y se eliminan los planes dentro de esa misma transacción. Una excepción anterior al commit revierte todas esas escrituras.

Las lecturas de snapshot usan REPEATABLE READ para no mezclar estado de una ronda con comandos/planes de otra. Las sesiones materializadas son locales a cada operación; no se publican ni se reutilizan como caché confirmada antes de persistir. Los tiempos se capturan con precisión de microsegundos para coincidir con timestamptz y con el estado registrado en JSONB.

Una fila distinta permite avanzar partidas independientes. Se probó manteniendo deliberadamente bloqueada una partida mientras otra resolvía su ronda. La exclusividad por usuario se protege además en PostgreSQL mediante `active_match_players`, no por una comprobación previa en memoria.

Las salas serializan preparación/creación por sala mediante SemaphoreSlim, sin mantener monitor lock durante I/O. La cola usa su semáforo para coordinar la pareja mientras crea su partida; no bloquea operaciones de rondas de otros juegos. Si falla creación, no publica matchId ni retira la pareja válida como emparejada.

## 5. Idempotencia persistente

El ámbito de la clave es partida/jugador/requestId. La huella SHA-256 incluye matchId, secuencia esperada y nombres de acciones normalizados, conservando orden. La comprobación sucede bajo el bloqueo de partida y **antes** de rechazar estado finalizado o secuencia obsoleta.

Un reintento idéntico devuelve el acuse y el resultado privado ya guardados; no consume nuevos críticos, aplica daño ni avanza secuencia otra vez. Otra huella con la misma clave produce `idempotency_key_reused`. Dos solicitudes simultáneas del mismo jugador con IDs diferentes permiten un solo plan pendiente; con el mismo ID y contenido se confirma una sola inserción.

Los resultados terminales por abandono/inactividad también se guardan en todos los comandos pendientes afectados, aunque sus jugadores estén desconectados. JSONB puede normalizar espacios y orden de claves; se conserva el contenido compatible, no la representación textual original.

## 6. Eventos, privacidad y publicación SignalR

La secuencia existente se conserva: evento inicial `match.started` en 0; cada resolución incrementa una vez y emite `round.resolved`, incluido el resultado terminal; abandono/inactividad incrementan una vez y emiten `match.ended`. No hay dos eventos con igual secuencia ni evento terminal adicional para una ronda que ya terminó la partida.

Las aceptaciones de plan ya quedan auditadas en `match_commands`/`pending_round_plans`, con identidad, secuencia, solicitud y fecha; no agregan una transición que rompa la secuencia del plan rival. Los nicknames quedan en participantes, con fecha de actualización; `started_at` registra la elección del segundo nickname. El evento inicial conserva el significado de creación usado por el protocolo previo.

Cada evento de ronda v1 guarda `before`, `after`, `firstPlan`, `secondPlan`, `criticalRolls`, `resolution` y `reason`. Un decorador captura las tiradas de la fuente real sin alterar probabilidades. Una prueba vuelve a resolver con esas tiradas y obtiene el mismo estado PostgreSQL, incluidos daño crítico y resultado. No hay UI ni endpoint público de replay/historial.

Los payloads internos nunca se difunden al cliente. Las notificaciones usan las vistas privadas existentes: solo estado propio y resultados permitidos del rival, sin sus planes ni recursos privados. Permanecen los nombres y formas de mensaje de SignalR y las rutas REST. AsyncAPI añade únicamente documentación de durabilidad y errores nuevos.

Los envíos SignalR ocurren después del commit y fuera del bloqueo SQL. Un fallo de envío se registra sin detalles sensibles y no revierte la jugada confirmada. No hay outbox ni garantía de entrega/orden entre commits concurrentes; eso queda para 2C. Un reintento con la misma clave recupera el resultado; `JoinMatch` con ID conocido relee el snapshot.

## 7. Migración nueva y uso local

`002_live_matches.sql` añade:

- `matches.started_at` y coherencia de fechas.
- Ledger `active_match_players`, con PK por auth_subject, unicidad por match/jugador y FK a participante.
- Triggers diferidos que rellenan/liberan asignaciones activas al confirmar las mutaciones.
- Trigger que impide modificar los campos de una partida ya completada, salvo el timestamp técnico de actualización.

El backfill rechaza usuarios asignados a varias partidas activas heredadas: no elimina registros ni decide resultados para resolverlos. La migración se revierte completa en ese caso. Una prueba comprueba upgrade desde 1 con datos válidos conservando su checksum; otra comprueba rechazo sin pérdida ante conflicto heredado. Las fechas de inicio históricas desconocidas permanecen nulas.

Con variables locales preparadas según README:

```powershell
docker compose --env-file env/postgres.env up -d --wait
dotnet run --project tools/Game.Database -- migrate
dotnet run --project tools/Game.Database -- status
dotnet run --project src/Game.Server
```

La versión actual es 2. La conexión se suministra externamente mediante `ConnectionStrings__GameDatabase`; Auth0 conserva su configuración existente. No se modificaron contraseñas locales ni se ejecutaron migraciones sobre bases del usuario. Las pruebas utilizaron contenedores y bases temporales independientes.

## 8. Pruebas y resultados

| Verificación final | Resultado |
| --- | --- |
| `dotnet build ProyectoServer.slnx --configuration Release` | Exit 0; 0 advertencias y 0 errores. |
| `GAME_POSTGRES_TESTS=1` y `dotnet test ProyectoServer.slnx --configuration Release` | Exit 0; dominio 25, servidor 27, PostgreSQL 71. **123 aprobadas, 0 fallidas y 0 omitidas.** |
| Casos conservados de fases anteriores | 87/87 aprobados: 25 de dominio, 27 de servidor y 35 de infraestructura PostgreSQL. |
| Casos nuevos de 2B | 36/36: 31 del ciclo real, 3 HTTP/SignalR y 2 de upgrade. |
| `npm.cmd run lint` | Exit 0, sin errores de ESLint. |
| `npm.cmd run build` | Exit 0; TypeScript/Vite correctos, 45 módulos. |
| `git diff --check` | Sin errores de whitespace. |
| AsyncAPI YAML | Sintaxis válida, sin claves duplicadas; 99 referencias internas resueltas. |

El checksum SHA-256 normalizado de la fuente final de `001_initial.sql` recibida de 2A y conservada en 2B es `233C9068D710D26959D0A27BBE8E3F311C25A5258A904911A4901506E12E53F2`. Durante la revisión se comparó inicialmente con `3B56417B…`, que correspondía al binario intermedio del ensayo temporal de Compose en 2A, antes de su ajuste de validación de secuencia; se corrigió esa referencia de verificación, sin editar 001 ni reescribir historial. Si una base externa conserva un checksum distinto, el migrador lo rechazará: se debe investigar su versión real antes de migrarla y no actualizar el historial a ciegas.

Las pruebas PostgreSQL usan Docker/PostgreSQL 17.11 reales, una base vacía por caso y Npgsql. Las pruebas de concurrencia no usan mocks de base. Los 31 casos usan captura del transporte para verificar notificaciones; las 3 pruebas funcionales ejecutan HTTP, autenticación JWT y SignalR LongPolling reales mediante TestServer sobre PostgreSQL real.

Se verificaron los 16 escenarios mínimos solicitados: creación/participantes; primer plan duradero; segundo plan/resolución; resolución única; secuencia de eventos; reintento procesado; reutilización incompatible; planes simultáneos de ambos; solicitudes simultáneas del mismo jugador; partidas independientes concurrentes; rollback provocado; rechazo tras cierre; autorización; privacidad; nueva conexión/pool/servicio; y reproducción de una ronda crítica desde el evento.

Además: salas públicas/privadas, cola rápida, conflicto entre dos salas para un mismo humano, exclusividad liberada al finalizar, cierre concurrente por inactividad, abandono con jugador sin conexión, persistencia del resultado aunque falle SignalR, y verificación de publicación fuera de una fila bloqueada.

La prueba funcional recorre sala privada → unión con invitación → conexiones y suscripción a sala → ambos listos → creación PostgreSQL → JoinMatch → nicknames → planes simultáneos de rondas sucesivas → victoria → consulta de sala terminada → salida. Otra comprueba HTTP 401 anónimo, HTTP 403 para identidad máquina y rechazo de un no participante por el hub. La tercera provoca fallo al insertar evento de creación: obtiene Problem Details 503 sin datos sensibles, ninguna partida parcial y éxito al reintentar tras retirar el fallo inyectado.

El host funcional usa JWT firmados con clave efímera e issuer local de prueba, mediante opciones sobrescritas solo en el proyecto de tests; no contacta Auth0 ni cambia su proveedor/configuración de producción. Sigue el patrón de [pruebas de integración ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/testing/integration-testing). Las primeras ejecuciones del nuevo host fallaron porque su configuración se aplicaba después del punto de entrada; se corrigió con host settings tempranos. Los resultados de la tabla corresponden a la versión corregida, no a esa ejecución fallida.

## 9. Límites y escenarios no verificados

- No se ejecutó un navegador con usuarios Auth0 reales, renovación de sesión ni cortes reales de WebSocket. La comprobación funcional usa LongPolling y JWT de prueba.
- No se simuló caída del proceso de sistema operativo durante commit ni una desconexión TCP real de PostgreSQL. Sí se probaron fallos SQL que fuerzan rollback completo y recuperación con nuevas conexiones/servicios.
- No se implementó descubrimiento automático de partidas ni restauración completa de salas, cola y conexiones después de reiniciar.
- No se verificó despliegue multiinstancia con difusión SignalR coordinada ni Azure. La consistencia SQL entre instancias de servicio sí se probó; el transporte sigue siendo local al proceso.
- Vite conserva la advertencia preexistente de bundle de 530,38 kB (gzip 153,50 kB); no impide build y el cliente no recibió cambios en esta fase.
- La infraestructura PostgreSQL se habilita en tests con `GAME_POSTGRES_TESTS=1`. Sin ella, esos casos aparecen como omitidos; CI debe habilitarla. Los resultados reportados arriba sí incluyen su ejecución real.

No quedan fallos conocidos que impidan compilar o ejecutar los flujos de esta fase verificados.

## 10. Riesgos y trabajo reservado para 2C

- Sin outbox, un proceso puede morir después del commit y antes de publicar. También pueden llegar notificaciones fuera de orden. Las respuestas persistentes permiten reintentar, pero no garantizan entrega automática.
- El cliente no guarda de forma duradera su requestId ni descubre la partida activa al reiniciar. El snapshot actual tampoco indica plan pendiente ni causa terminal; no debe interpretarse esta integración como recuperación completa.
- Un fallo de conexión durante commit puede dejar al llamador sin saber si se confirmó. Reintentar el mismo requestId resuelve esa ambigüedad para planes. La creación necesita correlación/descubrimiento para recuperar el matchId si se perdió la respuesta o el lobby temporal.
- Al reiniciar, la expiración consulta partidas activas de PostgreSQL aunque no haya conexiones. La política de período de gracia, presencia y reconexión debe definirse antes de ampliar recuperación.
- Las rutas subject/conexión se mantienen por proceso y pueden quedar obsoletas. No hay bus ni coordinador de conexiones entre réplicas.
- El ledger activo y los triggers garantizan asignaciones durante la operación normal; el rol de base de producción debe carecer de privilegios para alterar tablas/triggers. TLS, roles de migración/ejecución, pool, backups y retención siguen pendientes para Azure.
- Los eventos son internos y pueden contener planes; cualquier futura consulta de historial/replay debe filtrar/autorizarlos, sin publicar JSONB interno directamente.

La implementación termina en Fase 2B. No se inició la Fase 2C.
