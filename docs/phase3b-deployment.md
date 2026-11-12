# Fase 3B.2 — Despliegue real en Azure

Fecha: 11 de noviembre de 2026. Estado: **migraciones 001–003 aplicadas; Container App desplegada y verificaciones públicas iniciales aprobadas; configuración remota de Auth0 y pruebas con dos usuarios pendientes**. No se modificó GitHub ni Auth0 remoto.

## Punto de control actual — 11 de noviembre de 2026

- Los 14 componentes base de `gameproj-dev` están aprovisionados. Los recursos regionales usan `northcentralus`; el grupo permanece en `centralus`.
- ACR conserva el administrador deshabilitado. La imagen `game-server:196bf2041049de68189c31c9fdc3c6d474e7ffe6` está publicada con digest `sha256:ffa4975c40a0a5a6cf34e5d801f9ff711e1ed753b4d093632dd7dc02aabebe12`.
- Key Vault usa RBAC. La identidad `gameproj-dev-identity` tiene `AcrPull` y `Key Vault Secrets User` limitados a sus recursos correspondientes. El secreto `postgres-connection` existe con una versión identificable; su valor no fue consultado ni registrado.
- Las subredes `container-apps` y `postgresql` continúan aprovisionadas y con `privateEndpointNetworkPolicies: Disabled`; esta preparación no las modifica.
- El template aislado `infra/postgres-bootstrap.bicep` referencia VNet, subred PostgreSQL, DNS privado, enlace DNS, identidad y Key Vault mediante `existing`. Solo declara PostgreSQL Flexible Server, la base `game` y `postgres-connection`.
- Compilación Bicep: aprobada. Validación ARM: `Succeeded`.
- `what-if` `FullResourcePayloads`: `Succeeded`, 3 `Create`, 10 `Ignore`, 0 `Modify`, 0 `Delete` y 0 reemplazos. Las creaciones previstas son `gameproj-dev-pg-qvxef6b7unloc`, su base `game` y el secreto `postgres-connection`.
- El deployment aislado `phase3b-postgres-bootstrap` terminó en `Succeeded` a las `2026-11-12T01:45:10Z`. La contraseña definitiva se generó criptográficamente, se transmitió mediante un archivo ARM temporal fuera del repositorio y ese archivo fue sobrescrito y eliminado al terminar. El valor no apareció en argumentos, salidas ni documentación.
- PostgreSQL `gameproj-dev-pg-qvxef6b7unloc` está `Ready`: versión 17, `Standard_B1ms`, Burstable, 32 GB, auto-grow, sin HA, retención de backup de 7 días y sin backup georredundante.
- La base `game` existe con charset `UTF8` y collation `en_US.utf8`.
- Red y TLS: acceso público `Disabled`, subred delegada y zona DNS privada aprobadas, `require_secure_transport=on`, mínimo `TLSv1.2` y cadena cliente configurada con `SSL Mode=VerifyFull`.
- El secreto `postgres-connection` existe con URI versionada terminada en `bed1303d7ee14d6e9cc761608d33b66f`. Solo se consultaron metadatos de ARM; el contenido permaneció oculto.
- Los 14 componentes base fueron verificados nuevamente: ninguno falta, ambas asignaciones RBAC continúan presentes, ACR conserva el administrador deshabilitado y las políticas de red de las subredes permanecen en `Disabled`.
- El Migration Job existe y tuvo una sola ejecución `Succeeded`; no quedan ejecuciones activas. `gameproj-dev-app` está desplegada con una revisión saludable y una réplica lista.
- Costo actual estimado: unos US$31.86/mes si la réplica cumple las condiciones de tarifa inactiva, o US$55.68 si se factura activa todo el mes, antes de logs, egreso y operaciones variables.

## 1. Estado comprobado

### Etapa 2 preparada — Migration Job

