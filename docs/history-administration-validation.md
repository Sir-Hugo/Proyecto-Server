# Validación de historial y administración

Fecha: 11 de noviembre de 2026.

## Alcance auditado

`Proyecto3.pdf` no está disponible en el repositorio ni en sus directorios padres accesibles. La auditoría se realizó contra README, ROADMAP, OpenAPI y la implementación actual. Los requisitos pendientes identificados eran el historial paginado y privado, el detalle y replay autorizado, y las operaciones administrativas protegidas con auditoría persistente.

No se cambiaron las reglas del juego, la interfaz React ni el mecanismo JWT existente. Tampoco se modificaron recursos de Azure, Auth0 o GitHub, y no se construyó ni publicó una imagen.

## Funcionalidad implementada

- `GET /api/history?page=1&pageSize=20`: partidas del `sub` autenticado, con resultado relativo al usuario y paginación limitada a 100 elementos.
- `GET /api/history/{matchId}`: detalle solo para participantes. Un usuario ajeno recibe 404 para evitar confirmar la existencia de la partida.
- `GET /api/history/{matchId}/events`: replay cronológico proyectado a la perspectiva del participante. No expone el payload JSONB original, subjects, planes del rival ni tiradas críticas.
- `GET /api/admin/matches/active`: partidas activas.
- `GET /api/admin/connections`: conexiones SignalR de la instancia actual.
- `POST /api/admin/matches/{matchId}/terminate`: cierre transaccional, evento `match.ended`, mensajes privados en outbox y auditoría.
- `PUT` y `DELETE /api/admin/users/{subject}/suspension`: suspensión y rehabilitación con motivo obligatorio. La suspensión se aplica a HTTP y SignalR, incluso a invocaciones de una conexión ya abierta.
- `GET /api/admin/audit`: auditoría administrativa paginada.

Todos los endpoints administrativos requieren la policy `GameAdministrator`, que exige la claim `permissions` con el valor exacto `admin:game`. Las pruebas demuestran que autenticarse o presentar una claim `role=admin` inventada no concede acceso.

## Migración 004

`004_history_administration.sql` agrega:

- `user_suspensions`, con motivo, administrador, fecha y expiración opcional.
- `admin_audit_log`, con actor, acción, objetivo, motivo y fecha.
- índices para expiración y consultas de auditoría.
- triggers que rechazan UPDATE, DELETE y TRUNCATE del registro de auditoría.

Las migraciones 001–003 no fueron modificadas. La herramienta conserva la aplicación explícita, transaccional e idempotente de migraciones; el servidor no migra al arrancar.
El SHA-256 normalizado que registrará `schema_migrations` para 004 es `FB743F6E54D1A7A1FC24DA9DC2AD2E19C8DB8D4379563FF0D62078A326BB2F56`.

## Autorización Auth0 pendiente

Auth0 debe emitir el permiso real de la API en el access token:

1. Crear el permiso `admin:game` en la API del juego.
2. Habilitar RBAC y **Add Permissions in the Access Token** para esa API.
3. Asignar el permiso solamente al rol o usuarios administradores autorizados.
4. Obtener un access token real y comprobar que contiene `permissions: ["admin:game"]`, con issuer y audience de producción correctos.
5. Verificar 200 para el administrador y 403 para un usuario ordinario antes de considerar validada la administración en producción.

No se agregaron subjects, tokens ni roles administrativos simulados a la configuración de producción. La prueba local firma tokens efímeros únicamente para ejercitar el middleware JWT y la policy.

## Pruebas

La compilación Release terminó con 0 errores y 0 advertencias. La suite completa se ejecutó con PostgreSQL 17.11 real mediante Testcontainers:

| Proyecto | Aprobadas | Fallidas | Omitidas |
| --- | ---: | ---: | ---: |
| Game.Domain.Tests | 25 | 0 | 0 |
| Game.Server.Tests | 27 | 0 | 0 |
| Game.Postgres.Tests | 90 | 0 | 0 |
| **Total** | **142** | **0** | **0** |

Las pruebas nuevas cubren paginación, privacidad 404, sanitización del replay, OpenAPI, 401/403, permiso Auth0, rechazo de un rol falso, terminación transaccional, outbox, suspensión HTTP/SignalR, rehabilitación y auditoría append-only. `git diff --check` no reportó errores.

## Actualización posterior de Azure

El servidor Azure actual sigue ejecutando la imagen anterior y el esquema 003. Para publicar este cambio en una operación posterior y autorizada:

1. Revisar y versionar los cambios locales.
2. Construir y publicar una imagen nueva por digest inmutable.
3. Actualizar temporalmente el Migration Job a ese digest y ejecutar `Game.Database migrate` una sola vez para aplicar 004.
4. Verificar en `schema_migrations` la versión y checksum sin mostrar la cadena de conexión.
5. Desplegar una nueva revisión de Container App con la misma identidad, red, secretos, CPU, memoria y una sola réplica.
6. Validar health, historial y respuestas 401/403; después configurar el permiso Auth0 y probar administración con tokens reales.
7. Conservar la revisión anterior para rollback de aplicación. La migración 004 es aditiva y no tiene downgrade automático.

La lista de conexiones es local a la instancia, coherente con la réplica única actual. Si se habilitan varias réplicas, esa consulta necesitará un registro distribuido o Azure SignalR antes de representar todas las conexiones.
