# Fase 6A — Auditoría de CI/CD y observabilidad

Fecha: 12 de noviembre de 2026. Rama: `avance-11/10`. Esta fase fue de auditoría: no modificó Azure, Auth0, PostgreSQL, workflows ni código de producción.

## 1. Consolidación de rendimiento

La evidencia de Fase 5A se publicó exclusivamente en `origin/avance-11/10`:

- Commit: `04b682e5a280385a7451ace9ba538e3709ef44cb` (`Add reproducible performance benchmarks and results`).
- El SHA local, el de la rama remota y el consultado después del push coinciden.
- Archivos publicados: informe, ejecutor .NET, script PowerShell, instrucciones y JSON de resultados.
- `evidencias/` y los documentos no rastreados `phase3d*` se conservaron intactos y fuera del commit.

El JSON mide 106,954 bytes y contiene 375 muestras individuales y 31 agregados. Los percentiles se vuelven a calcular agrupando las muestras por escenario y operación; no proceden de promedios de lotes. Los conteos son A=10, B=90, C=160, D=75 y E=40. Las operaciones HTTP, conexión SignalR, ronda completa y recuperación se mantienen separadas.

El informe y el JSON coinciden en los p95 de `signalr.round.all-clients`: 52.05 ms con una partida, 64.47 ms con 10 y 101.96 ms con 20. El resultado conserva 0 errores, 0 respuestas 429 y 0 timeouts. No contiene JWT, contraseñas, cadenas de conexión ni datos personales; los subjects `perf|...` son identificadores sintéticos. Las instrucciones crean y destruyen un PostgreSQL 17 aislado y una credencial efímera.

La metodología declara sus límites: TestServer, SignalR Long Polling y PostgreSQL local no equivalen a Azure/WSS. Cinco muestras de línea base y cinco de reconexión son evidencia funcional con baja fuerza estadística; las 30 y 40 rondas concurrentes son suficientes para una conclusión local acotada, no para capacidad de producción. El punto de ruptura no se midió y el consumo de PostgreSQL no forma parte de la métrica del proceso .NET.

## 2. Evidencia real de GitHub Actions

La consulta de GitHub Actions se realizó por API en modo lectura, sin imprimir ni persistir la credencial Git.

| Evidencia | Resultado |
| --- | --- |
| Workflow CI | Activo; se dispara en push y pull request según YAML |
| Run del commit `04b682e` | Run `43229536095`, finalizado en `failure`: `build-test` falló, `secret-scan` aprobó y `codeql` falló en `analyze` |
| Causa de `build-test` | `RepeatedConcurrentReadyRequestsCreateOneRoomMatchAndReturnItsId`: una de dos solicitudes concurrentes recibió 409 `match_already_started`; 98/99 PostgreSQL pasaron |
| Run anterior `9a2883e` | Falló: 152/152 pruebas pasaron, pero `Generate and validate API contracts` terminó con curl exit 7 después de 30 s; el workflow no mostró `server.log` |
| CodeQL anterior | El análisis C# se construyó, pero la carga falló: code scanning no está habilitado y el token no puede acceder al endpoint requerido. Además, el mapa YAML convierte `javascript-typescript` en un input inesperado en vez de incluirlo en `languages` |
| Gitleaks | Aprobó en los dos runs inspeccionados |
| CD | Dos ejecuciones históricas, ambas `skipped`; ningún despliegue exitoso por Actions |
| GitHub Environments | Ninguno configurado |
| Variables de repositorio | Ninguna configurada |
| Protección de `main` | No configurada; API devolvió 404 para branch protection |

La prueba concurrente aprobó localmente durante Fase 5A, por lo que el resultado alojado demuestra una carrera o una expectativa no estable, no una suite verde. No se corrigió en esta fase.

## 3. Matriz CI/CD

Clasificaciones: **verificado**, **implementado no verificado**, **incompleto**, **ausente** y **no aplicable**.