- Se creó `infra/migration-job-bootstrap.bicep`. Declara únicamente `Microsoft.App/jobs/gameproj-dev-migrate` y referencia mediante `existing` el Container Apps Environment, la identidad administrada, ACR, Key Vault y la versión existente de `postgres-connection`.
- Nombres confirmados con Azure CLI: entorno `gameproj-dev-cae`, identidad `gameproj-dev-identity` y ACR `gameprojdevqvxef6b7unlocgameacr`.
- Imagen inmutable: `gameprojdevqvxef6b7unlocgameacr.azurecr.io/game-server@sha256:ffa4975c40a0a5a6cf34e5d801f9ff711e1ed753b4d093632dd7dc02aabebe12`.
- La imagen usa UID no-root `1654`, contiene `/app/database/Game.Database.dll`, incluye el bundle CA de Linux y permite iniciar los comandos reales `migrate` y `status`. Ambos alcanzaron la validación de `ConnectionStrings__GameDatabase` durante la inspección local sin conectarse a PostgreSQL.
- Configuración del job: trigger manual, timeout 900 segundos configurable, cero reintentos, paralelismo 1, una finalización requerida, 0.25 vCPU y 0.5 GiB. No define ingress HTTP.
- ACR usa la identidad administrada y su asignación `AcrPull` existente. La conexión usa una referencia versionada de Key Vault y la variable exacta `ConnectionStrings__GameDatabase`; el secreto no se copia al template ni a sus salidas.
- El entorno `gameproj-dev-cae` está `Succeeded`, integrado a la subred `container-apps` y configurado para enviar logs a Log Analytics. PostgreSQL continúa `Ready`, con acceso público deshabilitado, DNS privado vinculado y TLS `VerifyFull` en la cadena almacenada.
- La primera compilación detectó que la ubicación de un recurso `existing` no puede usarse como valor calculable al inicio. Se corrigió agregando el parámetro explícito `location=northcentralus`; no se ejecutó ningún deployment durante esa corrección.
- Compilación final: aprobada. Validación ARM: `Succeeded`.
- `what-if` `FullResourcePayloads`: `Succeeded`, 1 `Create` para `gameproj-dev-migrate`, 11 `Ignore`, 0 `Modify`, 0 `Delete` y 0 reemplazos.
- El deployment aislado `phase3b-migration-job-bootstrap` terminó en `Succeeded` a las `2026-11-12T01:57:43Z`. Creó exclusivamente `gameproj-dev-migrate`.
- El job está `Succeeded` en `northcentralus` y conserva trigger `Manual`, timeout 900 segundos, cero reintentos, paralelismo 1, una finalización, 0.25 vCPU y 0.5 GiB.
- La configuración desplegada usa la identidad `gameproj-dev-identity`, el digest aprobado, el comando `dotnet /app/database/Game.Database.dll migrate` y la referencia versionada de `postgres-connection`. Solo se consultaron metadatos seguros.
- La lista de ejecuciones está vacía (`ExecutionCount=0`): crear el job no inició migraciones automáticamente.
- PostgreSQL continúa `Ready` con acceso público deshabilitado; las políticas de red de las dos subredes permanecen `Disabled`, ACR conserva el administrador deshabilitado y no existe Container App.
- El costo actual permanece cerca de US$0.71/día. Un job manual sin ejecuciones no consume CPU o memoria. Iniciar una sola ejecución requiere una segunda autorización explícita.

### Ejecución autorizada de migraciones

- Se comprobó antes del inicio que PostgreSQL estaba `Ready` y que el job tenía cero ejecuciones activas o históricas.
- Se envió exactamente una orden de inicio. Identificador: `gameproj-dev-migrate-pm7jsvg`.
- Estado final: `Succeeded`. Inicio `2026-11-11T20:02:32-06:00`; fin `2026-11-11T20:03:05-06:00`.
- Log Analytics registró `Committed schema migration 1`, `2` y `3`, seguido de `Schema version: 3`. No aparecieron errores de DNS, TLS, autenticación, ACR, Key Vault o PostgreSQL, ni datos sensibles.
- Historial verificado:
  - `001_initial.sql`: `233C9068D710D26959D0A27BBE8E3F311C25A5258A904911A4901506E12E53F2`.
  - `002_live_matches.sql`: `CF3108B1E200B79D6EFA256FFB7CFFC0B7E18C3C117759E134E4E72D14911020`.
  - `003_session_recovery.sql`: `8BEA74D1AED595E24E6A1ECE016950B1105917ADC1C4B3CFF9A4E906CB994B6A`.
