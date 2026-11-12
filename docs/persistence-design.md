# Diseño de persistencia de partidas

## Objetivo y alcance

PostgreSQL será la fuente de verdad duradera para el estado de las partidas, sus eventos confirmados, los planes pendientes y las respuestas idempotentes. La primera implementación seguirá siendo de una sola instancia y no requiere Redis. Redis podrá añadirse después para coordinación y estado caliente al escalar a varias instancias.

Este diseño cubre el ciclo de vida de una partida ya iniciada. La persistencia de salas, cola de emparejamiento, perfiles y administración queda fuera de este paso y deberá diseñarse aparte.

**Estado Fase 2B:** el ciclo real de partidas usa PostgreSQL a través de `PostgresMatchSessionStore`. Salas, cola y conexiones permanecen temporales. Las migraciones 001 y 002 se ejecutan explícitamente; no se reescribió 001 ni existe fallback de partidas en memoria.

## Modelo relacional

Los nombres siguientes son conceptuales; se recomienda usar `snake_case` en PostgreSQL.

### `matches`

Una fila por partida. Conserva el último estado confirmado para poder servir snapshots sin reproducir todo el historial.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `id` | `uuid` | Clave primaria; identificador público de partida. |
| `status` | `text` | `active` o `completed`. |
| `round_number` | `smallint` | Entre 1 y 10. |
| `sequence_number` | `bigint` | No negativo; aumenta una vez por transición confirmada. |
| `first_player_id`, `second_player_id` | `uuid` | Identificadores internos de participantes, distintos entre sí. |
| `first_health`, `second_health` | `smallint` | Entre 0 y 50. |
| `first_energy`, `second_energy` | `integer` | No negativo. No imponer un máximo de reserva que las reglas no contemplan. |
| `first_reserved_energy`, `second_reserved_energy` | `integer` | No negativo, predeterminado 0; conserva todo `PlayerState`, aunque el resolver actual consume la reserva al avanzar. |
| `first_used_blocks`, `second_used_blocks` | `smallint` | Entre 0 y 5. |
| `first_used_special`, `second_used_special` | `boolean` | Estado de uso del ataque especial. |
| `winner_player_id` | `uuid` nullable | Nulo para partidas activas o empate; si tiene valor, debe identificar a uno de los dos participantes. |
| `last_activity_at` | `timestamptz` | Base para el cierre por inactividad. |
| `created_at`, `completed_at` | `timestamptz` | Auditoría del ciclo de vida. |
| `updated_at` | `timestamptz` | Actualizado automáticamente por trigger. |
| `started_at` | `timestamptz` nullable | Desde 002, instante en que ambos nicknames están elegidos; puede ser nulo antes de ese punto o en registros heredados sin fecha conocida. |

Los nicknames pueden guardarse en `match_participants` porque pertenecen a esa partida y no al perfil global.

### `match_participants`

Una fila por participante de la partida.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id` | `uuid` | Clave foránea a `matches`. |
| `player_id` | `uuid` | Identificador interno estable del participante. |
| `auth_subject` | `text` | `sub` de Auth0; único dentro de una partida. |
| `nickname` | `varchar(10)` nullable | Nulo antes de elegirlo; si existe, entre 1 y 10 caracteres. Letras Unicode, números, `_` y `-` se validan en aplicación, como en `SetNicknameAsync`. |
| `slot` | `smallint` | 1 o 2. |

Claves: primaria (`match_id`, `player_id`), única (`match_id`, `auth_subject`), única (`match_id`, `slot`). La implementación evita FKs circulares: los participantes referencian `matches` y constraint triggers diferidos comprueban al commit que existen exactamente dos y que cada ID corresponde a su slot declarado. Una transacción normal inserta primero la partida y luego sus dos participantes. `auth_subject` permanece como texto separado del UUID interno. Los participantes tienen `created_at` y `updated_at` con trigger de actualización.

### `match_events`

Registro append-only de transiciones confirmadas. El contenido se conserva como JSONB versionado para poder añadir campos de evento sin migrar una columna por cada detalle.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id` | `uuid` | Parte de la clave primaria y FK a `matches`. |
| `sequence_number` | `bigint` | Número de secuencia producido por la transición. |
| `event_type` | `text` | Por ejemplo `match.started`, `round.resolved`, `match.completed`. |
| `event_version` | `smallint` | Versión del esquema del evento, inicialmente 1. |
| `occurred_at` | `timestamptz` | Instante UTC de confirmación. |
| `payload` | `jsonb` | Datos suficientes para auditoría y eventual reproducción. |