| Punto | Estado | Evidencia y observación |
| --- | --- | --- |
| Push | Verificado | El push de `04b682e` creó el run `43229536095`. |
| Pull request | Implementado no verificado | `pull_request` existe; no se observó un run de PR. |
| Build .NET Release | Verificado | Paso alojado correcto, 0 errores. |
| Pruebas dominio y servidor | Verificado | 25/25 y 28/28 en GitHub. |
| PostgreSQL 17 real | Incompleto | `GAME_POSTGRES_TESTS=1` y Testcontainers ejecutan 99 pruebas; el run actual quedó 98/99 por la carrera de ready. |
| Frontend ESLint y build | Verificado | Están en CI y el run anterior llegó más allá de esos pasos. |
| Pruebas frontend | Ausente | CI nunca ejecuta `npm test`; las 3 pruebas solo están verificadas localmente. |
| Contratos OpenAPI/AsyncAPI | Incompleto | Los pasos existen; el run anterior falló al iniciar/consultar el host y no publica `server.log` para diagnóstico. |
| Bicep y actionlint | Implementado no verificado de extremo a extremo | Existen pasos estáticos, pero los runs rojos no ofrecen una evidencia final verde del pipeline completo. |
| Auditoría de dependencias | Implementado no verificado como control completo | NuGet y `npm audit --audit-level=high` existen; no hay política equivalente para vulnerabilidades moderadas ni artefacto de resultado. |
| Gitleaks | Verificado | Job `secret-scan` verde en los runs inspeccionados. |
| CodeQL | Incompleto | El análisis C# se genera, pero no se publica porque code scanning no está habilitado. La sintaxis inline de `languages` produce `Unexpected input(s) 'javascript-typescript'`, así que JavaScript/TypeScript tampoco queda demostrado. |
| Build Docker en CI | Ausente | CI no construye la imagen; solo CD contiene `docker build`. |
| Escaneo de imagen/SBOM/firma | Ausente | No hay Trivy/Defender equivalente, SBOM ni firma del manifiesto. |
| Publicación ACR | Implementado no verificado | CD haría login y push por SHA; ningún run CD lo ejecutó. Las publicaciones existentes fueron manuales. |
| OIDC Azure | Incompleto | `azure/login@v3` e `id-token: write` existen, pero no hay variables ni environments y no se verificó federación desde Actions. |
| Secretos del pipeline | Parcialmente verificado | OIDC evita un client secret y Gitleaks pasa. Falta configurar environments; las acciones usan tags mayores mutables en vez de SHA inmutable. |
| Despliegue dev automático | Ausente | La rúbrica lo exige; CD está condicionado por `AZURE_DEPLOY_ENABLED`, solo apunta a `main` y nunca ha desplegado. |
| Aprobación manual prod | Ausente | No existe el environment `prod` ni required reviewers. |
| Container Apps | Implementado no verificado | El YAML despliega por Bicep, pero el Azure actual fue desplegado manualmente. |
| Migraciones | Implementado no verificado | El job se actualiza y ejecuta una vez por run; no hay prueba en Actions ni protección contra reejecución manual del workflow. |
| Health checks posteriores | Incompleto | Solo verifica readiness. No comprueba liveness, SPA, REST protegido, SignalR ni revisión/imagen exacta antes de declarar éxito. |
| Rollback | Incompleto | Hay procedimiento documental, pero CD no lo implementa ni se demostró un rollback en menos de cinco minutos. |
| Tráfico y drenado | Ausente | La app usa modo `Single`; no hay división de tráfico, ventana de observación ni drenado explícito. |
| Permisos mínimos | Parcialmente implementado | CI usa `contents: read`; CodeQL agrega `security-events: write`; CD usa `contents: read` e `id-token: write`. La identidad/roles Azure no están verificados desde Actions. |
| Dependencia CI antes de CD | Ausente | Los workflows son independientes; un push a `main` podría desplegar si se habilita la variable aunque el CI del mismo commit falle. |
| Protección de rama/checks requeridos | Ausente | `main` no está protegida y no exige CI verde. |