- Los tres checksums almacenados en Azure coinciden con los archivos locales después de normalizar finales de línea, igual que `SchemaMigrator`.
- El migrador ejecuta el DDL y registra cada versión dentro de una sola transacción; solo imprime el historial después del commit. Por ello el commit y los checksums prueban la aplicación de las diez tablas esperadas: `schema_migrations`, `matches`, `match_participants`, `match_events`, `match_commands`, `pending_round_plans`, `active_match_players`, `rooms`, `room_members` y `match_outbox`.
- No se ejecutó un `SELECT` independiente posterior sobre `information_schema`: la autorización permitía una sola ejecución y PostgreSQL no tiene acceso público. La misma ejecución de `migrate` llamó a `GetVersionsAsync`, que es el mecanismo usado por el comando `status`, y devolvió las tres versiones desde la base.
- Estado posterior: una ejecución total, una `Succeeded`, cero activas y cero adicionales. PostgreSQL permanece `Ready` y con acceso público `Disabled`.
- La siguiente etapa autorizada fue crear exclusivamente la Container App mediante el template aislado; no se repitieron las migraciones.

### Etapa 3 desplegada — Container App

- Se creó `infra/container-app-bootstrap.bicep`. El ARM compilado contiene un solo recurso desplegable: `Microsoft.App/containerApps/gameproj-dev-app`. Container Apps Environment, identidad administrada, ACR, Key Vault, el secreto versionado y Application Insights son referencias `existing`.
- La ubicación regional está limitada explícitamente a `northcentralus`. El entorno `gameproj-dev-cae` está `Succeeded`, usa la subred `container-apps`, tiene `internal=false` y admite ingress público sin cambiar su red.
- Imagen inmutable: `gameprojdevqvxef6b7unlocgameacr.azurecr.io/game-server@sha256:ffa4975c40a0a5a6cf34e5d801f9ff711e1ed753b4d093632dd7dc02aabebe12`. ACR confirma ese digest y mantiene el administrador deshabilitado. La imagen usa UID no-root `1654` y expone el puerto 8080.
- Configuración desplegada: 0.5 vCPU, 1 GiB, mínimo y máximo de una réplica, revisión única, ingress externo, `allowInsecure=false`, puerto 8080 y transporte `auto`, compatible con WebSocket. No hay credenciales de ACR; el pull usa `gameproj-dev-identity` con `AcrPull` acotado al registro.
- La conexión se monta únicamente como `ConnectionStrings__GameDatabase` mediante la referencia versionada `postgres-connection` de Key Vault y la misma identidad con `Key Vault Secrets User` acotado al vault. No se incluye la cadena en Bicep, parámetros, salidas o logs y el servidor no ejecuta migraciones al arrancar.
- Sondas desplegadas: startup en `/health/live` durante hasta 90 segundos, liveness en `/health/live` y readiness en `/health/ready`, todas por HTTP interno en 8080. Liveness solo comprueba el proceso; readiness ejecuta `SELECT 1` contra PostgreSQL.
- El backend exige Authority, Audience y Client ID en producción, valida JWT con metadata HTTPS y acepta el token de query string exclusivamente en `/hubs/game`. REST y el hub requieren autorización. CORS y los orígenes WebSocket quedan limitados al origen HTTPS de la aplicación.
- La inspección directa de la imagen publicada confirmó que el bundle no contiene `https://localhost:3000` ni los valores Auth0 locales. Conserva `/config/client.json`, usa `window.location.origin` para REST, SignalR, callback y logout, por lo que no requiere reconstrucción.
- Los tres valores Auth0 públicos se cargaron desde `env/.env`, archivo ignorado y no rastreado. No se incluyeron Client Secrets, contraseñas, tokens ni cadenas de conexión. Auth0 remoto no fue modificado.
- Compilación Bicep: aprobada. Validación ARM: `Succeeded`, un recurso validado.
- `what-if` `FullResourcePayloads`: `Succeeded`, 1 `Create` para `gameproj-dev-app`, 12 `Ignore`, 0 `Modify`, 0 `Delete` y 0 reemplazos. Los doce `Ignore` son los recursos existentes del grupo; no se prevén cambios sobre ellos.
- El deployment `phase3b-container-app-bootstrap` terminó en `Succeeded` el 11 de noviembre de 2026 a las 20:27:41 -06:00, duró aproximadamente 17 segundos y creó únicamente `gameproj-dev-app`.
- La auditoría de operaciones muestra una operación `Create` para la aplicación, cuatro operaciones `Read` sobre recursos `existing` y una evaluación de salidas. No hubo escrituras sobre ACR, Container Apps Environment, Application Insights, Key Vault, PostgreSQL o red.
- Estado observado: aplicación `Running`; revisión `gameproj-dev-app--ufavaq1` `Healthy`, activa y con 100 % del tráfico; una réplica `Running`, contenedor listo, iniciado y con cero reinicios.
- La revisión ejecuta exactamente el digest aprobado. El sistema registró el pull exitoso por identidad y el inicio del contenedor, sin errores de plataforma.
- URL pública real: `https://gameproj-dev-app.salmonmoss-7dc2e6dd.northcentralus.azurecontainerapps.io`.
- HTTP→HTTPS responde 301. `/health/live` y `/health/ready` responden 200. Readiness prueba que la aplicación accede al PostgreSQL privado mediante la referencia versionada de Key Vault.
- La SPA responde 200 cuando se usa el encabezado de navegador `Accept: text/html`; contiene el punto de montaje React y su JavaScript compilado responde 200. Un `Accept: */*` deliberado devuelve 404 por el diseño del fallback, para no convertir rutas API desconocidas en HTML.
- `/config/client.json` responde 200 y contiene únicamente `auth0Domain`, `auth0ClientId` y `auth0Audience`, todos completos. `/docs/` y `/openapi/v1.json` responden 200; OpenAPI reporta versión 3.1.1.
- `GET /api/rooms`, la negociación SignalR y un handshake WebSocket a `/hubs/game` sin token responden 401. Esto confirma enrutamiento HTTPS/WSS y autenticación obligatoria; una conexión autenticada todavía requiere configurar Auth0 y probarla con un usuario real.
- Los logs de sistema no contienen errores. Los dos mensajes de autorización fallida en consola corresponden a las pruebas anónimas 401. No aparecieron fallos de PostgreSQL, Key Vault, ACR, Auth0 o inicialización.
- Falta registrar la URL pública real en Allowed Callback URLs, Allowed Logout URLs y Allowed Web Origins de Auth0. El login, SignalR autenticado, partida con dos usuarios, reinicio y recuperación no se declaran validados todavía.

