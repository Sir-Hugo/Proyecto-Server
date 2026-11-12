# Costos previstos en Azure

Fecha de revisión: 10 de noviembre de 2026. La plantilla usa una configuración deliberadamente pequeña, sin alta disponibilidad ni réplicas adicionales. Los importes se dejan pendientes porque dependen de la región, moneda, oferta Azure for Students y precios vigentes al momento de crear los recursos.

## Recursos y supuestos

| Recurso | Configuración inicial | Cantidad o uso supuesto | Estimación mensual |
|---|---|---:|---:|
| Azure Container Apps | Consumption, 0.5 vCPU, 1 GiB, una réplica | 730 horas y tráfico bajo | Pendiente de verificar |
| Container Apps Environment | Consumption | Un ambiente | Pendiente de verificar |
| Azure Database for PostgreSQL Flexible Server | Burstable `Standard_B1ms`, 32 GiB, sin HA ni backup georredundante | 730 horas | Pendiente de verificar |
| Azure Container Registry | Basic | Un registro y pocas imágenes | Pendiente de verificar |
| Key Vault | Standard | Un secreto y pocas operaciones | Pendiente de verificar |
| Log Analytics | Retención de 30 días | Ingesta baja; medir después de la primera prueba | Pendiente de verificar |
| Application Insights | Basado en el mismo workspace | Ingesta baja | Pendiente de verificar |
| Identidad administrada | Asignada por el usuario | Una identidad | Pendiente de verificar |
| Red virtual y dos subredes delegadas | `/27` para Container Apps y `/28` para PostgreSQL | Una red | Sin cargo directo por la VNet; verificar tráfico y servicios asociados |
| Private DNS | Una zona para PostgreSQL y un enlace de VNet | Uso bajo | Pendiente de verificar |

Los parámetros de `infra/dev.bicepparam` y `infra/prod.bicepparam` fijan una sola réplica porque SignalR, la cola rápida y los workers todavía no coordinan varias instancias. PostgreSQL suele ser el gasto fijo más importante aun cuando no hay jugadores. El almacenamiento de PostgreSQL, ACR, Key Vault y Log Analytics puede seguir generando cargos aunque se detenga el cómputo.

Container Apps Consumption incluye concesiones mensuales para consumo de CPU, memoria y solicitudes, según la [documentación de facturación](https://learn.microsoft.com/azure/container-apps/billing). Una réplica mínima activa durante todo el mes puede superar esas concesiones aunque casi no reciba tráfico. PostgreSQL Flexible Server, sus 32 GiB, los backups retenidos, ACR Basic, Private DNS y la ingesta de observabilidad no se vuelven gratuitos por falta de solicitudes. La VNet no tiene un cargo base, pero el tráfico y los servicios conectados sí pueden tenerlo.

El presupuesto mínimo viable debe calcularse en la calculadora oficial con 730 horas de `Standard_B1ms`, 32 GiB, una réplica Container Apps de 0.5 vCPU/1 GiB, ACR Basic, una zona Private DNS y una estimación conservadora de logs y operaciones. A ese total verificado conviene agregar 20 % de margen, sin superar el crédito que realmente quede disponible. Crear alertas al 50 %, 75 %, 90 % y 100 % del presupuesto; las alertas informan, pero no detienen recursos.

## Obtener una cotización oficial

1. Abrir la [Calculadora de precios de Azure](https://azure.microsoft.com/pricing/calculator/) e iniciar sesión con la cuenta que contiene la suscripción Azure for Students.
2. Seleccionar la misma región indicada por el parámetro `location` —inicialmente `eastus2`— y la moneda de facturación de la suscripción.
3. Agregar Azure Container Apps con una réplica de 0.5 vCPU y 1 GiB durante 730 horas. Incluir las solicitudes y transferencia esperadas, sin marcar workload profiles dedicados.
4. Agregar Azure Database for PostgreSQL Flexible Server, tier Burstable, `Standard_B1ms`, 32 GiB, 7 días de backup para dev o 14 para prod, sin HA y sin backup georredundante.
5. Agregar Container Registry Basic, Key Vault Standard, Private DNS, Log Analytics y Application Insights. Para los dos últimos, empezar con una estimación conservadora de ingesta y reemplazarla por el volumen observado tras la primera prueba.
6. Guardar o exportar la cotización con fecha. Copiar los importes verificados a esta tabla antes de crear recursos.
7. Contrastar el total con las páginas oficiales de [Container Apps](https://azure.microsoft.com/pricing/details/container-apps/), [PostgreSQL Flexible Server](https://azure.microsoft.com/pricing/details/postgresql/flexible-server/), [Container Registry](https://azure.microsoft.com/pricing/details/container-registry/), [Key Vault](https://azure.microsoft.com/pricing/details/key-vault/) y [Azure Monitor](https://azure.microsoft.com/pricing/details/monitor/).

No se debe asumir que el crédito estudiantil cubre un mes completo hasta comprobar el saldo y la oferta concreta de la cuenta.

## Detener desarrollo

Para una pausa breve del ambiente `dev`, guardar primero los nombres de recursos y luego detener PostgreSQL y permitir cero réplicas únicamente en dev:

```powershell
az postgres flexible-server stop --resource-group $resourceGroup --name $postgresName
az containerapp update --resource-group $resourceGroup --name $containerAppName --min-replicas 0 --max-replicas 1
```

Antes de una revisión, restaurar PostgreSQL, volver a una réplica y comprobar readiness:

```powershell
az postgres flexible-server start --resource-group $resourceGroup --name $postgresName
az containerapp update --resource-group $resourceGroup --name $containerAppName --min-replicas 1 --max-replicas 1
curl.exe --fail --show-error "https://$publicHost/health/ready"
```

La siguiente aplicación de Bicep también restaura `minReplicas: 1`. Reducir a cero no es apropiado para el ambiente disponible durante la revisión: detiene los workers de outbox y expiración.

Para una pausa larga, la forma más fiable de eliminar cargos es conservar la cotización y parámetros, exportar cualquier dato que deba sobrevivir y borrar **solo el resource group de dev**. Esta acción elimina la base y sus datos, por lo que requiere un backup verificado:

```powershell
az group delete --name $resourceGroup --yes --no-wait
```

## Control del crédito

- Consultar Cost Management + Billing y el saldo de Azure for Students antes y después de cada sesión de despliegue.
- Crear un presupuesto mensual con avisos, por ejemplo al 50 %, 75 % y 90 % del crédito disponible. Los avisos no detienen servicios automáticamente.
- Revisar diariamente costos por recurso durante la primera semana y configurar una alerta de anomalía si la oferta la permite.
- Mantener una sola revisión activa, borrar imágenes antiguas de ACR y evitar aumentar retención de logs, almacenamiento, SKU o réplicas sin una nueva cotización.
- Revisar egreso de red, crecimiento de logs y backups: un error repetitivo o una prueba de carga puede consumir crédito aunque los SKUs sean pequeños.