### Riesgos del pipeline

1. **Crítico académico y operativo:** no existe un run CI verde; una carrera real rompe la suite hospedada.
2. **Alto:** CD podría ejecutar con CI rojo porque no depende de un workflow exitoso ni de branch protection.
3. **Alto:** no hay environments ni aprobación de producción; la condición YAML no sustituye required reviewers.
4. **Alto:** no se ha demostrado OIDC, publicación, migración ni despliegue mediante Actions.
5. **Medio:** la imagen se despliega por tag de commit, no por digest recuperado y verificado después del push.
6. **Medio:** acciones de terceros/oficiales están fijadas a tags mayores (`@v6`, `@v4`, etc.), no a SHA; existe riesgo de cadena de suministro.
7. **Medio:** CodeQL falla por configuración de GitHub; el pipeline aparenta un control que no entrega resultados.
8. **Medio:** el fallo de generación de contratos no conserva ni muestra `server.log`, lo que reduce diagnosticabilidad.
9. **Medio:** faltan pruebas frontend, Docker build y escaneo de imagen en CI.

## 4. Estado real de Azure y observabilidad

Consultas Azure de solo lectura confirmaron la suscripción `Azure for Students`, el grupo `rg-gameproj-dev` y los siguientes componentes relevantes:

- Workspace `gameproj-dev-logs`, `Succeeded`, retención de 30 días.
- Application Insights `gameproj-dev-appi`, `Succeeded`, conectado al workspace.
- Container Apps Environment `gameproj-dev-cae`, `Succeeded`, con destino `log-analytics`.
- Container App `gameproj-dev-app`, modo de revisión `Single`, una réplica mínima/máxima y sondas startup/liveness/readiness.
- Variable `APPLICATIONINSIGHTS_CONNECTION_STRING` configurada en la app.
- Conexión PostgreSQL mediante `secretRef: postgres-connection`; no se consultó su valor.
- Presupuesto mensual `gameproj-dev-monthly-budget` de US$20 con notificaciones al 50, 75, 90 y 100 %.
- Cero metric alerts, scheduled query rules y activity log alerts en el grupo.
- PostgreSQL, ACR y Key Vault no tienen diagnostic settings configurados.

No se instaló ninguna extensión de Azure CLI para consultar Log Analytics o Application Insights. La integración del environment con el workspace y los registros de migración documentados anteriormente demuestran recepción de logs de plataforma/consola; no se obtuvo en esta fase un conteo nuevo de tablas.

## 5. Matriz de observabilidad

| Capacidad | Estado | Evidencia y brecha |
| --- | --- | --- |
| Logging estructurado | Incompleto | Se usan plantillas `ILogger` con propiedades, pero la salida no está configurada como JSON estructurado ni tiene un esquema común. |
| IDs de partida/jugador | Incompleto | Algunos warnings incluyen `MatchId`; no existe cobertura sistemática y no se registra un identificador seguro del jugador como exige la rúbrica. |
| Liveness/readiness | Verificado | Rutas, sondas Azure y PostgreSQL readiness están desplegados y fueron verificados en fases anteriores. |
| Correlation/trace ID | Ausente | No hay middleware, header de correlación ni uso explícito de `Activity`/`TraceIdentifier`. |
| Logs de conexiones | Ausente | `ConnectionRegistry` y `GameHub` agregan/quitan conexiones sin eventos observables. |
| Logs de ciclo de partida | Incompleto | Se registran expiraciones y fallos de entrega; faltan inicio, ronda confirmada, finalización y recuperación con dimensiones consistentes. |
| Métricas HTTP | Ausente | No hay OpenTelemetry, counters/histograms ni panel de latencia/error por ruta. |
| Métricas SignalR | Ausente | No hay conexiones activas, invocaciones, errores o duración como métricas. |
| Métricas de dominio | Ausente | Faltan partidas activas, jugadas/s, latencia de jugada y outbox pendiente. |
| Log Analytics | Verificado en infraestructura | Environment enlazado al workspace con 30 días; recibe consola/plataforma. |
| Application Insights | Incompleto | Recurso y connection string existen, pero el proyecto no referencia SDK de Application Insights ni OpenTelemetry; no produce telemetría propia. |
| OpenTelemetry | Ausente | La rúbrica lo pide explícitamente. |
| Alertas operativas | Ausente | No hay reglas métricas ni KQL. El presupuesto tiene cuatro notificaciones de costo, pero no cubre salud, errores o latencia. |
| Diagnóstico PostgreSQL/ACR/Key Vault | Ausente | No hay diagnostic settings para esos recursos. |
| Sanitización | Parcialmente implementada | Los errores PostgreSQL registran operación y SQLSTATE, sin cadena de conexión; los warnings evitan planes privados. No existe redacción central ni prueba que impida registrar JWT, y los access tokens SignalR pueden viajar en query string durante WebSocket. |
| Auditoría administrativa | Verificado | Las acciones administrativas persisten en PostgreSQL; es auditoría de negocio, no telemetría operacional. |

