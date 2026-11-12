# Fase 2C — Validación de recuperación, reconexión y resiliencia

Fecha de validación: 10 de noviembre de 2026.

## Estado encontrado antes de continuar

La inspección de `git status`, el diff, el código y los informes anteriores confirmó que el árbol contenía las Fases 1, 2A y 2B completas, sin una implementación parcial de 2C. No existían `003_session_recovery.sql` ni este informe; README, ROADMAP y el diseño aún declaraban salas, cola, conexiones, descubrimiento y outbox como pendientes. Se conservaron todos los cambios existentes y no se modificaron las migraciones 001/002.

## Archivos de esta fase

Nuevos:

- `src/Game.Infrastructure/Migrations/003_session_recovery.sql`
- `src/Game.Server/Rooms/OutboxDeliveryService.cs`
- `tests/Game.Postgres.Tests/Phase2CRecoveryTests.cs`
- `docs/phase2c-validation.md`

Modificados para 2C:

- `src/Game.Infrastructure/MatchSession.cs` y `PostgresMatchSessionStore.cs`
- `src/Game.Server/Hubs/GameHub.cs`, `Program.cs` y `appsettings.json`
- `src/Game.Server/Rooms/InMemoryRoomService.cs` (se conserva el nombre histórico; ahora usa PostgreSQL), `MatchRoundService.cs`
- `web-client/src/App.tsx`
- `tests/Game.Server.Tests/InMemoryMatchSessionStore.cs`, `tests/Game.Postgres.Tests/LiveMatchTests.cs`, `LiveMigrationTests.cs`, `PostgresPersistenceTests.cs` y `HttpSignalRTests.cs`
- `README.md`, `ROADMAP.md`, `docs/asyncapi.yaml`, `docs/persistence-design.md` y `env/.env.example`

El resto de archivos modificados que muestra Git pertenece a las fases anteriores y se dejó intacto salvo las ampliaciones explícitas indicadas.

## Migración 003

`003_session_recovery.sql` agrega:

- `matches.round_deadline_at` y `matches.terminal_reason`;
- `rooms`, con visibilidad, invitación, ciclo de vida, actividad y asociación única a partida;
- `room_members`, con identidad Auth0, `player_id`, propietario y preparación;
- `match_outbox`, con destinatario privado, evento, secuencia, payload, intentos, próximo intento, entrega y abandono controlado tras 10 fallos de transporte.

Las rutas verificadas fueron base vacía (001→002→003), base instalada en 001 (001→002→003) y base instalada en 002 (002→003), preservando partidas y checksums anteriores.

## Recuperación de partidas y salas

Las salas se consultan y actualizan en PostgreSQL. Un servicio nuevo recupera visibilidad, invitación, capacidad, miembros, preparación y `matchId`. La fila de sala se bloquea al unir o cambiar preparación, y `active_match_players` continúa impidiendo dos partidas activas por usuario bajo concurrencia.

`FindLatestMatchAsync` localiza la partida más reciente del subject autenticado. `ResumeMatch` y `JoinMatch` vuelven a comprobar la pertenencia antes de asociar la conexión. El snapshot privado contiene ronda, secuencia, salud, energía propia, bloqueos propios, especial propio, plan pendiente propio, plazo, ganador y causa terminal. No incluye energía, plan, bloqueos ni especial del oponente.

## Reconexión y resincronización

SignalR autentica cada conexión nueva con el flujo Auth0 existente. La conexión se incorpora al grupo de partida después de autorizarla y reemplaza la ruta efímera previa. `OnDisconnectedAsync` elimina rutas obsoletas y conserva una ventana de gracia configurable separada del plazo de ronda. Keepalive y timeout se configuran con `SignalR:KeepAliveSeconds` y `SignalR:ClientTimeoutSeconds`.

El cliente guarda sólo `game.activeMatchId`, que no es secreto; no guarda JWT ni refresh tokens. Una pestaña nueva usa ese ID o `ResumeMatch` con ID nulo para descubrir la partida. Los snapshots son autoritativos. Los eventos con secuencia ya aplicada se descartan; al reconectar, primero se entrega el snapshot y después se drena el outbox, cerrando la ventana entre lectura y suscripción. `hasSubmittedPlan` mantiene bloqueado el reenvío de un plan ya confirmado.

## Transactional outbox

Las notificaciones `round.resolved` y `match.ended` se insertan para cada destinatario en la misma transacción Npgsql que actualiza estado, comandos, planes y eventos. No se mantiene la transacción abierta durante SignalR. El servicio de fondo reintenta con backoff de 1 a 60 segundos, registra un código de error sin secretos y deja de reintentar automáticamente tras 10 fallos de transporte. Una conexión ausente no consume intentos y puede recuperar el snapshot al volver.

