# Fase 6C — CD manual y preparación de release

Fecha: 12 de noviembre de 2026. Este documento describe la preparación; no acredita una ejecución de CD ni un cambio de la Container App.

## Estado comprobado

- El CI del commit `44cbac90b62b1c04b8e65dfd2330248f206f0380` terminó correctamente en el run `43307416675`: `build-test`, `secret-scan` y `codeql` aprobaron.
- La aplicación pública observada antes de esta fase continuaba en la revisión `gameproj-dev-app--0000003`, basada en una imagen anterior. La corrección idempotente de `ready=true` aún requiere despliegue.
- Después de ese commit, la rama recibió cambios visuales hasta `b2a5d09`. La imagen autorizada en esta fase se construye deliberadamente desde el archive de `44cbac9`; no incluye esos cambios visuales posteriores.
- PostgreSQL ya tiene el esquema 4. Este release no agrega migraciones después de `004_history_administration.sql`, por lo que el CD debe usar `migration_mode=skip`.
- La imagen se publicó como `gameprojdevqvxef6b7unlocgameacr.azurecr.io/game-server:44cbac90b62b1c04b8e65dfd2330248f206f0380`. Su digest remoto es `sha256:db818d77f92e8c511a78e2116e19326f941a49781247a6c16c77bdad94579aaa`.
- La publicación no actualizó la Container App. Producción continuó en `gameproj-dev-app--0000003` con el digest anterior `sha256:1eadf595aa66f0c529843ca31a098bec604ff92def8b06b50a3ec43e51265836`.
- Los cuatro digests de rollback comprobados (`1eadf5…`, `fd0bfb…`, `bf3434…` y `ffa497…`) permanecen disponibles en ACR.

## Diseño del workflow

`.github/workflows/cd.yml` solo acepta `workflow_dispatch`; un push no puede desplegar. El operador debe proporcionar un SHA completo, seleccionar un GitHub Environment protegido, marcar la confirmación y conservar `migration_mode=skip` cuando el esquema esté actualizado.

Antes de escribir en Azure, el workflow:

1. hace checkout del SHA exacto y exige un árbol limpio;
2. consulta la API de GitHub con `actions: read` y exige un run exitoso de `ci.yml` para ese SHA;
3. obtiene un token Azure efímero por OIDC mediante `id-token: write`;
4. comprueba que el administrador de ACR está deshabilitado y captura la imagen actualmente desplegada;
5. reutiliza una etiqueta existente sin sobrescribirla, o construye y publica `game-server:<SHA>`;
6. resuelve el digest remoto y despliega únicamente `game-server@sha256:...`;
7. comprueba provisioning, imagen exacta, revisión activa, liveness y readiness;
8. registra la imagen anterior como referencia de rollback y no elimina manifiestos.

Las migraciones están desactivadas por defecto. `run-once` exige el texto adicional `RUN MIGRATIONS`, ausencia de otra ejecución activa y una sola ejecución identificada. Para `44cbac9` no debe seleccionarse esa opción.

## Configuración externa pendiente

La API de GitHub confirmó que actualmente no existen Environments ni variables de Actions en el repositorio. Por ello, publicar este workflow no puede iniciar una autenticación OIDC válida ni un despliegue hasta completar deliberadamente la configuración siguiente.

Crear los GitHub Environments `dev` y, solo si se usará, `prod`. `prod` debe tener required reviewers. Cada Environment necesita estas variables, sin Client Secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_RESOURCE_GROUP`
- `ACR_NAME`
- `CONTAINER_APP_NAME`
- `MIGRATION_JOB_NAME` únicamente para una ejecución de migración aprobada

Microsoft Entra debe tener una federated credential por Environment con issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange` y subject:

- `repo:Sir-Hugo/Proyecto-Server:environment:dev`
- `repo:Sir-Hugo/Proyecto-Server:environment:prod`

La identidad OIDC necesita `AcrPush` limitado al ACR y permiso para leer/actualizar la Container App, preferiblemente mediante un rol personalizado al recurso. Si se habilita `run-once`, también necesita leer/actualizar/iniciar exclusivamente el Migration Job. No necesita Owner, administrador ACR, acceso a secretos de Key Vault ni acceso directo a PostgreSQL.

## Ejecución segura

1. Confirmar que el SHA tiene CI verde y que la revisión pública está saludable.
2. Confirmar variables, federated credential y aprobación del Environment `dev`.
3. Abrir **Actions → CD (manual and protected) → Run workflow**.
4. Indicar el SHA completo aprobado, `environment=dev`, `migration_mode=skip` y `confirm_deploy=true`.
5. Revisar la imagen anterior mostrada por el workflow y aprobar el Environment si corresponde.
6. Esperar que la publicación, actualización por digest y ambos health checks terminen correctamente.
7. Probar Auth0, REST, SignalR, dos usuarios y recuperación antes de considerar promovible el release.