## 6. Cobertura de la rúbrica

El texto original exige:

- IaC capaz de levantar un ambiente con una ejecución del pipeline.
- CI/CD con build, linter, pruebas, contratos, seguridad, publicación, dev automático y prod con aprobación.
- revisiones con división de tráfico, drenado y rollback documentado menor de cinco minutos;
- identidad administrada para accesos entre servicios y ausencia de cadenas/llaves en configuración;
- OpenTelemetry hacia Application Insights, logs estructurados con IDs seguros, métricas propias, sondas y al menos dos alertas;
- p95 < 200 ms, error < 1 %, sin jugadas perdidas/duplicadas, CPU/memoria por partida y punto de ruptura;
- costo documentado y apagado o escala a cero de dev cuando no se usa.

### Demostrado

- Bicep amplio y despliegue Azure real, aunque ejecutado de forma manual y por bloques.
- ACR, identidad administrada para ACR/Key Vault, Key Vault, red privada PostgreSQL, Container Apps y health probes.
- Costos documentados y presupuesto real con cuatro umbrales.
- Build/test/lint/contratos/seguridad definidos; Gitleaks y partes del CI sí se ejecutan.
- p95 local inferior a 200 ms, 0 % de error y consistencia de secuencias hasta 20 partidas.

### Parcial o no demostrado

- Pipeline único de provisión y despliegue: no ejecutado.
- CI verde: no existe; el run actual falla 1/99 PostgreSQL.
- Dev automático, prod con aprobación, OIDC, ACR y despliegue mediante Actions: no configurados/verificados.
- Tráfico dividido, drenado y rollback <5 minutos: ausentes o no medidos.
- PostgreSQL usa contraseña dentro de una cadena protegida por Key Vault; no usa identidad administrada, por lo que el requisito estricto de identidad no se cumple completamente.
- OpenTelemetry, métricas propias y dos alertas operativas: ausentes.
- Punto de ruptura: no medido. CPU/memoria por partida es una aproximación del proceso TestServer y excluye PostgreSQL.
- Dev no escala a cero: Container App está fijada a una réplica y PostgreSQL permanece aprovisionado; existe un procedimiento manual de apagado, pero no evidencia de política/automatización.
- README y ROADMAP están desactualizados: todavía presentan Auth0, migración 004, pruebas reales, historia y recuperación como pendientes aunque ya se completaron.

## 7. Deficiencias priorizadas