Clave primaria (`match_id`, `sequence_number`). No actualizar ni borrar eventos durante la operación normal. El evento de ronda no debe incluir el plan secreto del rival en una respuesta destinada al cliente; los eventos internos sí pueden guardar los planes necesarios para auditoría, con acceso restringido.

La infraestructura protege UPDATE, DELETE y TRUNCATE con triggers append-only, añade `created_at` y exige payload de tipo objeto JSONB. El formato interno de evento v1 de 2B contiene el estado confirmado completo (incluido ganador, energía y contadores), causa y resultados/rolls suficientes para verificación sin volver a sortear críticos. La prueba de almacenamiento utiliza un `match.started` con el estado inicial completo. El emisor real de 2B guarda `before`, `after`, `firstPlan`, `secondPlan`, `criticalRolls`, `resolution` y `reason`; una prueba reproduce una ronda crítica con el dominio y las tiradas guardadas. No existe interfaz de replay ni se confunden estos payloads internos con vistas privadas SignalR. Un propietario de base puede deshabilitar triggers: el rol de ejecución de producción debe carecer de esos privilegios.

### `pending_round_plans`

Planes aceptados para la ronda actual que todavía esperan el plan contrario. No deben estar disponibles mediante consultas de historial ni snapshots del rival.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id` | `uuid` | FK a `matches`. |
| `round_number` | `smallint` | Debe coincidir con la ronda activa al aceptar el plan. |
| `player_id` | `uuid` | FK compuesta a participante de esa partida. |
| `request_id` | `uuid` | Identificador de idempotencia de la solicitud. |
| `actions` | `jsonb` | Lista validada de acciones; solo el servidor la lee. |
| `submitted_at` | `timestamptz` | Hora UTC de aceptación. |

Clave primaria (`match_id`, `round_number`, `player_id`): un solo plan por jugador y ronda. Al resolver la ronda, ambos registros se eliminan dentro de la misma transacción.

La FK compuesta referencia el comando de ese jugador y requestId; una restricción UNIQUE impide usar el mismo comando en otra ronda. Un trigger bloquea la fila de partida y comprueba ronda activa y secuencia aceptada al almacenar el plan. El CHECK exige un array JSONB de 1 a 19 nombres de acción válidos; costos, bloqueos y disponibilidad del especial siguen validados por el dominio en el flujo de juego, no por una segunda implementación SQL.

### `match_commands`

Libro de idempotencia para comandos recibidos. Permite responder a un reintento incluso después de reiniciar el proceso.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id`, `player_id`, `request_id` | `uuid` | Clave primaria compuesta: una solicitud por jugador y partida. |
| `request_fingerprint` | `bytea` | Huella determinista de secuencia esperada y acciones normalizadas. |
| `accepted_sequence` | `bigint` | Secuencia a la que se presentó el comando. |
| `acknowledgement` | `jsonb` | Acuse `round.plan.accepted` previamente generado. |
| `outcome` | `jsonb` nullable | Resultado privado final una vez resuelta la ronda; nulo mientras espera al otro jugador. |
| `outcome_event_type` | `text` nullable | Nombre de evento para reproducir `CachedOutcome`, necesario porque el código actual guarda tanto `round.resolved` como `match.ended`. Nulo exactamente cuando outcome es nulo. |
| `created_at` | `timestamptz` | Hora de recepción. |
| `updated_at` | `timestamptz` | Trigger de auditoría al completar el resultado. |

