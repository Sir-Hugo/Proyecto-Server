# Preparación de entrega final

Fecha de revisión: 12 de noviembre de 2026.

La auditoría se realizó contra `Proyecto2-Requirements.docx`, README, ROADMAP, implementación, CI e informes de validación. `Proyecto3.pdf` no está disponible en el repositorio; cualquier requisito exclusivo de ese documento debe cotejarse antes de etiquetar la entrega.

## Matriz de cumplimiento

| Requisito | Archivo o evidencia | Estado | Observaciones |
| --- | --- | --- | --- |
| Código completo del servidor | `src/Game.Domain`, `src/Game.Infrastructure`, `src/Game.Server` | Cumplido | Dominio autoritativo, REST, SignalR, persistencia, historial y administración. |
| Cliente de prueba | `web-client`, `tests/Game.Postgres.Tests/HttpSignalRTests.cs` | Cumplido | Cliente React real y clientes de integración automatizados. |
| Bot de prueba | `scripts/bot` | Cumplido | Dos bots autónomos usan REST y SignalR reales, eligen planes legales y terminan una partida local sin intervención. Usa JWT efímeros y PostgreSQL aislado; no contacta producción. |
| Infraestructura como código | `infra/*.bicep`, `infra/*.bicepparam` | Cumplido | Recursos y deployments aislados en Bicep; el despliegue real se ejecutó por bloques controlados. |
| CI | `.github/workflows/ci.yml` | Cumplido | Build, 155 pruebas .NET, frontend, Docker, Bicep, contratos, dependencias, CodeQL y gitleaks. Run confirmado `43345298193`. |
| CD | `.github/workflows/cd.yml`, `docs/phase6c-cd-release.md` | Parcial | Workflow manual protegido preparado; no se ejecutó porque faltan GitHub Environments y OIDC/RBAC. No hay despliegue automático a dev. |
| OpenAPI 3.1 | `/openapi/v1.json`, `/docs/`, CI y pruebas OpenAPI | Cumplido | Contrato generado por la aplicación y validado por Redocly; no se guarda una copia estática. |
| AsyncAPI 3.0 | `docs/asyncapi.yaml` | Cumplido | Contrato versionado y validado en CI. |
| Pruebas unitarias e integración | `tests/` | Cumplido | Dominio, host, seguridad, PostgreSQL 17, REST, SignalR y recuperación. |
| Scripts de carga | `scripts/performance/` | Cumplido | Ejecución reproducible y resultados JSON versionados. |
| Meta p95 menor de 200 ms | `docs/phase5a-performance-validation.md` | Parcial | Se cumplió localmente hasta 20 partidas, pero la medición no es extremo a extremo por WebSocket ni representa Azure. |
| CPU/memoria por partida y punto de ruptura | `docs/phase5a-performance-validation.md` | Pendiente | No se midieron CPU/memoria por partida ni se elevó carga hasta degradación. |
| Optimización medida antes/después | Informes de rendimiento | Pendiente | No se aplicó una optimización porque la prueba local ya cumplió el umbral; falta comparación cuantitativa. |
| Seguridad y autorización | `docs/phase4a-security-validation.md`, `docs/threat-model.md` | Cumplido | JWT/Auth0, pertenencia, `admin:game`, límites, privacidad, TLS, Key Vault y controles de entrada. Persisten riesgos documentados. |
| Escaneo de imagen sin vulnerabilidades críticas | CI y documento de seguridad | Parcial | Se escanean código, secretos y dependencias; falta un escaneo explícito de la imagen final con política de bloqueo. |
| Evidencia de rendimiento | `scripts/performance/results/phase5a-results.json` | Cumplido | Resultado local reproducible, con límites metodológicos documentados. |
| Ejecución local | `README.md`, `compose.yaml`, archivos `.example` | Cumplido | Docker Compose, migraciones explícitas y valores Auth0 públicos. |
| Despliegue Azure | `docs/phase3b-deployment.md`, `docs/evidence/` | Cumplido | Container App saludable, PostgreSQL privado, Key Vault e identidad administrada. |
| Recuperación y continuidad | `docs/phase2c-validation.md`, evidencia visual | Cumplido | Sesión, estado, outbox e idempotencia comprobados; una sola réplica. |
| Historial, replay y administración | `docs/history-administration-validation.md`, OpenAPI y evidencia | Cumplido | Privacidad por participante, replay sanitizado, permiso real y auditoría persistente. |
| Observabilidad OpenTelemetry | `docs/phase7b-observability.md` | Cumplido | Requests, trazas, dependencias PostgreSQL y métricas del juego recibidas en Application Insights. |
| Dos alertas operativas | `infra/operational-alerts.bicep`, `docs/phase7b-observability.md` | Cumplido | HTTP 5xx y ausencia de réplica habilitadas; no se dispararon mediante fallos artificiales. |
| Canary y drenado de conexiones | Configuración actual y `docs/phase6c-cd-release.md` | Pendiente | Container Apps usa modo `Single`; división de tráfico y drenado no fueron demostrados. |
| Rollback menor de cinco minutos | `docs/phase6c-cd-release.md` | Parcial | Comando y digests están documentados; el tiempo objetivo no fue ensayado. |
| Costos Azure | `docs/azure-costs.md`, `docs/phase3b-deployment.md` | Cumplido | Estimación, presupuesto y riesgos documentados; deben seguir vigilándose. |

## Estado real de la entrega

- Rama auditada: `avance-11/10`, commit inicial de esta fase `8589f7e070872c07082c70eb8811a0836e91f873`.
- Azure: revisión `gameproj-dev-app--0000005`, una réplica saludable.
- Imagen: `sha256:64b45b4ee8339ca88c97542073a833e24b6fc5021c48e29bf980892c3f279da3`.
- PostgreSQL 17: `Ready`, acceso público deshabilitado.
- La rama remota visible `origin/main` apunta a `b2a5d0903edf9f39bed632f18ca7dc717cd8d7b0`; ese commit ya es ancestro de `avance-11/10`. No se encontró otra rama remota del compañero.
- Otro integrante mantiene cambios de frontend fuera de las referencias remotas visibles. No se integraron ni sobrescribieron en esta fase.

## Pendientes antes de crear `v1.0.0`

1. Obtener e integrar conscientemente el trabajo final del frontend.
2. Ejecutar el CI completo sobre el commit integrado y confirmar `build-test`, `secret-scan` y `codeql`.
3. Revisar la versión integrada en Azure o en un ambiente equivalente si cambia el comportamiento.
4. Confirmar en la fuente oficial si se exige video, presentación, bot independiente u otro entregable no incluido en el documento disponible.
5. Revisar el diff final y crear `v1.0.0` únicamente desde el commit aprobado.

No se declara cumplido el CD automático, el escalado horizontal, el canary, el drenado, el punto de ruptura, CPU/memoria por partida ni la optimización antes/después.