La garantía es al menos una vez. Dos publicadores pueden observar una misma fila antes de marcarla, por lo que el cliente usa `matchId` y secuencia para descartar duplicados funcionales. Los payloads privados se envían únicamente a la conexión autorizada del subject destinatario.

## Plazos, desconexión y cola rápida

El plazo autoritativo de cada ronda reside en `round_deadline_at`. Al vencer, el mismo bloqueo `SELECT ... FOR UPDATE` usado por comandos decide una sola vez: gana quien presentó plan o hay empate si ninguno lo hizo. Esta conserva la regla del sketch. El enunciado también menciona “acción por defecto”; no se añadió una acción automática porque contradice la victoria/empate por inactividad especificada con mayor precisión.

La gracia de reconexión predeterminada es 30 segundos y describe presencia/transporte; no extiende ni reemplaza el plazo de cinco minutos. Desconectarse no concede victoria inmediata.

La cola rápida continúa deliberadamente en memoria. Por ello no se anuncia como recuperable: una instancia nueva empieza vacía. Una pestaña que seguía marcada como `queued` se reinscribe al reconectar con el mismo request ID; si el navegador se cerró, el usuario debe inscribirse otra vez. El tiempo máximo sigue configurable, las entradas duplicadas se consolidan, las expiradas se retiran y el semáforo local más el ledger PostgreSQL evitan asignaciones activas dobles.

## Pruebas automatizadas

Comandos ejecutados:

```powershell
dotnet build ProyectoServer.slnx --configuration Release
$env:GAME_POSTGRES_TESTS='1'
dotnet test ProyectoServer.slnx --configuration Release --no-build
npm.cmd run lint
npm.cmd run build
```

Resultado .NET final:

- `Game.Domain.Tests`: 25 aprobadas, 0 fallidas, 0 omitidas.
- `Game.Server.Tests`: 27 aprobadas, 0 fallidas, 0 omitidas.
- `Game.Postgres.Tests`: 83 aprobadas, 0 fallidas, 0 omitidas.
- Total: **135 aprobadas, 0 fallidas, 0 omitidas**.
- Build Release: correcto, 0 errores y 0 advertencias.

Las 12 pruebas agregadas verifican recuperación de sala desde otra instancia, descubrimiento de partida, plan pendiente, resolución entre pools independientes, rechazo no autorizado, resultado terminal, caída de transporte, recuperación y no duplicación del outbox, plazo persistido, carrera plazo/comando, estrategia de cola, limpieza de sala, actualización desde versión 2 y reinicio real del host ASP.NET. En conjunto con las pruebas conservadas se cubren los 18 escenarios solicitados, incluida privacidad, aislamiento entre partidas e idempotencia.

La prueba funcional crea sala y partida mediante REST, conecta dos clientes SignalR con JWT efímeros de prueba, confirma el primer plan, destruye el primer `WebApplicationFactory`, crea un host independiente sobre la misma base, reautentica y reconecta ambos clientes, comprueba el plan pendiente, envía el segundo plan y verifica una sola resolución persistida. Usa PostgreSQL 17.11 real mediante Testcontainers y Long Polling de TestServer; no altera Auth0 real.

Cliente:

- ESLint: correcto, sin errores ni advertencias.
- TypeScript/Vite: correcto.
- Vite produjo una advertencia de tamaño: chunk JavaScript de 531.19 kB (153.73 kB gzip), por encima de 500 kB. Es una advertencia preexistente de empaquetado, no un fallo de 2C.

## Limitaciones y preparación para Azure

- No se probó un navegador real con Auth0 y WebSocket ni un corte físico de red; la prueba funcional usa el middleware JWT real, dos hosts ASP.NET independientes y SignalR Long Polling dentro de TestServer.
- La cola no sobrevive reinicios por diseño y requiere reinscripción.
- Las rutas de conexión son locales a cada réplica. Varias réplicas requieren Azure SignalR Service o un backplane equivalente para dirigir eventos a conexiones alojadas en otro proceso.
- El outbox ya es compartido por PostgreSQL y tolera duplicados, pero varias réplicas deberían reclamar lotes con `FOR UPDATE SKIP LOCKED` para reducir envíos simultáneos.
- Las filas que alcanzan 10 fallos quedan disponibles para observación/operación manual; todavía no existe panel administrativo ni alerta, porque pertenecen a fases posteriores.
- No se crearon recursos Azure, Redis, administración ni historial REST.

La Fase 2C termina aquí. No se inició ninguna fase posterior.
