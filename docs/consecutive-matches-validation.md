# Validación de partidas consecutivas y recuperación

Fecha: 12 de noviembre de 2026.

## Causa raíz comprobada

El fallo combinaba tres referencias obsoletas del cliente y un cursor global:

1. `recoveredMatchId` se inicializaba desde `game.activeMatchId` aunque la partida almacenada ya estuviera completada.
2. «Volver al lobby» ocultaba el snapshot, pero no eliminaba `recoveredMatchId`, `room.matchId` ni la entrada de `localStorage`. El efecto de `JoinMatch` volvía a solicitar explícitamente esa partida.
3. `latestMatchSequence` se compartía entre partidas. Si la primera terminaba en secuencia 6, el snapshot inicial de la segunda, con secuencia 0, era tratado como antiguo y descartado.
4. `FindLatestMatchAsync` priorizaba una partida activa, pero, si no encontraba una, devolvía la partida finalizada más reciente. Esto hacía que una recuperación sin ID volviera a presentar un resultado histórico como sesión actual.
5. Después de confirmar `is_ready`, dos solicitudes concurrentes podían observar 2/2 jugadores listos y crear dos partidas antes de asociar una a la sala. La primera creación ocupaba `active_match_players` y la segunda terminaba en `player_already_in_match`, aunque ambas solicitudes correspondieran a la misma sala.

La captura de Firefox es consistente con este flujo: el cliente enviaba `JoinMatch` con el ID terminado y el servidor respondía correctamente para ese ID. SignalR y Auth0 no causaban el problema.

## Corrección

- `game.activeMatchId` representa únicamente una partida activa. Se elimina cuando llega un snapshot o evento terminal para ese ID.
- El resultado final continúa en `matchSnapshot` para que el usuario pueda verlo; no se borra el resultado persistente.
- «Volver al lobby» limpia las referencias de navegación de la partida y sala anteriores, además del cursor de secuencia.
- El inicio de sala, matchmaking y la respuesta REST de ready seleccionan inmediatamente el Match ID nuevo.
- Los mensajes se aceptan únicamente si corresponden al Match ID esperado. Cada Match ID inicia su propio cursor de secuencia, por lo que secuencia 0 de una partida nueva es válida.
- Respuestas tardías de otra partida y eventos fuera de orden de la misma partida se descartan.
- La recuperación sin Match ID consulta solamente el ledger `active_match_players`; una partida completada sigue en `matches`, `match_participants`, `match_events` e historial.
- Después de descartar una referencia local completada, el cliente vuelve a consultar al servidor. Si existe otra partida activa, esa partida reemplaza automáticamente al resultado histórico.
- La transición de sala lista a partida usa un advisory lock transaccional derivado del ID de sala. Solicitudes ready concurrentes para la misma sala crean una sola partida y devuelven el mismo Match ID; salas distintas conservan concurrencia independiente.

PostgreSQL ya liberaba correctamente el ledger mediante `matches_active_assignments`, un trigger diferido que se ejecuta en la misma transacción del cierre. `player_already_in_match` permanece asociado a la PK de `active_match_players` y sigue impidiendo dos partidas realmente activas por usuario. El bloqueo nuevo evita que una carrera interna de una sola sala llegue incorrectamente a esa restricción.

## Archivos responsables y modificados

- `web-client/src/App.tsx`: selección del Match ID, persistencia local, retorno al lobby, recuperación y filtros de mensajes SignalR.
- `web-client/src/matchSession.ts`: decisión pura de aceptación por partida y secuencia.
- `src/Game.Infrastructure/PostgresMatchSessionStore.cs`: recuperación limitada a partidas activas.
- `src/Game.Server/Rooms/InMemoryRoomService.cs`: serialización PostgreSQL de la creación de partida por sala.
- `tests/Game.Postgres.Tests/HttpSignalRTests.cs`: flujo REST + SignalR de dos partidas consecutivas y recarga de un cliente.
- `tests/Game.Postgres.Tests/Phase2CRecoveryTests.cs`: liberación del ledger, reinicio del servicio, prioridad activa, rechazo simultáneo, finalización idempotente e historial preservado.
- `web-client/src/matchSession.test.ts`: cambio de partida y mensajes tardíos o fuera de orden.

## Base de datos

No se requiere migración 005. Las migraciones 001–004 permanecen sin cambios. La consulta usa el ledger e índices existentes.

## Validación automatizada

- Compilación .NET Release: 0 errores y 0 advertencias.
- Dominio: 25 pruebas aprobadas.
- Servidor: 27 pruebas aprobadas.
- PostgreSQL 17.11: 93 pruebas aprobadas.
- Total .NET: 145 aprobadas, 0 fallidas y 0 omitidas.
- Frontend: build de producción aprobado; ESLint aprobado; 3 pruebas Node aprobadas.
- `git diff --check`: aprobado.

La regresión integral crea la primera sala, inicia y termina la primera partida, crea una segunda sala con los mismos subjects, confirma un Match ID distinto, conecta ambos usuarios, simula la recarga de uno mediante una conexión nueva, recupera la segunda partida activa, la termina y comprueba que ambos historiales contienen las dos partidas. Otras regresiones confirman que solicitudes ready concurrentes de una misma sala reciben un único Match ID, que una tercera partida en otra sala aún devuelve `player_already_in_match` y que repetir la finalización no agrega otro evento terminal.

## Actualización posterior de Azure

La actualización debe agrupar esta corrección con historial, administración y la migración 004 ya preparados:

1. Revisar los cambios locales y construir una única imagen nueva.
2. Publicarla en ACR con etiqueta de commit y registrar el digest inmutable.
3. Actualizar el Migration Job al digest nuevo y ejecutar una sola vez la migración 004.
4. Verificar el checksum y que los datos históricos existentes permanecen.
5. Desplegar una revisión nueva de Container App con una réplica y la configuración actual.
6. Repetir con dos cuentas reales: dos partidas consecutivas, recarga durante la segunda, historial y permisos `admin:game`.
7. Mantener la revisión anterior disponible para rollback de aplicación.

No se ejecutó ninguna operación sobre Azure, PostgreSQL de producción, Auth0 o GitHub durante esta corrección.