La clave primaria impide duplicar el mismo `request_id`. Si se repite con la misma huella, se devuelve el acuse y, si ya existe, el resultado almacenado. Si la huella no coincide, se rechaza como reutilización de clave. Retener filas mientras la partida sea consultable; cualquier política de borrado debe respetar el período de reintento acordado.

La infraestructura usa SHA-256 (32 bytes) de una representación JSON determinista con matchId, secuencia esperada y acciones normalizadas conservando su orden. `acknowledgement` y `outcome` son objetos JSONB; al releer, PostgreSQL puede cambiar espacios/orden de claves, pero conserva el contenido. `PostgresMatchSessionStore` carga y compara la huella antes de comprobar estado/secuencia y reproduce el acuse/resultado persistido. `SavePendingPlanAsync` se conserva solo como operación de infraestructura de 2A, no se encadena para ejecutar rondas reales.

## Restricciones de integridad

Además de claves primarias y foráneas, definir `CHECK` para:

- `status IN ('active', 'completed')`.
- `round_number BETWEEN 1 AND 10` y `sequence_number >= 0`.
- Salud entre 0 y 50; bloqueos usados entre 0 y 5; energía no negativa.
- Los dos participantes son distintos y el ganador, si existe, es uno de ellos.
- Una partida activa no tiene `completed_at`; una completada sí.
- `slot IN (1, 2)` y nickname no vacío cuando existe, con límite de 10 caracteres. La clase de caracteres se valida en el servidor (`SetNicknameAsync`), no en `Game.Domain`, porque una expresión SQL para letras Unicode puede variar por configuración.

La consistencia entre `matches` y los eventos no se puede expresar completamente con `CHECK`; la garantiza la transacción de escritura. Las consultas y escrituras usan parámetros, y las tablas de eventos, planes y comandos no se exponen directamente por REST.

## Transacciones

### Aceptar un plan

En una transacción `READ COMMITTED`:

1. Buscar la partida por ID y bloquear su fila con `SELECT ... FOR UPDATE`. Este bloqueo serializa los comandos de una partida en la primera versión de una instancia.
2. Confirmar que el participante pertenece a la partida.
3. Consultar `match_commands` por `(match_id, player_id, request_id)` **antes** de validar la secuencia actual. Si existe, comparar la huella y devolver el acuse/resultado ya persistido, o rechazar si la huella difiere.
4. Confirmar que la partida sigue activa, que no hay un plan del jugador para esa ronda y que la secuencia esperada coincide.
5. Validar reglas del plan en el dominio. Insertar `match_commands` con el acuse y `pending_round_plans` con las acciones. Confirmar la transacción.

Si todavía falta el plan contrario, no se cambia la secuencia de la partida ni se emite un evento `round.resolved`.

### Resolver una ronda

Cuando ya existen dos planes pendientes, dentro de una transacción:

1. Mantener el bloqueo de la fila `matches` y cargar ambos planes de la misma ronda.
2. Resolver la ronda con el dominio puro, usando un instante UTC capturado una sola vez para la operación.
3. Actualizar en `matches` salud, energía, ronda, estado, ganador, `sequence_number` y `last_activity_at`.
4. Insertar un único `round.resolved` con la nueva secuencia, también cuando la ronda termina la partida: contiene el estado terminal y ganador. No emitir otro evento con esa misma secuencia. Abandono/inactividad generan `match.ended` con su propio incremento.
5. Guardar en cada `match_commands.outcome` el resultado privado correspondiente a su jugador.
6. Eliminar los dos planes pendientes y confirmar.
7. Solo después del commit, publicar por SignalR los acuses y resultados. Si falla el envío, el estado ya confirmado sigue disponible y el cliente puede recuperarlo mediante snapshot/reintento.

