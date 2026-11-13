# Fase 7B: observabilidad y alertas operativas

Estado al 12 de noviembre de 2026: la instrumentación está desplegada y validada en la revisión `gameproj-dev-app--0000005`. Application Insights recibió solicitudes, trazas, dependencias PostgreSQL y métricas propias. Las dos alertas operativas y su grupo de acción también están desplegados.

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

## Resultados verificados en Azure

Application Insights recibió telemetría de la revisión `gameproj-dev-app--0000005`: solicitudes HTTP, trazas correlacionadas, dependencias PostgreSQL y métricas del `Meter` `Game.Server`. Durante una partida manual autenticada se observaron:

| Métrica | Resultado observado |
| --- | ---: |
| `game.round_plans.processed` | 10 planes |
| `game.round_plan.processing.duration` | 47,965 ms de promedio interno |
| `game.round.resolution.duration` | 5 resoluciones; 0,4818 ms de promedio interno |

Estas duraciones miden secciones internas del servidor. No incluyen transporte de entrada, espera del segundo jugador, entrega SignalR ni renderizado, por lo que no demuestran por sí mismas el objetivo p95 extremo a extremo menor de 200 ms. La inspección realizada no encontró secretos, JWT, cadenas de conexión ni planes privados en los datos revisados.

## Trazas, logs y privacidad

Las actividades propias `match.create`, `match.abandon` y `round.plan.submit` usan identificadores internos de partida y jugador para correlación. Los logs estructurados registran creación, abandono, aceptación de planes, resolución y altas/bajas de conexiones. La correlación estándar de OpenTelemetry adjunta `trace_id` y `span_id` durante una actividad.

No se registran subjects de Auth0, connection strings, JWT, contraseñas, cuerpos completos, planes de jugadores, acciones privadas ni identificadores de conexión. Los fallos PostgreSQL solo registran una categoría acotada y, para errores del servidor, `SQLSTATE`.

## Alertas operativas

[`infra/operational-alerts.bicep`](../infra/operational-alerts.bicep) es un deployment aislado, no está referenciado por `main.bicep` y usa `deployAlerts=false` de forma predeterminada. El deployment `phase7e-operational-alerts` terminó en `Succeeded` con `deployAlerts=true` y creó exclusivamente:

- Grupo de acción `gameproj-dev-app-operations`, habilitado, con un destinatario autorizado y el esquema común de alertas. La dirección no se documenta en el repositorio.
- Alerta `gameproj-dev-app-http-5xx`, habilitada y vinculada al grupo de acción.
- Alerta `gameproj-dev-app-no-ready-replica`, habilitada y vinculada al grupo de acción.

Las condiciones desplegadas son:

1. **HTTP 5xx:** total de `Requests` con `statusCodeCategory=5xx` mayor que 5 durante cinco minutos; severidad 2 y evaluación cada cinco minutos.
2. **Sin réplica:** promedio de `Replicas` menor que 1 durante cinco minutos; severidad 1 y evaluación cada cinco minutos.

Ambas señales son métricas estándar del namespace `Microsoft.App/containerapps`, tienen ventana y frecuencia de evaluación de cinco minutos y apuntan exclusivamente a `gameproj-dev-app`. La segunda detecta ausencia de réplicas, no evalúa directamente la semántica de `/health/ready`; esta limitación evita introducir consultas de logs o pruebas web adicionales.

## Comprobación y mantenimiento de alertas

La comprobación posterior al despliegue debe confirmar que las reglas están habilitadas, que su `scope` corresponde a la Container App y que `actions[0].actionGroupId` corresponde a `gameproj-dev-app-operations`. No se deben provocar fallos reales para validar las notificaciones. Azure puede enviar al destinatario un correo de bienvenida o confirmación del grupo de acción; esto no cambia el estado de las reglas.

Para deshabilitar temporalmente las reglas sin eliminarlas:

```powershell
az monitor metrics alert update --resource-group rg-gameproj-dev --name gameproj-dev-app-http-5xx --enabled false
az monitor metrics alert update --resource-group rg-gameproj-dev --name gameproj-dev-app-no-ready-replica --enabled false
```

Para eliminarlas al finalizar la evaluación, primero eliminar las reglas y después el grupo de acción:

```powershell
az monitor metrics alert delete --resource-group rg-gameproj-dev --name gameproj-dev-app-http-5xx
az monitor metrics alert delete --resource-group rg-gameproj-dev --name gameproj-dev-app-no-ready-replica
az monitor action-group delete --resource-group rg-gameproj-dev --name gameproj-dev-app-operations
```

Estos comandos son procedimientos documentados y requieren autorización antes de ejecutarse.

## Consultas de comprobación en Application Insights

Para repetir la comprobación sin alterar recursos:

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

El despliegue reutiliza Application Insights y Log Analytics existentes y agrega un grupo de acción y dos reglas métricas. Azure cobra las alertas métricas por serie temporal monitoreada; la tarifa publicada incluye las primeras diez series métricas supervisadas por mes. Las notificaciones por correo tienen una franquicia mensual separada. Las dos reglas deberían permanecer dentro de las unidades gratuitas si la suscripción no las ha consumido con otras alertas, pero esto no constituye una garantía de costo cero. Las métricas estándar de plataforma se ingieren sin costo. Se debe revisar la [tarifa vigente de Azure Monitor](https://azure.microsoft.com/pricing/details/monitor/) y el consumo total de la suscripción.

El mayor riesgo de costo es el volumen de requests, dependencias y logs de aplicación exportados. Esta implementación limita logs del dominio y dimensiones propias; no agrega paneles, pruebas web, almacenamiento ni servicios. Debe observarse la ingesta diaria tras el primer despliegue y ajustar sampling o nivel de logs solo con datos reales.

## Limitaciones y pasos mínimos de publicación

- Validación local: build Release sin advertencias; 155/155 pruebas .NET aprobadas (25 dominio, 30 servidor y 100 PostgreSQL); compilación correcta de `main.bicep` y `operational-alerts.bicep`; `git diff --check` correcto. El frontend no fue modificado y, por la restricción de coordinación, no se ejecutaron sus pruebas.
- La prueba de host `StartsAndServesLivenessWithoutApplicationInsightsConnectionString` confirma HTTP 200 en `/health/live` sin configurar el exportador.
- La exportación está desplegada y se verificó en Application Insights con solicitudes, trazas, dependencias PostgreSQL y métricas propias del juego.
- El conteo de partidas realiza una consulta liviana cada 15 segundos; una indisponibilidad de PostgreSQL mantiene el último valor observado y emite un fallo acotado.
- La tasa de jugadas se calcula desde el counter; no existe un instrumento separado por segundo.
- La compilación Bicep, la validación ARM y el `what-if` finalizaron correctamente. El `what-if` mostró exactamente tres creaciones, sin modificaciones ni eliminaciones. Las tres operaciones del deployment terminaron en `Succeeded`.

Después del deployment, `gameproj-dev-app--0000005` permaneció `Healthy`, con una réplica y 100 % del tráfico. PostgreSQL permaneció `Ready` y con acceso público deshabilitado. No se modificaron PostgreSQL, Auth0, red, Key Vault, Log Analytics, Application Insights ni el Migration Job.

Fuentes: [configuración OpenTelemetry de Application Insights](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-configuration?tabs=aspnetcore), [métricas admitidas de Container Apps](https://learn.microsoft.com/azure/azure-monitor/reference/supported-metrics/microsoft-app-containerapps-metrics) y [precios de Azure Monitor](https://azure.microsoft.com/pricing/details/monitor/).