- Suscripción seleccionada: Azure for Students, habilitada, sin publicar identificadores.
- Rol efectivo: Owner.
- Azure CLI: 2.92.0.
- Proveedores requeridos: registrados.
- Crédito informado y confirmado por el usuario: US$100.
- Git: rama `avance-11/10`, remoto `Sir-Hugo/Proyecto-Server`, cambios locales todavía sin commit ni push.
- Grupo del proyecto: `rg-gameproj-dev`, creado en `centralus` y etiquetado únicamente como ambiente dev; sus recursos regionales usan `northcentralus`.

La validación inicial rechazó `centralus`; después de autorizar `northcentralus`, se desplegaron por bloques aislados los recursos base, PostgreSQL y el Migration Job, y se ejecutaron una sola vez las migraciones. Bicep principal y parámetros dev compilan sin diagnósticos. `docker build --check .` también terminó sin advertencias.

## 2. Bloqueo regional y decisión pendiente

Aunque `eastus2` ofrece PostgreSQL públicamente, la consulta de capacidades para esta suscripción devuelve una restricción de aprovisionamiento. La posterior validación de `centralus` también fue rechazada para VNet, ACR, Log Analytics, Application Insights e identidad administrada por la política **Allowed resource deployment regions**.

```text
Subscriptions are restricted from provisioning in this region.
```

La política permite exclusivamente `chilecentral`, `brazilsouth`, `northcentralus`, `westus` y `spaincentral`. Una comprobación independiente confirma que `northcentralus` no está restringida para PostgreSQL, muestra B1ms y tiene cuota 0/1 de Managed Environment. No se sustituyó la región automáticamente.

Opciones:

1. **North Central US — recomendada para la entrega:** mantener el grupo ya creado y desplegar dentro de él todos los recursos regionales en `northcentralus`. La ubicación del grupo solo almacena metadatos y no obliga a que sus recursos usen la misma región.
2. Mantener `eastus2` o `centralus`: abrir una solicitud de excepción de política/límites y esperar aprobación. El plazo universitario hace incierto este camino.

No se recomienda dividir Container Apps y PostgreSQL entre regiones: la integración por subred delegada es regional y la alternativa exigiría otra topología, más costo y nuevas pruebas.

## 3. Recursos propuestos

Un solo ambiente `dev`, con nombres derivados de `gameproj-dev`:

