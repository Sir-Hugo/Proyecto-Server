# Modelo de amenazas

Este modelo resume los activos, límites de confianza, controles implementados y riesgos residuales del despliegue actual. No acredita controles que no hayan sido probados.

## Activos y límites de confianza

- Identidades Auth0, access tokens y permiso `admin:game`.
- Estado privado de partidas, planes pendientes, historial y auditoría administrativa.
- Credencial de PostgreSQL almacenada en Key Vault.
- Imagen de ACR, workflows y artefactos de compilación.
- Límites: navegador–Auth0, navegador–ingress HTTPS/WSS, Container App–Key Vault/PostgreSQL y GitHub Actions–Azure.

## Amenazas, controles y riesgos residuales

| Amenaza | Controles implementados | Riesgo residual |
| --- | --- | --- |
| Token falsificado, vencido o para otra API | JWT valida firma, issuer, audience y expiración; Auth0 real fue probado. | SignalR recibe el access token en query únicamente bajo `/hubs/game`; proxies no deben registrar la URL completa. No existe ticket WebSocket de un solo uso. |
| Acceso horizontal a salas, partidas o historial | Pertenencia validada en servidor; detalle y replay ajenos responden 404; vistas privadas por participante. | Un defecto futuro en una nueva ruta requiere regresiones equivalentes. |
| Escalada administrativa | Política exige la claim `permissions` con `admin:game`; un rol nominal no concede acceso; acciones persistidas en auditoría append-only. | La asignación de permisos en Auth0 es un control externo y debe revisarse periódicamente. |
| Alteración, repetición o carrera de jugadas | Secuencia, `requestId`, huella del contenido, bloqueos de fila, transacciones y restricciones PostgreSQL. | La coordinación de transporte y límites sigue siendo local a una réplica. |
| Filtración de planes o datos internos | Proyecciones privadas, replay sanitizado, payloads acotados y logs sin tokens, conexiones ni planes. | Identificadores internos aparecen en trazas para correlación; el acceso a telemetría debe mantenerse restringido. |
| Abuso de recursos o entradas | Límites HTTP/SignalR, máximo de conexiones, cuerpos de 64 KiB, mensajes de 8 KiB y validación de contratos. | Los contadores son por instancia; no protegen globalmente un despliegue con varias réplicas. |
| Robo de secretos o acceso a base | PostgreSQL privado con TLS; secreto versionado en Key Vault; identidad administrada y RBAC acotado; archivos locales ignorados. | La rotación y restauración deben operarse y probarse fuera del repositorio. |
| Intercepción o origen web malicioso | HTTPS/WSS, `allowInsecure=false`, HSTS, CORS explícito y orígenes restringidos. | CSP no está habilitada por compatibilidad con Auth0 y debe evaluarse antes de agregar contenido externo. |
| Caída o pérdida de estado confirmado | PostgreSQL, outbox transaccional, recuperación de sesión, migraciones explícitas y health checks. | No se documentó una restauración real desde backup ni una prueba multirréplica. |
| Compromiso de dependencias o pipeline | CodeQL, gitleaks, auditorías NuGet/npm, imagen no-root, SHA y digest inmutables. | Falta un escaneo explícito de la imagen final y una política de bloqueo de CVE críticas. El CD OIDC aún no se ejecutó. |
| Release defectuoso | Health checks, revisión inmutable, digests conservados y rollback documentado. | Canary, drenado de WebSockets y rollback menor de cinco minutos no fueron ensayados. |
| Pérdida de disponibilidad | Liveness/readiness, métricas, trazas y alertas HTTP 5xx/sin réplica. | Una sola réplica mantiene un punto único de indisponibilidad; no se midió el punto de ruptura. |

Las pruebas automatizadas están en `tests/`; la infraestructura y las alertas están en `infra/`; los contratos se publican mediante OpenAPI y `docs/asyncapi.yaml`. La evidencia consolidada y los resultados operativos se incluyen en `Documentacion_Proyecto3.pdf`, entregado por separado fuera del repositorio.