Un error antes del commit revierte estado, evento, resultado idempotente y eliminación de planes como una sola unidad.

### Finalizar por inactividad

El servicio de temporizador inicia una transacción, bloquea la partida, vuelve a comprobar `status` y `last_activity_at` contra el límite, decide empate o ganador según los planes válidos pendientes y actualiza el estado junto con el evento terminal. La comprobación dentro del bloqueo evita cerrar por inactividad una partida cuya ronda acaba de confirmarse. Al confirmar, elimina planes pendientes que ya no sean necesarios y publica el resultado.

## Recuperación y lectura

- El snapshot actual se lee de `matches` y `match_participants`; no es necesario reproducir todos los eventos al iniciar el servidor.
- `match_events` permite auditoría y será la base para reconstrucción/verificación futura. La reconstrucción de estado por replay queda como capacidad posterior, con pruebas que comparen el replay con el snapshot.
- Al reconectar, el servidor consulta el estado persistido y envía un snapshot privado con secuencia actual y resultado disponible para los comandos del jugador.
- Los resultados y planes privados se filtran siempre por `player_id`; nunca se devuelve el payload interno completo de eventos al rival.

## Índices iniciales

- `match_events(match_id, sequence_number)` ya queda cubierto por su clave primaria.
- `match_participants(auth_subject, match_id)` para localizar partidas del usuario.
- `matches(status, last_activity_at)` para el proceso de expiración.
- `match_commands(match_id, player_id, request_id)` y `pending_round_plans(match_id, round_number, player_id)` quedan cubiertos por claves primarias.

## Decisiones por cerrar antes de implementar

1. Duración de retención de partidas, eventos e idempotencia.
2. Si el nickname y el historial se pueden consultar indefinidamente por los participantes o solo durante un período definido.
3. Resuelto en 2B: creación en secuencia 0, un incremento por ronda resuelta o cierre por abandono/inactividad. Aceptaciones de plan quedan auditadas en comandos/planes sin incrementar secuencia; nicknames en participantes y started_at, sin emitir transiciones numeradas adicionales.
4. Resuelto en 2B: tras cierre, se eliminan planes pendientes dentro de la transacción y se guarda el resultado terminal privado en todos sus comandos, incluso sin conexión activa. La retención de comandos/eventos sigue pendiente.
5. Resuelto en 2A: Npgsql y migraciones SQL embebidas, Docker Compose con PostgreSQL 17.11.

## Acceso y migraciones implementados en Fase 2A

Se eligió una sola estrategia: SQL explícito con Npgsql 10, sin ORM ni repositorios genéricos. Permite expresar JSONB, índices y triggers PostgreSQL directamente y evita introducir otro modelo de dominio. Un `NpgsqlDataSource` singleton administra el pool; cada operación usa conexiones/comandos asíncronos desechables, parámetros y CancellationToken. El servidor requiere `ConnectionStrings:GameDatabase` y registra el store persistente, sin ejecutar migraciones al arrancar ni cambiar sus rutas REST/SignalR.

`PostgresMatchStore` ofrece creación transaccional de partida/participantes, lectura consistente mediante un único SELECT, inserción de evento y almacenamiento/lectura de un plan privado con su comando. No hay endpoints públicos para estas operaciones; el servicio de juego deberá autorizar el subject antes de usar IDs privados. El flujo real usa `PostgresMatchSessionStore`, que comparte sus helpers de lectura/inserción y confirma la ronda completa en una transacción; no encadena esas operaciones de bajo nivel como escrituras independientes.

