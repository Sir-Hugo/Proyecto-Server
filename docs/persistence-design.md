# Diseño propuesto de persistencia de partidas

## Objetivo y alcance

PostgreSQL será la fuente de verdad duradera para el estado de las partidas, sus eventos confirmados, los planes pendientes y las respuestas idempotentes. La primera implementación seguirá siendo de una sola instancia y no requiere Redis. Redis podrá añadirse después para coordinación y estado caliente al escalar a varias instancias.

Este diseño cubre el ciclo de vida de una partida ya iniciada. La persistencia de salas, cola de emparejamiento, perfiles y administración queda fuera de este paso y deberá diseñarse aparte.

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
| `first_used_blocks`, `second_used_blocks` | `smallint` | Entre 0 y 5. |
| `first_used_special`, `second_used_special` | `boolean` | Estado de uso del ataque especial. |
| `winner_player_id` | `uuid` nullable | Nulo para partidas activas o empate; si tiene valor, debe identificar a uno de los dos participantes. |
| `last_activity_at` | `timestamptz` | Base para el cierre por inactividad. |
| `created_at`, `completed_at` | `timestamptz` | Auditoría del ciclo de vida. |

Los nicknames pueden guardarse en `match_participants` porque pertenecen a esa partida y no al perfil global.

### `match_participants`

Una fila por participante de la partida.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id` | `uuid` | Clave foránea a `matches`. |
| `player_id` | `uuid` | Identificador interno estable del participante. |
| `auth_subject` | `text` | `sub` de Auth0; único dentro de una partida. |
| `nickname` | `varchar(10)` | Entre 1 y 10 caracteres; validar letras Unicode, números, `_` y `-` en aplicación. |
| `slot` | `smallint` | 1 o 2. |

Claves: primaria (`match_id`, `player_id`), única (`match_id`, `auth_subject`), única (`match_id`, `slot`). Una FK compuesta desde las columnas de jugador en `matches` puede asegurar que sus dos IDs pertenecen a esa partida.

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

### `match_commands`

Libro de idempotencia para comandos recibidos. Permite responder a un reintento incluso después de reiniciar el proceso.

| Campo | Tipo sugerido | Regla |
|---|---|---|
| `match_id`, `player_id`, `request_id` | `uuid` | Clave primaria compuesta: una solicitud por jugador y partida. |
| `request_fingerprint` | `bytea` | Huella determinista de secuencia esperada y acciones normalizadas. |
| `accepted_sequence` | `bigint` | Secuencia a la que se presentó el comando. |
| `acknowledgement` | `jsonb` | Acuse `round.plan.accepted` previamente generado. |
| `outcome` | `jsonb` nullable | Resultado privado final una vez resuelta la ronda; nulo mientras espera al otro jugador. |
| `created_at` | `timestamptz` | Hora de recepción. |

La clave primaria impide duplicar el mismo `request_id`. Si se repite con la misma huella, se devuelve el acuse y, si ya existe, el resultado almacenado. Si la huella no coincide, se rechaza como reutilización de clave. Retener filas mientras la partida sea consultable; cualquier política de borrado debe respetar el período de reintento acordado.

## Restricciones de integridad

Además de claves primarias y foráneas, definir `CHECK` para:

- `status IN ('active', 'completed')`.
- `round_number BETWEEN 1 AND 10` y `sequence_number >= 0`.
- Salud entre 0 y 50; bloqueos usados entre 0 y 5; energía no negativa.
- Los dos participantes son distintos y el ganador, si existe, es uno de ellos.
- Una partida activa no tiene `completed_at`; una completada sí.
- `slot IN (1, 2)` y nickname no vacío, con límite de 10 caracteres. La clase de caracteres se valida también en el dominio porque una expresión SQL para letras Unicode puede variar por configuración.

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
4. Insertar `match_events` para `round.resolved` con la nueva secuencia y, si termina, `match.completed` con la secuencia que corresponda según la convención elegida. **Recomendación:** una transición atómica genera un solo evento `round.resolved` que incluye el estado terminal y su causa; evitar dos eventos con la misma secuencia.
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
3. Convención exacta de secuencia: el diseño recomienda incrementarla una vez por ronda resuelta y usar esa misma secuencia en el evento correspondiente.
4. Si los planes aceptados deben sobrevivir a una expiración por inactividad para evaluar abandono; la regla actual requiere saber cuál jugador envió un plan, pero no necesariamente conservar sus acciones una vez terminada la partida.
5. Mecanismo de migraciones para el proyecto .NET y forma local de levantar PostgreSQL.