| Recurso | Configuración |
|---|---|
| Resource group | `rg-gameproj-dev`, ya creado en `centralus` y vacío |
| Recursos regionales | `northcentralus`, pendiente de autorización específica |
| Virtual Network | `10.20.0.0/16` |
| Subred Container Apps | `/27`, delegada a `Microsoft.App/environments` |
| Subred PostgreSQL | `/28`, delegada a `Microsoft.DBforPostgreSQL/flexibleServers` |
| Private DNS | Zona de PostgreSQL y enlace a la VNet |
| PostgreSQL Flexible Server | 17, Burstable `Standard_B1ms`, 32 GB, una zona, sin HA ni backup georredundante |
| Base de datos | `game`, UTF-8 |
| Container Apps Environment | Consumption, externo para HTTPS/WSS, integrado a VNet |
| Container App | 0.5 vCPU, 1 GiB, exactamente una réplica |
| Migration Job | Manual, 0.25 vCPU, 0.5 GiB; ejecuta `Game.Database migrate` dentro de la VNet |
| Container Registry | Basic, administrador deshabilitado |
| Key Vault | Standard, RBAC, soft delete de 7 días |
| Identidad administrada | User-assigned, `AcrPull` y `Key Vault Secrets User` con ámbito de recurso |
| Observabilidad | Log Analytics, 30 días, y Application Insights basado en workspace |

PostgreSQL mantiene `publicNetworkAccess: Disabled`. La aplicación y el job resuelven el FQDN mediante Private DNS y usan `SSL Mode=VerifyFull`. Bicep crea `postgres-connection` como un recurso secreto de Key Vault a partir del parámetro `@secure()`; su valor no se imprime ni se incluye en el cliente.

## 4. Estimación de costo