## Rollback

El workflow conserva y muestra `previous_image`. Ante una revisión defectuosa, detener nuevas operaciones, comprobar que PostgreSQL no cambió y, con autorización, ejecutar:

```powershell
az containerapp update `
  --resource-group rg-gameproj-dev `
  --name gameproj-dev-app `
  --image '<previous_image_por_digest>'
```

Después deben verificarse provisioning `Succeeded`, una revisión saludable, tráfico, `/health/live`, `/health/ready` y los flujos autenticados. El workflow no elimina la imagen nueva ni la anterior y no hace rollback automático.

## Checklist contrastada con la evidencia disponible

`Proyecto3.pdf` no está en el repositorio ni en los adjuntos disponibles durante esta fase. La tabla se limita a requisitos documentados en README, ROADMAP, informes de validación y la rúbrica transcrita en la auditoría 6A. La fecha de entrega proviene de la instrucción del equipo; la modalidad, el video y la presentación deben comprobarse en el aula virtual o en el PDF original.

| Entregable o requisito | Estado | Evidencia o acción pendiente |
| --- | --- | --- |
| Código backend y frontend | Completo | Rama `avance-11/10`; CI real verde para `44cbac9`. |
| Pruebas dominio, servidor y PostgreSQL 17 | Completo | 152/152 en CI, incluidas 99 PostgreSQL. |
| Pruebas, lint y build frontend | Completo | 3/3, ESLint y build en CI. |
| OpenAPI y AsyncAPI | Completo | Validación automatizada en CI. |
| Docker no-root, puerto 8080 y migrador separado | Completo | Dockerfile y build CI. |
| Migraciones versionadas 001–004 | Completo | Incluidas en la imagen; esquema 4 ya comprobado en Azure. |
| IaC Bicep | Completo en repositorio | Compila en CI; el despliegue real se realizó previamente por bloques manuales. |
| Azure HTTPS/WSS, PostgreSQL privado y Key Vault | Completo y desplegado | Evidencias de fases 3B–3E. |
| Auth0 real y permiso administrativo | Completo con evidencia manual | Informes de fases 3E y 4A. |
| CI de build, pruebas, contratos y seguridad | Completo y verificado | Run `43307416675`. |
| CD por GitHub Actions | Preparado, no verificado | Faltan Environments, OIDC/RBAC y un run manual autorizado. |
| Despliegue dev automático | Pendiente respecto de la rúbrica transcrita | El workflow se dejó manual por seguridad y por instrucción expresa. |
| Producción con aprobación | Preparado, no configurado | Crear Environment `prod` con required reviewers. |
| Digest, revisión, health checks y rollback | Implementado en workflow, no ejecutado | Requiere el primer run controlado. |
| Rendimiento p95 menor de 200 ms | Completo localmente | Fase 5A hasta 20 partidas; no equivale a prueba Azure. |
| OpenTelemetry y métricas propias | Pendiente | La auditoría 6A no encontró instrumentación. |
| Dos alertas operativas | Pendiente | Existe presupuesto, pero no alertas de salud/latencia. |
| Canary, drenado y rollback menor de cinco minutos | Pendiente | No demostrado; Container App usa revisión `Single`. |
| Evidencias visuales | Recopiladas localmente | `evidencias/` permanece sin rastrear; seleccionar las exigidas para la entrega. |
| README, ROADMAP e informes técnicos | Parcialmente actualizado | Revisar enlaces y exportar/copiar los documentos exigidos por la plataforma. |
| Video, presentación y modalidad de entrega | Sin verificar | Consultar el PDF original o aula virtual; no hay fuente local. |
| Fecha límite | Informada por el equipo | 12 de noviembre de 2026, 23:45, hora de Costa Rica. |

## Archivos recomendados para la entrega

Sin una lista oficial disponible, el paquete técnico reproducible incluye el repositorio sin secretos ni artefactos compilados: `src/`, `tests/`, `tools/`, `web-client/`, `infra/`, `scripts/`, `.github/workflows/`, `Dockerfile`, archivos de solución/proyecto, migraciones, README, ROADMAP y contratos/documentos técnicos. `env/.env`, binarios, `node_modules`, datos PostgreSQL y credenciales quedan excluidos.

Las capturas, video, presentación o informe final deben agregarse únicamente según la modalidad oficial confirmada. Los JSON operativos `phase3d*` contienen snapshots sanitizados, pero no son necesarios para compilar ni ejecutar el proyecto.
