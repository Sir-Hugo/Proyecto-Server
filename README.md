# Proyecto de servidor de juego

## Etapa 1: dominio inicial

El proyecto usa C# sobre .NET 10 LTS. Aunque los requisitos aceptan .NET 8 o posterior, .NET 8 llega al fin de soporte el 10 de noviembre de 2026, por lo que iniciamos con .NET 10. El modelo del juego vive en `src/Game.Domain`, separado de cualquier transporte, almacenamiento o framework de servidor. Nullable y warnings como errores están activados en `Directory.Build.props`.

Reglas del juego ya incorporadas: dos jugadores, salud máxima de 50, 1 punto de energía al inicio, acciones normales con costo de 1, ataque base de 5, crítico triple, recuperación de 2 puntos al comienzo de cada ronda posterior y expiración tras 5 minutos sin actividad. La partida dura como máximo 10 rondas: si nadie queda sin salud antes, gana quien tenga más salud al resolver la décima ronda; si tienen la misma salud, hay empate. Cada acción de reservar convierte un punto de energía actual en un punto guardado para la siguiente ronda. No hay tope de reserva; la duración máxima mantiene acotadas las acciones a 19 por jugador por ronda. Cada jugador puede usar hasta 5 bloqueos por partida. Un bloqueo cancela por completo un ataque, incluido un crítico o el ataque especial.

El ataque especial se desbloquea desde la ronda 5, cuesta 3 puntos de energía, hace 20 de daño fijo y solo se puede usar una vez por jugador por partida. No puede ser crítico. Si un jugador bloquea en una ronda donde el rival usa tanto el ataque especial como ataques normales, el bloqueo cancela primero el ataque especial.

Los planes de ambos jugadores se consideran simultáneos. Cada jugador debe asignar toda su energía disponible a ataques, bloqueos, reservas o al ataque especial. Los bloqueos cancelan ataques antes de resolver el daño restante. La probabilidad de crítico acordada es 10% para los ataques normales. Si ambos jugadores llegan a cero de salud en la misma ronda, el resultado es empate.

Si un jugador no envía un plan válido durante 5 minutos y el otro sí lo envió, gana el jugador activo. Si ninguno envía un plan dentro del plazo, el resultado por inactividad es empate. El servidor deberá llevar el control de actividad por jugador para aplicar esta regla.

## Etapa 2: Servidor

El host de ASP.NET Core está en `src/Game.Server` y referencia el dominio. Expone `GET /health`, publica la documentación OpenAPI 3.1 en `/docs` (con el JSON en `/openapi/v1.json`) y genera respuestas ProblemDetails para errores HTTP y excepciones no controladas. El contrato `round.plan.submit` (versión 1 en un campo independiente) y su validador estructural están en `Contracts/V1`; el protocolo en tiempo real está documentado en `docs/asyncapi.yaml`. El Hub autenticado de SignalR está en `/hubs/game`; recibe comandos de emparejamiento y planes de ronda, valida que la identidad pertenezca a la partida y envía snapshots privados. Las rondas se resuelven en el servidor cuando ambos jugadores envían planes válidos. Se validan las reglas, la secuencia esperada y la idempotencia por `requestId`: un reintento idéntico recibe el acuse y resultado ya calculados, mientras que reutilizar el identificador con otro contenido se rechaza. Ambos jugadores reciben su resultado individual, sin revelar el plan del rival. También están implementados el cierre por inactividad y el abandono voluntario, que otorga la victoria al rival y comunica el motivo por SignalR. Las conexiones requieren un JWT válido. Para clientes web que usan WebSocket, el servidor acepta `access_token` en la cadena de consulta únicamente para `/hubs/game`. El límite actual es de 8 KiB por mensaje entrante de SignalR. La configuración de autenticación se mantiene en `src/Game.Server/auth` y el proveedor OIDC elegido es Auth0. La autoridad y audiencia de la API se cargan localmente desde variables de entorno, sin guardar valores específicos del tenant en el repositorio.

El estado de partidas, planes pendientes, secuencia e idempotencia aún se conserva en memoria: se pierde al reiniciar el servidor y no se comparte entre varias instancias. En `docs/persistence-design.md` se encuentra una propuesta para persistir partidas y eventos en PostgreSQL mediante transacciones atómicas; esa persistencia todavía no está implementada. Redis queda como posible componente posterior para coordinación y estado caliente al escalar el servidor.

La gestión inicial de salas REST está en `src/Game.Server/Rooms`: permite crear salas públicas o privadas, listar las públicas disponibles, consultar, unirse, salir y cambiar el estado de preparación. El límite está fijado en dos jugadores por sala. Por ahora el estado se guarda en memoria, así que se pierde al reiniciar el servidor y no se comparte entre varias instancias. La actividad de miembros renueva la expiración de la sala; tras 5 minutos sin actividad, un servicio de fondo la elimina, con una revisión cada 30 segundos. Cuando ambos miembros están listos, se crea una partida inicial en memoria y la respuesta incluye su `matchId`. La cola rápida empareja jugadores en orden de llegada y vence tras 4 minutos; el cliente recibe eventos de espera, cancelación, vencimiento o partida encontrada. Los tiempos se configuran con `RoomLifecycle:InactivityLimitMinutes`, `RoomLifecycle:CleanupIntervalSeconds` y `Matchmaking:MaximumWaitMinutes`.

Para desarrollo local, `src/Game.Server/appsettings.Development.json` permite el origen `http://localhost:5173` para las solicitudes del cliente web y las conexiones WebSocket. En otros entornos, configura `Cors:AllowedOrigins` con los orígenes exactos de confianza; el servidor no inicia si no se configura ninguno. La conexión del cliente usa un access token de Auth0 y no debe registrarse ni compartirse.

### Configuración de Auth0 para el servidor

El servidor usa la validación JWT estándar de ASP.NET Core; no necesita un SDK específico de Auth0. Al configurar Auth0, se registrará una API con el algoritmo de firma RS256 y se usarán los siguientes valores:

- `Authentication:Authority`: el dominio del tenant Auth0, con esquema HTTPS y `/` al final, por ejemplo `https://mi-tenant.us.auth0.com/`.
- `Authentication:Audience`: el identificador de la API registrado en Auth0, por ejemplo `https://game.example.com`.

Los valores reales para Authority y Audience se encuentran en la carpeta `env`

El cliente deberá solicitar a Auth0 un *access token* para el identificador de esta API. `Authority` y `Audience` no son contraseñas ni claves privadas, pero dependen del tenant y se mantienen fuera del repositorio junto con la configuración local. La carpeta `env` está excluida en `.gitignore`.