Precios minoristas USD consultados el 11 de noviembre de 2026 mediante la [Azure Retail Prices API](https://learn.microsoft.com/rest/api/cost-management/retail-prices/azure-retail-prices). Son estimaciones antes de impuestos, descuentos u otras condiciones de Azure for Students. La tabla corresponde a North Central US.

Supuestos: Central US, 730 horas/mes, 30.42 días, tráfico bajo, menos de 10 GB en ACR, menos de 5 GB/mes de logs, una zona DNS y una réplica siempre disponible.

| Componente | Precio usado | Diario aproximado | Mensual aproximado |
|---|---:|---:|---:|
| PostgreSQL B1ms | US$0.017/h | US$0.41 | US$12.41 |
| PostgreSQL 32 GB | US$0.115/GB-mes | US$0.12 | US$3.68 |
| ACR Basic | US$0.1666/día | US$0.17 | US$5.07 |
| Private DNS | primeras 25 zonas: US$0.50/zona-mes | US$0.02 | US$0.50 |
| Key Vault | US$0.03/10 000 operaciones | < US$0.01 | < US$0.01 con uso bajo |
| Log Analytics/App Insights | primeros 5 GB/mes gratuitos | US$0 con uso bajo | US$0 con uso bajo |
| Container Apps, mayormente inactiva | después de concesiones gratuitas | ~US$0.39 después de agotar la concesión | ~US$10.21 |
| Container Apps, activa todo el mes | peor supuesto razonable para esta réplica | ~US$1.30 después de la concesión | ~US$34.02 |

Total mensual estimado: **US$31.86** si la réplica permanece mayormente inactiva, y **US$55.68** si se factura como activa durante todo el mes. Durante los primeros cuatro días aproximados del ciclo mensual, las concesiones de 180 000 vCPU-s y 360 000 GiB-s pueden cubrir la réplica; un despliegue de dos días con poco tráfico se estima alrededor de **US$1.43**, más logs, egreso u operaciones no previstas. La [página oficial de Container Apps](https://azure.microsoft.com/pricing/details/container-apps/) documenta las concesiones y la facturación activa/inactiva.

Riesgos de facturación:

- Los workers periódicos pueden hacer que Container Apps alterne entre tarifa inactiva y activa.
- PostgreSQL, almacenamiento, ACR y DNS continúan cobrando con tráfico cero.
- Errores repetitivos pueden superar los 5 GB gratuitos de Azure Monitor; después, el medidor observado es US$2.76/GB.
- El auto-grow de PostgreSQL puede aumentar almacenamiento.
- El entorno de `northcentralus` informa 0 de 100 cores Consumption en uso; la réplica propuesta de 0.5 vCPU cabe en la cuota observada. La cuota sigue siendo un límite operativo, no una garantía de aprovisionamiento.
- Detener PostgreSQL no elimina almacenamiento, ACR, DNS ni logs.

Presupuesto recomendado: US$20 para el período de revisión, con alertas al 50 %, 75 %, 90 % y 100 %. Si se conserva un mes completo, elevarlo al rango mensual aprobado y vigilarlo diariamente.

## 5. Configuraciones manuales pendientes

- `northcentralus` quedó autorizada para validación y what-if; el despliegue todavía requiere aprobación final.
- Authority, Audience y Client ID público están configurados en `env/.env`, que Git ignora y no rastrea.
- Cuando exista la URL pública, agregarla exactamente a Allowed Callback URLs, Allowed Logout URLs y Allowed Web Origins de Auth0.
- GitHub OIDC y los Environments `dev`/`prod` siguen pendientes. El despliegue inicial será manual con Azure CLI.
- Los workflows y cambios locales deben recibir commit/push autorizado antes de usar GitHub Actions. `AZURE_DEPLOY_ENABLED` debe permanecer deshabilitado mientras tanto.
- El destinatario de presupuesto se obtuvo de la cuenta autenticada sin imprimirlo. Bicep incluye un presupuesto mensual de US$20 con alertas reales al 50 %, 75 %, 90 % y 100 % para ese correo y el rol Owner.

El cliente de producción obtiene únicamente Authority, Audience y Client ID públicos desde `/config/client.json`. El Dockerfile no define Client Secret, el bundle se construye sin archivos `.env`, y los archivos locales están ignorados por Git.

## 6. Secuencia histórica de preparación — no ejecutar

El bloque siguiente conserva el plan original anterior al despliegue por etapas y queda reemplazado por los templates aislados. No debe ejecutarse nuevamente ni usarse `infra/main.bicep` para recrear la aplicación. La creación aprobada se realizó con `az deployment group create`, `infra/container-app-bootstrap.bicep` y un archivo temporal local con los tres parámetros públicos Auth0; el archivo fue eliminado después de las verificaciones.

```powershell
$resourceGroup = 'rg-gameproj-dev'
$location = 'northcentralus' # despliegue pendiente de autorización final
$namePrefix = 'gameproj'
$environmentName = 'dev'
$imageTag = 'phase3b-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')

$auth0Authority = $env:AUTH0_AUTHORITY
$auth0Audience = $env:AUTH0_AUDIENCE
$auth0ClientId = $env:AUTH0_CLIENT_ID
if (!$auth0Authority -or !$auth0Audience -or !$auth0ClientId) {
  throw 'Falta configuración pública de Auth0'
}

az group create --name $resourceGroup --location $location

# Recursos compartidos, identidad y roles acotados.
az deployment group create --resource-group $resourceGroup --name phase3b-bootstrap `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    deployRoleAssignments=true deployData=false deployMigrationJob=false deployApplication=false

$outputs = az deployment group show --resource-group $resourceGroup --name phase3b-bootstrap `
  --query properties.outputs -o json | ConvertFrom-Json
$registryName = $outputs.registryName.value
$loginServer = $outputs.registryLoginServer.value

# Imagen local e inmutable en ACR.
az acr login --name $registryName
docker build --pull --tag "${loginServer}/game-server:${imageTag}" .
docker push "${loginServer}/game-server:${imageTag}"

# Contraseña aleatoria, nunca mostrada. Bicep crea PostgreSQL y el secreto de Key Vault.
$passwordBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
$env:POSTGRES_BOOTSTRAP_PASSWORD = [Convert]::ToHexString($passwordBytes)
try {
  az deployment group create --resource-group $resourceGroup --name phase3b-data `
    --template-file infra/main.bicep `
    --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
      auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
      postgresAdministratorPassword="$env:POSTGRES_BOOTSTRAP_PASSWORD" `
      deployRoleAssignments=false deployData=true deployMigrationJob=false deployApplication=false
  if ($LASTEXITCODE -ne 0) { throw 'Falló el despliegue de datos' }
}
finally {
  Remove-Item Env:POSTGRES_BOOTSTRAP_PASSWORD -ErrorAction SilentlyContinue
  Remove-Variable passwordBytes -ErrorAction SilentlyContinue
}

# Job dentro de la VNet y migraciones 001–003.
az deployment group create --resource-group $resourceGroup --name phase3b-job `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag=$imageTag deployRoleAssignments=false deployData=false deployMigrationJob=true deployApplication=false
$jobName = "${namePrefix}-${environmentName}-migrate"
$execution = az containerapp job start --resource-group $resourceGroup --name $jobName --query name -o tsv
$status = ''
for ($attempt = 0; $attempt -lt 90; $attempt++) {
  $status = az containerapp job execution show --resource-group $resourceGroup --name $jobName `
    --job-execution-name $execution --query properties.status -o tsv
  if ($status -in @('Succeeded', 'Failed')) { break }
  Start-Sleep -Seconds 10
}
if ($status -ne 'Succeeded') { throw "La migración terminó en estado $status" }

