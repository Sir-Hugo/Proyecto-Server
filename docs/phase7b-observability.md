# Fase 7B: observabilidad y alertas operativas

Estado al 12 de noviembre de 2026: la instrumentación está implementada y validada localmente. No está incluida en la imagen desplegada, no se ha publicado una imagen nueva y no se han creado reglas de alerta en Azure.

## Arquitectura de telemetría

`Game.Server` registra OpenTelemetry antes de construir la aplicación. Cuando existe `APPLICATIONINSIGHTS_CONNECTION_STRING`, `UseAzureMonitor()` activa la instrumentación de ASP.NET Core, `HttpClient`, logs, trazas y métricas hacia el Application Insights existente. La cadena se obtiene exclusivamente del entorno; no se almacena en el repositorio ni se escribe en logs. Si la variable está ausente, el servidor conserva los proveedores locales de métricas y trazas y arranca sin exportador.

El paquete `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0 es compatible con `net10.0`. Los orígenes propios son el `ActivitySource` `Game.Server.Operations` y el `Meter` `Game.Server`. También se registra el origen de actividad `Npgsql`; la distribución de Azure Monitor agrega la instrumentación HTTP básica cuando se habilita el exportador.

La telemetría cubre creación y abandono de partidas, recepción de planes, resolución de rondas, conexiones SignalR y fallos acotados. Los health checks existentes siguen disponibles en `/health/live` y `/health/ready`.

## Métricas propias

| Nombre | Tipo | Unidad | Significado |
|---|---|---:|---|
| `game.matches.active` | observable gauge | `{match}` | Partidas con estado `active` contadas en PostgreSQL cada 15 segundos. |
| `game.signalr.connections.active` | observable gauge | `{connection}` | Conexiones autenticadas registradas en la réplica actual. |
| `game.round_plans.processed` | counter | `{plan}` | Planes nuevos aceptados y persistidos. Excluye reintentos idempotentes. Su tasa por segundo representa las jugadas procesadas por segundo. |
| `game.round_plan.processing.duration` | histogram | `ms` | Validación y persistencia transaccional de un plan nuevo, desde la entrada al servicio hasta el retorno de la transacción. Excluye la entrega SignalR y no representa latencia extremo a extremo. |
| `game.round.resolution.duration` | histogram | `ms` | Resolución síncrona del dominio una vez disponibles ambos planes. Excluye persistencia y entrega. |
| `game.failures` | counter | `{failure}` | Fallos relevantes con etiquetas limitadas `operation` y `category`: persistencia, conflictos de operación, recolección PostgreSQL y límite de conexiones. |

Los identificadores de partida o jugador no se usan como dimensiones de métricas, evitando series de alta cardinalidad. El gauge de conexiones es por réplica; el despliegue actual tiene una réplica. Si se escala horizontalmente, debe agregarse por suma para obtener el total.

## Trazas, logs y privacidad

Las actividades propias `match.create`, `match.abandon` y `round.plan.submit` usan identificadores internos de partida y jugador para correlación. Los logs estructurados registran creación, abandono, aceptación de planes, resolución y altas/bajas de conexiones. La correlación estándar de OpenTelemetry adjunta `trace_id` y `span_id` durante una actividad.

No se registran subjects de Auth0, connection strings, JWT, contraseñas, cuerpos completos, planes de jugadores, acciones privadas ni identificadores de conexión. Los fallos PostgreSQL solo registran una categoría acotada y, para errores del servidor, `SQLSTATE`.

## Alertas propuestas

[`infra/operational-alerts.bicep`](../infra/operational-alerts.bicep) es un deployment aislado, no está referenciado por `main.bicep` y usa `deployAlerts=false` de forma predeterminada. Al habilitarlo explícitamente crea un grupo de acción con correo y dos alertas métricas estáticas sobre la Container App existente:

1. **HTTP 5xx:** total de `Requests` con `statusCodeCategory=5xx` mayor que 5 durante cinco minutos; severidad 2 y evaluación cada cinco minutos.
2. **Sin réplica:** promedio de `Replicas` menor que 1 durante cinco minutos; severidad 1 y evaluación cada cinco minutos.

Ambas señales son métricas estándar del namespace `Microsoft.App/containerapps`. La segunda detecta ausencia de réplicas, no evalúa directamente la semántica de `/health/ready`; esta limitación evita introducir consultas de logs o pruebas web adicionales.

## Comprobación posterior en Application Insights

Después de publicar una imagen y actualizar la Container App con una autorización separada:

1. Confirmar que `APPLICATIONINSIGHTS_CONNECTION_STRING` continúa presente como referencia segura y que la revisión queda saludable.
2. Generar una partida controlada, una conexión SignalR y al menos un plan nuevo; repetir el mismo request id para verificar que no aumenta el contador.
3. Esperar varios minutos y consultar Application Insights sin incluir valores sensibles.

Consultas KQL de referencia:

```kusto
requests
| where timestamp > ago(30m)
| summarize total=count(), failures=countif(success == false), p95=percentile(duration, 95) by bin(timestamp, 5m)
```

```kusto
dependencies
| where timestamp > ago(30m)
| summarize total=count(), failures=countif(success == false) by type, bin(timestamp, 5m)
```

```kusto
traces
| where timestamp > ago(30m)
| where message has_any ("Match", "Round", "SignalR")
| project timestamp, severityLevel, message, operation_Id
```

En Metrics Explorer se deben buscar los instrumentos con prefijo `game.` y comprobar conteos, gauges y percentiles de histogramas. Los nombres/tablas finales dependen de la proyección que realice el exportador de Azure Monitor; deben verificarse en el recurso real antes de cerrar la evidencia académica.

## Costos y recursos

La implementación local y la compilación Bicep no crean costos. El despliegue posterior reutilizaría Application Insights y Log Analytics existentes, y agregaría un grupo de acción y dos reglas métricas. Azure cobra principalmente por volumen de telemetría ingerida y retenida; las alertas métricas se cobran por serie temporal monitoreada, con unidades gratuitas según la oferta, y los correos se cobran después de su franquicia. Las métricas estándar de plataforma se ingieren sin costo. Se debe revisar la [tarifa vigente de Azure Monitor](https://azure.microsoft.com/pricing/details/monitor/) para la suscripción antes de habilitar las reglas.

El mayor riesgo de costo es el volumen de requests, dependencias y logs de aplicación exportados. Esta implementación limita logs del dominio y dimensiones propias; no agrega paneles, pruebas web, almacenamiento ni servicios. Debe observarse la ingesta diaria tras el primer despliegue y ajustar sampling o nivel de logs solo con datos reales.

## Limitaciones y pasos mínimos de publicación

- Validación local: build Release sin advertencias; 155/155 pruebas .NET aprobadas (25 dominio, 30 servidor y 100 PostgreSQL); compilación correcta de `main.bicep` y `operational-alerts.bicep`; `git diff --check` correcto. El frontend no fue modificado y, por la restricción de coordinación, no se ejecutaron sus pruebas.
- La prueba de host `StartsAndServesLivenessWithoutApplicationInsightsConnectionString` confirma HTTP 200 en `/health/live` sin configurar el exportador.
- La exportación fue configurada y probada por compilación/inicio local sin connection string, pero todavía no fue desplegada ni verificada dentro de Application Insights.
- El conteo de partidas realiza una consulta liviana cada 15 segundos; una indisponibilidad de PostgreSQL mantiene el último valor observado y emite un fallo acotado.
- La tasa de jugadas se calcula desde el counter; no existe un instrumento separado por segundo.
- Las alertas compilan, pero no se ejecutó validación ARM ni `what-if` y no se crearon recursos.

Para publicar: revisar el diff, crear un commit autorizado, esperar CI, construir y publicar una imagen por digest, actualizar únicamente la imagen de la Container App, ejecutar las comprobaciones anteriores y, con otra autorización explícita, validar/desplegar `operational-alerts.bicep` con `deployAlerts=true` y el correo real. PostgreSQL, Auth0, red, Key Vault y el Migration Job no requieren cambios.

Fuentes: [configuración OpenTelemetry de Application Insights](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-configuration?tabs=aspnetcore), [métricas admitidas de Container Apps](https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-app-containerapps-metrics) y [precios de Azure Monitor](https://azure.microsoft.com/pricing/details/monitor/).