| Prioridad | Deficiencia | Valor académico | Riesgo | Esfuerzo estimado |
| --- | --- | --- | --- | --- |
| P0 | Corregir carrera de ready y obtener CI verde | Muy alto | Medio | Bajo–medio |
| P0 | Hacer diagnosticable/estable el paso de contratos y conservar `server.log` al fallar | Alto | Bajo | Bajo |
| P0 | Agregar `npm test` y Docker build a CI | Alto | Bajo | Bajo |
| P0 | Resolver CodeQL: habilitar code scanning o sustituirlo por escaneo CLI que funcione en este repositorio | Alto | Bajo | Bajo–medio |
| P1 | Configurar branch protection y checks requeridos | Alto | Bajo | Bajo |
| P1 | Crear environments `dev`/`prod`, variables OIDC y required reviewers en prod | Muy alto | Medio | Medio |
| P1 | Impedir CD con CI rojo; desplegar y promover digest inmutable | Muy alto | Medio | Medio |
| P1 | Incorporar OpenTelemetry/Application Insights y métricas requeridas | Muy alto | Medio | Medio |
| P1 | Crear dos alertas operativas verificables | Muy alto | Bajo | Bajo–medio |
| P1 | Diseñar canary/tráfico, drenado y cronometrar rollback | Alto | Medio | Medio–alto |
| P2 | Escaneo de imagen, SBOM y acciones fijadas por SHA | Medio | Bajo | Medio |
| P2 | Diagnostic settings para PostgreSQL, Key Vault y ACR con retención/costo controlados | Medio | Bajo | Bajo–medio |
| P2 | Correlation IDs y política de redacción con pruebas | Alto | Bajo | Medio |
| P2 | Actualizar README/ROADMAP y reunir capturas/evidencia académica | Alto | Bajo | Bajo |
| P3 | Medir punto de ruptura en staging con WSS y telemetría por recurso | Alto | Medio | Medio |
| P3 | Evaluar identidad Entra para PostgreSQL | Alto | Alto | Alto |

## 8. Archivos para una fase posterior

- `.github/workflows/ci.yml`: estabilidad, frontend tests, Docker build/scan, logs de fallo y artefactos.
- `.github/workflows/cd.yml`: dependencia de CI, digest, environments, aprobaciones, smoke tests, canary y rollback.
- `tests/Game.Postgres.Tests/LiveMatchTests.cs` y posiblemente `src/Game.Server/Rooms/InMemoryRoomService.cs`: contrato/idempotencia de ready concurrente.
- `src/Game.Server/Game.Server.csproj` y `src/Game.Server/Program.cs`: OpenTelemetry, métricas, tracing, correlation y redacción.
- `src/Game.Server/Hubs/GameHub.cs`, `Rooms/MatchRoundService.cs` y servicios hospedados: dimensiones y métricas de conexión/partida.
- `infra/main.bicep` y bootstraps: alertas, diagnostic settings y parámetros de telemetría.
- `README.md`, `ROADMAP.md`, `docs/phase3a-validation.md` y documentación de rollback: estado final y evidencia actualizada.
- Configuración remota de GitHub: branch protection, environments, variables OIDC y required reviewers; no se representa totalmente mediante los archivos actuales.

## 9. Propuesta de Fase 6B

1. Reproducir y corregir la carrera de ready con una regresión determinista; obtener 152/152 en GitHub.
2. Corregir el arranque de contratos, imprimir/subir `server.log` sanitizado al fallar, ejecutar `npm test`, construir la imagen y escanearla.
3. Habilitar un análisis de código que funcione en el plan actual y dejar CI completamente verde.
4. Configurar protección de `main`, environments y OIDC con permisos mínimos; mantener CD deshabilitado hasta validar `what-if` y un run manual dev.
5. Instrumentar OpenTelemetry con métricas de partidas, conexiones, jugadas, latencia y errores, sin subjects ni JWT en atributos.
6. Crear y probar al menos dos alertas: readiness/errores y p95 de jugada o ausencia de réplicas saludables.
7. Diseñar una promoción por digest con revisión canary, smoke tests, drenado y rollback cronometrado.
8. Actualizar README/ROADMAP y ejecutar un ensayo académico completo con evidencias, sin realizar producción automáticamente.

La Fase 6B debe separar cambios locales/configuración de una autorización posterior para escribir en GitHub o Azure. La primera entrega revisable debería ser CI verde y observabilidad compilada/probada localmente; después se aprueban por separado branch protection, OIDC, alertas y despliegue dev.