# Solo después de observar Succeeded.
az deployment group create --resource-group $resourceGroup --name phase3b-app `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag=$imageTag deployRoleAssignments=false deployData=false deployMigrationJob=true deployApplication=true

$publicUrl = az deployment group show --resource-group $resourceGroup --name phase3b-app `
  --query properties.outputs.publicUrl.value -o tsv
```

Después de configurar Auth0 se verificarán `/health/live`, `/health/ready`, `/config/client.json`, SPA, REST, WebSocket/SignalR, partida real con dos cuentas, reinicio de revisión y recuperación. No se usarán JWT de prueba para declarar producción válida.

## 7. Recursos desplegados y resultados

- El grupo `rg-gameproj-dev` permanece en `centralus`; los recursos regionales están en `northcentralus`.
- Existen los 14 componentes base, PostgreSQL Flexible Server, la base `game`, el secreto versionado y el Migration Job manual.
- Migraciones ejecutadas: exactamente una ejecución en Azure, `Succeeded`, con versiones 001–003 y checksums verificados.
- PostgreSQL permanece `Ready`, privado y con TLS; ACR conserva el administrador deshabilitado.
- Container App pública: `gameproj-dev-app`, `Running`, una revisión saludable y una réplica lista. URL: `https://gameproj-dev-app.salmonmoss-7dc2e6dd.northcentralus.azurecontainerapps.io`.
- Pruebas remotas iniciales: live 200, ready 200, SPA 200, asset React 200, configuración pública 200, documentación 200 y OpenAPI 200. REST y SignalR anónimos responden 401; HTTP redirige a HTTPS con 301.
- GitHub Actions: preparado localmente, todavía no publicado ni configurado remotamente.

### Resultado de what-if de Container App

| Tipo de cambio | Cantidad | Detalle |
|---|---:|---|
| `Create` | 1 | `Microsoft.App/containerApps/gameproj-dev-app` |
| `Ignore` | 12 | Recursos ya existentes del grupo |
| `Modify` | 0 | Ninguno |
| `Delete` | 0 | Ninguno |
| Reemplazos | 0 | Ninguno |

La validación ARM y el `what-if` `FullResourcePayloads` terminaron en `Succeeded`. El plan usa únicamente el ambiente dev, `northcentralus`, la imagen inmutable aprobada, una réplica y las referencias existentes. No vuelve a declarar PostgreSQL, ACR, Key Vault, red, job o recursos de producción como desplegables.

## 8. Mantenimiento, apagado y eliminación

Pausa temporal, conservando datos y cargos de almacenamiento/ACR/DNS:

```powershell
az containerapp update --resource-group rg-gameproj-dev --name gameproj-dev-app --min-replicas 0 --max-replicas 1
az postgres flexible-server stop --resource-group rg-gameproj-dev --name '<NOMBRE_POSTGRES_OUTPUT>'
```

Restaurar para la revisión:

```powershell
az postgres flexible-server start --resource-group rg-gameproj-dev --name '<NOMBRE_POSTGRES_OUTPUT>'
az containerapp update --resource-group rg-gameproj-dev --name gameproj-dev-app --min-replicas 1 --max-replicas 1
```

Eliminar todos los cargos después de exportar las evidencias y aceptar la pérdida de la base:

```powershell
az group delete --name rg-gameproj-dev --yes --no-wait
```

La eliminación es destructiva y requerirá otra aprobación explícita.

## 9. Rollback

Las migraciones son forward-only. Para volver a una imagen anterior, seleccionar un tag/digest ya publicado que sea compatible con el esquema actual y reaplicar Bicep con ese `imageTag`. Después se verifican readiness, login, SignalR y una partida. No se revierte ni borra el esquema automáticamente. Si el binario anterior no es compatible con las migraciones aplicadas, se publica una corrección hacia adelante.
