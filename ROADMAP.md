# Estado de entrega

## Implementado y validado

- Dominio autoritativo, salas públicas/privadas, cola rápida y partidas de dos jugadores.
- Persistencia PostgreSQL 17, idempotencia, orden por partida, expiración y recuperación de sesión.
- Historial paginado, detalle privado, replay sanitizado y administración auditada con `admin:game`.
- Cliente React integrado, Auth0 real, HTTPS/WSS y partidas consecutivas.
- Docker no-root, Bicep, Azure Container Apps, PostgreSQL privado, Key Vault e identidad administrada.
- CI con backend, frontend, PostgreSQL, contratos, imagen, Bicep, CodeQL y gitleaks.
- OpenTelemetry en Application Insights y dos alertas operativas habilitadas.
- Prueba local reproducible hasta 20 partidas concurrentes.

## Pendiente antes de `v1.0.0`

1. Integrar deliberadamente los últimos cambios del frontend del otro integrante.
2. Resolver cualquier conflicto sin perder el historial visual ni la documentación final.
3. Ejecutar el CI completo sobre el commit integrado y comprobar sus tres jobs.
4. Revisar el árbol y confirmar ausencia de secretos y artefactos temporales.
5. Crear y publicar `v1.0.0` solo desde ese commit aprobado.

## Limitaciones aceptadas para la entrega

- Una sola réplica en Azure; no existe coordinación distribuida para escalar horizontalmente.
- CD preparado, pero no ejecutado desde GitHub Actions porque faltan Environments y federated credentials.
- Canary, drenado de conexiones y rollback menor de cinco minutos no fueron demostrados.
- No se midieron CPU/memoria por partida, punto de ruptura ni una mejora antes/después.
- El repositorio no contiene un bot autónomo; los clientes automatizados están en pruebas y en `scripts/performance`.

La matriz completa y las evidencias están en [`docs/release-readiness.md`](docs/release-readiness.md).