`tools/Game.Database` expone `migrate` y `status`, sin requerir Auth0. `schema_migrations` conserva versión/nombre/checksum SHA-256/fecha. `migrate` usa una transacción y `pg_advisory_xact_lock` para serializar ejecuciones, valida historia/checksums, aplica solo versiones pendientes y confirma todo junto. Un error revierte también el DDL; una historia desconocida o modificada se rechaza. `status` no modifica el esquema. Los scripts publicados son inmutables y los cambios futuros se agregan como otra migración numerada, previa revisión. No se ejecutan en el arranque del servidor ni existe downgrade automático.

Los logs propios registran operación y SQLSTATE, omitiendo textos de excepción del proveedor, cadenas de conexión y payloads. El data source desactiva detalles de errores y no habilita logging de parámetros. Se fija el esquema `public`. Los errores de persistencia se presentan como `PersistenceException` sin incluir la excepción interna; la CLI termina con código 1. Una operación cancelada conserva su señal de cancelación.

Las pruebas reales usan Testcontainers.PostgreSql 4.16 y la misma imagen de Compose. Las decisiones de pool y API siguen la [documentación de Npgsql](https://www.npgsql.org/doc/basic-usage.html), y el contenedor sigue el [módulo PostgreSQL de Testcontainers](https://dotnet.testcontainers.org/modules/postgres/). La versión de PostgreSQL se fijó según sus [notas de la rama 17](https://www.postgresql.org/docs/17/release.html).

## Decisiones de operación y Azure

Compose publica solo localhost, tiene health check y volumen persistente; contraseña y puerto se configuran externamente. Su usuario inicial tiene privilegios de desarrollo. No se creó un usuario/contraseña de producción ni recursos externos. Antes de Azure: separar rol propietario/migrador del rol de ejecución (sin DDL, TRUNCATE ni modificación de eventos), usar conexión TLS con verificación de certificado, configurar secretos externamente y dimensionar el pool por instancia. El esquema no requiere extensiones, superusuario ni UUID generados en base de datos.

La retención sigue pendiente: no se programaron purgas. Eventos append-only impiden borrar partidas referenciadas sin un procedimiento de mantenimiento explícito. Las migraciones de producción se ejecutarán como paso controlado previo al despliegue. Persistencia de salas/cola y coordinación entre réplicas siguen fuera de alcance; no hay Redis.


## Integración transaccional implementada en 2B

`IMatchSessionStore` expresa la única frontera de persistencia usada por el servicio. Su implementación de producción es `PostgresMatchSessionStore`; el doble en memoria vive exclusivamente en el proyecto de pruebas unitarias. Una sesión materializada es una copia local al alcance de la transacción y nunca una caché que se confirme antes que PostgreSQL.

`MutateAsync` abre READ COMMITTED, toma la fila `matches FOR UPDATE`, carga estado, participantes, comandos y planes, ejecuta únicamente trabajo síncrono de dominio y persiste los cambios antes del commit. Una lectura de snapshot usa REPEATABLE READ para no mezclar estado de una ronda con comandos/planes de otra. Distintas partidas tienen filas y sujetos diferentes y avanzan de manera independiente. No hay I/O SignalR dentro del callback ni de la transacción.

La resolución valida el primer plan con `RoundResolver.ValidatePlan` y ambos con el resolver al completar la pareja; se expuso el validador puro existente para eliminar la duplicación de reglas del servidor. El algoritmo y probabilidades no cambiaron. Un decorador de ICriticalHitSource registra las tiradas reales. El reloj se captura con precisión de microsegundos para que el estado del evento y timestamptz coincidan al reproducir Windows/Linux.

Los eventos confirmados empiezan con `match.started` en 0 (creación y estado inicial); `started_at` registra separadamente cuándo se elige el segundo nickname. Cada resolución o cierre incrementa la secuencia exactamente una vez. No se emite un evento adicional por cada aceptación porque rompería la secuencia esperada por el protocolo simultáneo actual; esa aceptación ya tiene comando, huella, secuencia y fecha persistentes. Los planes completos solo aparecen en registros internos del servidor y nunca en sus notificaciones.

La migración **002_live_matches.sql** añade `started_at`, la tabla auxiliar `active_match_players` y sus triggers diferidos. Su PK por auth_subject impide asignaciones activas duplicadas entre salas o matchmaking, bajo concurrencia. Se rellena con las asignaciones heredadas; un conflicto rechaza toda la migración sin borrar datos. Los triggers sincronizan el ledger al confirmar y lo liberan al cerrar una partida. Otro trigger impide cambiar campos de una partida terminada, salvo su timestamp técnico updated_at. La migración 001 y su checksum permanecen intactos.

Las salas usan un semáforo por sala durante creación, sin mantener monitor lock durante I/O. Solo publican el matchId y la preparación final al confirmar la base. La cola mantiene la pareja hasta que su creación se confirma, usando su semáforo solo para coordinar el lobby. Ni el estado inicial cacheado en salas ni una conexión se usan para autorizar/leer el progreso de partida: `GameHub.JoinMatch` consulta PostgreSQL por ID/subject. GET y salida de salas consultan el estado terminal persistido.

## Publicación y límites de recuperación en 2B

Las notificaciones se construyen durante la operación pero se envían solo después del commit; sus vistas privadas y nombres no cambian. Los resultados se almacenan para ambos jugadores incluso si están desconectados. Un fallo de transporte se registra sin texto sensible y no revierte el estado; otro envío con requestId idéntico recupera la respuesta de PostgreSQL. `JoinMatch` por un ID conocido vuelve a leer el snapshot privado.

No se implementó outbox ni difusión coordinada entre procesos. Las notificaciones de dos commits concurrentes pueden llegar fuera de orden y una conexión perdida puede no recibir el último evento. Salas/cola, asociación subject-conexión y descubrimiento de partidas al iniciar el cliente siguen siendo memoria/problemas de 2C. El snapshot no añade indicador de plan pendiente ni motivo terminal; el cliente existente conserva esa limitación. Un fallo de conexión durante commit puede dejar resultado ambiguo para el llamador: reintentar un plan con la misma clave permite consultar si se confirmó; la recuperación de creación por origen/usuario necesita 2C.

## Recuperación de sesiones y outbox en 2C

La migración 003 conserva las migraciones anteriores y agrega `rooms`, `room_members` y `match_outbox`, además de `round_deadline_at` y `terminal_reason` en `matches`. Las salas se leen directamente desde PostgreSQL. El ledger de 002 sigue siendo la barrera final contra dos partidas activas para el mismo `auth_subject`.

Las notificaciones privadas de resolución y cierre se agregan a `MatchSession.NewNotifications`; `PostgresMatchSessionStore` inserta esas filas antes del mismo commit que actualiza el estado. La publicación ocurre después del commit. Cada fila registra intentos, próximo intento, entrega y un código de error sin datos privados. Dos publicadores pueden producir un duplicado durante una carrera, por lo que la garantía es al menos una vez y la secuencia del evento permite descartarlo.

`round_deadline_at` es el reloj autoritativo. El expirador local consulta vencimientos en la base y compite con comandos bajo `SELECT ... FOR UPDATE`. Las conexiones no se persisten: `ResumeMatch` vuelve a validar el JWT y la participación, reemplaza la ruta efímera y obtiene un snapshot privado. Esta forma evita depender del proceso previo y prepara el estado para réplicas; Azure aún necesitará un backplane de SignalR o Azure SignalR Service y coordinación de reclamación del outbox para reducir entregas duplicadas.

Errores de dominio/protocolo conservan strings HubException (`stale_sequence`, `idempotency_key_reused`, etc.). Fallos de persistencia se traducen a `database_unavailable` y la restricción de usuario activo a `player_already_in_match`; en salas son Problem Details 503/409. No se publican como exitosas operaciones cuya transacción fue rechazada. El temporizador registra fallos de base y vuelve a intentarlo en la siguiente revisión sin detener el host.
