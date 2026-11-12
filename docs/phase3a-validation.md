# Validación de la Fase 3A

Fecha: 10 de noviembre de 2026. Alcance: preparación local para Docker, Azure Bicep y CI/CD. No se inició sesión en Azure, no se creó ni modificó una suscripción, y no se hizo commit ni push.

> Actualización de Fase 3B.1: la red privada, los permisos mínimos y la secuencia vigente sustituyen los comandos de ejemplo de las secciones 9–11. Consulte `docs/phase3b-preflight.md` antes de ejecutar cualquier despliegue.

## 1. Archivos creados y modificados

Archivos creados en 3A:

- `Dockerfile` y `.dockerignore`.
- `.github/workflows/ci.yml` y `.github/workflows/cd.yml`.
- `infra/main.bicep`, `infra/dev.bicepparam` e `infra/prod.bicepparam`.
- `src/Game.Server/Health/PostgresReadinessHealthCheck.cs`.
- `src/Game.Server/auth/BearerSecuritySchemeTransformer.cs`.
- `tests/Game.Postgres.Tests/Phase3AHostTests.cs`.
- `redocly.yaml`.
- `docs/azure-costs.md` y este informe.

Archivos modificados en 3A:

- `src/Game.Server/Program.cs`: configuración obligatoria de producción, forwarded headers, HTTPS, estáticos/SPA, configuración pública, health checks y OpenAPI Bearer.
- `src/Game.Infrastructure/PostgresServices.cs`: conserva errores no sensibles y desactiva GSSAPI, no utilizado por la autenticación por contraseña elegida, para que Npgsql 10 no intente cargar Kerberos en la imagen chiseled. TLS continúa configurándose en la cadena Azure.
- `web-client/src/main.tsx` y `web-client/src/App.tsx`: configuración Auth0 pública en tiempo de ejecución y mismo origen por defecto.
- `compose.yaml`: perfil opcional `app`, imagen local, variables obligatorias y restricciones de contenedor.
- `docs/asyncapi.yaml`: referencia de seguridad válida para AsyncAPI 3.0.
- `.gitignore`, `README.md` y `ROADMAP.md`.

Se conservaron los cambios de 1, 2A, 2B y 2C que ya estaban en el árbol de trabajo. No se alteraron las reglas de `Game.Domain` durante 3A.

## 2. Arquitectura Azure propuesta

Una sola Azure Container App aloja ASP.NET Core, SignalR, REST, Scalar/OpenAPI y los archivos compilados de React. El dominio único evita otro servicio de hosting y simplifica CORS, callback y logout. Container Apps termina TLS y reenvía el esquema; la aplicación acepta forwarded headers y redirige HTTP a HTTPS en Production.

El ambiente inicial mantiene `minReplicas = 1` y `maxReplicas = 1`. Esta restricción es funcional: la cola rápida es por proceso y no existe un backplane SignalR ni coordinación distribuida de workers. Outbox, expiración y conexiones funcionan con una instancia. Escalar horizontalmente requiere diseño y pruebas posteriores.

PostgreSQL Flexible Server 17 conserva partidas y outbox. ACR almacena imágenes inmutables etiquetadas con el SHA del commit. La Container App y el job manual usan una identidad administrada asignada por el usuario para `AcrPull` y para leer un secreto de Key Vault. Log Analytics recibe los logs del Container Apps Environment y Application Insights queda creado y conectado al workspace para la instrumentación del ambiente.

## 3. Dockerfile y configuración de producción

El build tiene tres etapas:

1. `node:24-alpine` ejecuta `npm ci` y `npm run build`.
2. `mcr.microsoft.com/dotnet/sdk:10.0` restaura y publica `Game.Server` y `Game.Database` en Release.
3. `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` recibe solo los binarios publicados, la herramienta de migración y `wwwroot`.

La imagen final expone 8080, usa UID 1654, no tiene shell ni package manager, y no ejecuta migraciones en el entrypoint. `.dockerignore` excluye Git, outputs, dependencias Node, resultados, temporales, certificados, claves y archivos `.env`.

En Production, el host se niega a iniciar sin Authority, Audience y client ID públicos de Auth0. La conexión PostgreSQL y al menos un origen CORS siguen siendo obligatorios. `/config/client.json` solo devuelve datos públicos; la contraseña y la cadena de conexión nunca pasan al bundle. Las rutas HTML desconocidas reciben `index.html`; una ruta API o una solicitud que no acepta HTML conserva 404.

`/health/live` no consulta dependencias. `/health/ready` ejecuta `SELECT 1` con el pool configurado y devuelve 503 sin revelar la excepción cuando PostgreSQL no responde.

## 4. Recursos Bicep y dependencias

`infra/main.bicep` declara:

- Log Analytics y Application Insights basado en ese workspace.
- ACR Basic con usuario administrador deshabilitado.
- identidad administrada asignada por el usuario.
- Key Vault Standard con RBAC, soft delete y purge protection en prod.
- asignaciones `AcrPull` y `Key Vault Secrets User` para la identidad.
- PostgreSQL Flexible Server 17, base `game` y regla de servicios Azure, condicionados por `deployData`.
- Container Apps Environment.
- job manual de migración, condicionado por `deployMigrationJob || deployApplication`.
- Container App, condicionada por `deployApplication`, con startup/liveness/readiness probes.

La separación `deployData`, `deployMigrationJob` y `deployApplication` permite crear datos, publicar una imagen, crear y ejecutar el job, y solo después crear la aplicación. Todos los recursos usan parámetros de región, nombres, ambiente, imagen, cómputo, PostgreSQL, almacenamiento, logs y Auth0. Los archivos `.bicepparam` contienen marcadores públicos y dejan las tres operaciones deshabilitadas; no incluyen contraseñas.

## 5. Identidad y secretos

La identidad administrada autentica pulls desde ACR y referencias de secretos de Key Vault. La Container App no recibe credenciales de ACR. La cadena PostgreSQL se monta como `ConnectionStrings__GameDatabase` desde la referencia `postgres-connection`; Bicep y los workflows no contienen su valor.

Se evaluó Entra para PostgreSQL. La aplicación actual construye un `NpgsqlDataSource` con una cadena estable y no implementa renovación de tokens ni aprovisionamiento de administradores/roles Entra. Para no afirmar una integración incompleta, 3A mantiene autenticación PostgreSQL por contraseña, TLS `VerifyFull` en la cadena de 3B y el secreto en Key Vault. El paso futuro correcto es probar un `UsePeriodicPasswordProvider` con token de identidad administrada y roles Entra antes de desactivar password auth.

La plantilla usa por ahora una sola credencial para runtime y migración. Separar `game_app` y `game_migrator` requiere crear roles y privilegios SQL versionados; queda explícitamente pendiente. La regla `0.0.0.0` de PostgreSQL permite servicios Azure y no equivale a aislamiento privado. Private endpoints/VNet se omitieron para esta entrega por costo y complejidad; deben reevaluarse antes de un ambiente sensible.

## 6. Workflows CI/CD

CI se ejecuta en push y pull request con permisos globales `contents: read`. Realiza restore, build Release y las pruebas con `GAME_POSTGRES_TESTS=1`, por lo que Testcontainers no puede omitir PostgreSQL silenciosamente. También ejecuta `npm ci`, ESLint, build Vite, Bicep, actionlint, auditoría NuGet/npm, generación y lint de OpenAPI, validación AsyncAPI, gitleaks y CodeQL para C# y JavaScript/TypeScript.

Gitleaks puede requerir licencia para repositorios privados de una organización. CodeQL y la carga de `security-events` requieren que GitHub Code Security esté disponible para el tipo de repositorio. Si esas funciones no están disponibles, el job fallará de forma visible y se deberá habilitar la función o sustituirla por una CLI equivalente; no se convierte en éxito silencioso.

CD usa `azure/login@v3` con OIDC, permisos `contents: read` e `id-token: write`, imágenes `${{ github.sha }}`, Bicep, job manual de migración, despliegue y readiness. El job completo queda omitido hasta que la variable del GitHub Environment `AZURE_DEPLOY_ENABLED` sea `true`. Dev puede habilitar pushes a `main`; prod debe configurarse como GitHub Environment con required reviewers y se selecciona mediante `workflow_dispatch`.

La sintaxis, expresiones y scripts pasaron actionlint local. Esto no sustituye una ejecución alojada en GitHub ni una validación contra una suscripción Azure.

## 7. Validaciones ejecutadas

| Validación | Resultado local |
|---|---|
| `dotnet build ProyectoServer.slnx --configuration Release --no-restore` | Aprobada, 0 errores y 0 advertencias |
| Suite con `GAME_POSTGRES_TESTS=1` | Aprobada, 139/139 |
| `npm run lint` | Aprobada |
| `npm run build` | Aprobada; Vite avisa que el chunk principal supera 500 kB |
| Docker build multi-stage | Aprobada |
| Migraciones 001–003 ejecutadas desde la imagen | Aprobada, versión 3 |
| Imagen + PostgreSQL real | Aprobada: live 200, ready 200, configuración 200, SPA 200, API inexistente 404 |
| Usuario de imagen/contenedor | UID 1654; no-root |
| Bicep `main`, parámetros dev y prod | Aprobada con Bicep CLI 0.48.1, sin diagnósticos |
| GitHub Actions | Aprobada con actionlint 1.7.7 |
| AsyncAPI | Válida con AsyncAPI CLI 6.3.0; solo informa que existe AsyncAPI 3.1 |
| OpenAPI generado | Aprobado con Redocly CLI 2.77.0 y reglas recomendadas ajustadas en `redocly.yaml` |
| Dependencias .NET | Ningún paquete vulnerable reportado por NuGet |
| Dependencias npm | Sin vulnerabilidades high/critical; 10 moderate transitivas de herramientas ESLint/minimatch |
| `git diff --check` | Aprobada |

La imagen final inspeccionada mide aproximadamente 195 MiB sin comprimir (`204635390` bytes; el valor puede variar con el patch de las bases flotantes). La inspección de sus 59 archivos no encontró `.env`, `.pfx`, `.key`, `secrets*.json` ni los valores de prueba conocidos; sí contiene `wwwroot`, `Game.Server.dll` y `database/Game.Database.dll` como se esperaba.

## 8. Pruebas aprobadas y fallidas

Distribución final:

- `Game.Domain.Tests`: 25 aprobadas.
- `Game.Server.Tests`: 27 aprobadas.
- `Game.Postgres.Tests`: 87 aprobadas.
- Total: 139 aprobadas, 0 fallidas, 0 omitidas.

Las cuatro pruebas agregadas cubren liveness independiente de PostgreSQL, readiness 503 sin detalles sensibles, readiness con PostgreSQL migrado, archivos estáticos/fallback SPA, configuración Auth0 pública, 404 de API y rechazo de Production sin configuración requerida.

Durante el trabajo hubo tres fallos iniciales en esas pruebas porque el factory inyectaba la conexión después de la validación del host. Se corrigió el arnés con `UseSetting` y la suite final pasó completa. La primera validación Redocly reveló que OpenAPI no declaraba Bearer y la primera AsyncAPI CLI reveló una referencia de seguridad con sintaxis 2.x; ambos contratos se corrigieron y se volvieron a validar. No quedan fallos de prueba al cierre.

## 9. Limitaciones pendientes para Azure

- No se ejecutó `az deployment group validate` contra Azure porque no hay suscripción ni credenciales autorizadas en esta fase. La compilación local verifica esquema y tipos, pero Azure puede rechazar disponibilidad de región, cuotas, nombres o políticas.
- CD no se ha ejecutado en GitHub y OIDC aún no tiene federated credential real.
- Los valores Auth0 públicos y sus callback/logout URLs de Azure siguen pendientes.
- La aplicación aún no envía telemetría propia a Application Insights; logs de plataforma sí van a Log Analytics. La instrumentación puede añadirse después de medir el costo.
- PostgreSQL usa password en Key Vault y acceso público para servicios Azure; Entra, redes privadas y credenciales separadas siguen pendientes.
- Una sola réplica es obligatoria hasta distribuir SignalR, cola y workers. No se configuró Redis.
- No se midió tiempo de rollback, reconexiones SignalR durante cambio de revisión ni costo real.
- Vite genera un chunk de unos 531 kB minificado. Es una advertencia de rendimiento, no un bloqueo de despliegue.
- npm reporta 10 vulnerabilidades moderadas transitivas del toolchain. La corrección sugerida implica un cambio mayor de ESLint; CI bloquea high/critical y se debe revisar el aviso antes de futuras actualizaciones.

## 10. Costos y supuestos

La configuración propuesta usa Container Apps Consumption con una réplica de 0.5 vCPU/1 GiB, PostgreSQL Burstable `Standard_B1ms` con 32 GiB, ACR Basic, Key Vault Standard y 30 días de logs. No incluye alta disponibilidad ni backup georredundante. Los importes permanecen pendientes de una cotización oficial en la región y oferta Azure for Students de la cuenta.

La tabla, procedimiento de calculadora, recursos que cobran inactivos, comandos de pausa y alertas están en [azure-costs.md](azure-costs.md).

## 11. Instrucciones exactas para la Fase 3B

Estos comandos son para la siguiente fase y **no se ejecutaron en 3A**. Requieren Azure CLI actual, Docker, permisos para crear un resource group y la configuración Auth0 definitiva.

El operador del bootstrap necesita permisos para desplegar recursos y crear asignaciones RBAC, por ejemplo Owner sobre el resource group. CD debe recibir después un rol más limitado según los tipos que administra.

### 11.1 Preparar variables y comprobar la cotización

```powershell
az login
az account set --subscription 'SUBSCRIPTION_ID'

$resourceGroup = 'rg-gameproj-dev'
$location = 'eastus2'
$namePrefix = 'gameproj'
$environmentName = 'dev'
$auth0Authority = 'https://TENANT.us.auth0.com/'
$auth0Audience = 'https://game-api'
$auth0ClientId = 'PUBLIC_SPA_CLIENT_ID'

az group create --name $resourceGroup --location $location
```

Antes de crear el grupo, completar la cotización de `docs/azure-costs.md` y configurar un presupuesto. Para prod, usar otro resource group y `environmentName = 'prod'`.

### 11.2 Crear infraestructura base y PostgreSQL

Capturar la contraseña sin escribirla en el historial y no usar `--debug`:

```powershell
$securePassword = Read-Host 'Contraseña temporal del administrador PostgreSQL' -AsSecureString
$env:POSTGRES_BOOTSTRAP_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password

$deploymentJson = az deployment group create `
  --resource-group $resourceGroup `
  --name 'phase3b-bootstrap' `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    deployData=true deployMigrationJob=false deployApplication=false `
    postgresAdministratorPassword="$env:POSTGRES_BOOTSTRAP_PASSWORD" `
  --output json
if ($LASTEXITCODE -ne 0) { throw 'Falló el bootstrap Bicep' }

$deployment = $deploymentJson | ConvertFrom-Json
$acrName = $deployment.properties.outputs.registryName.value
$vaultName = $deployment.properties.outputs.keyVaultName.value
$postgresHost = $deployment.properties.outputs.postgresHost.value
$containerAppName = $deployment.properties.outputs.containerAppName.value
$migrationJobName = $deployment.properties.outputs.migrationJobName.value
```

### 11.3 Publicar la imagen y crear el secreto

```powershell
$imageTag = (git rev-parse HEAD).Trim()
az acr login --name $acrName
docker build --pull --tag "$acrName.azurecr.io/game-server:$imageTag" .
docker push "$acrName.azurecr.io/game-server:$imageTag"

$vaultId = az keyvault show --resource-group $resourceGroup --name $vaultName --query id --output tsv
$operatorObjectId = az ad signed-in-user show --query id --output tsv
az role assignment create --assignee-object-id $operatorObjectId --assignee-principal-type User `
  --role 'Key Vault Secrets Officer' --scope $vaultId --output none
# RBAC puede tardar unos minutos en propagarse; reintentar solo el siguiente comando si devuelve 403 temporal.
$escapedPassword = $env:POSTGRES_BOOTSTRAP_PASSWORD.Replace('"', '""')
$connection = 'Host=' + $postgresHost + ';Port=5432;Database=game;Username=gameadmin;Password="' + $escapedPassword + '";SSL Mode=VerifyFull;Trust Server Certificate=false;GSS Encryption Mode=Disable'
az keyvault secret set --vault-name $vaultName --name 'postgres-connection' --value $connection --output none
az role assignment delete --assignee-object-id $operatorObjectId --role 'Key Vault Secrets Officer' --scope $vaultId
Remove-Variable connection, escapedPassword, securePassword
Remove-Item Env:POSTGRES_BOOTSTRAP_PASSWORD
```

El valor no se imprime ni se guarda en un archivo. La fase siguiente debe crear roles SQL separados y reemplazar este secreto administrativo por uno de runtime con privilegios mínimos; hasta entonces no habilitar el acceso a terceros.

### 11.4 Crear y ejecutar el job antes de la aplicación

```powershell
az deployment group create `
  --resource-group $resourceGroup `
  --name 'phase3b-migration-job' `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag=$imageTag deployData=false deployMigrationJob=true deployApplication=false
if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el job' }

$execution = az containerapp job start --resource-group $resourceGroup --name $migrationJobName --query name --output tsv
do {
  Start-Sleep -Seconds 10
  $status = az containerapp job execution show --resource-group $resourceGroup --name $migrationJobName `
    --job-execution-name $execution --query properties.status --output tsv
} while ($status -in @('Pending', 'Running', 'Processing'))
if ($status -ne 'Succeeded') { throw "Migración fallida: $status" }
```

El job conserva orden, checksum, transacción y código de salida de `Game.Database`. No iniciar la aplicación si el estado no es `Succeeded`.

### 11.5 Desplegar y verificar

```powershell
$applicationJson = az deployment group create `
  --resource-group $resourceGroup `
  --name 'phase3b-application' `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=$environmentName location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag=$imageTag deployData=false deployMigrationJob=true deployApplication=true `
  --output json
if ($LASTEXITCODE -ne 0) { throw 'Falló el despliegue de la aplicación' }

$publicUrl = (($applicationJson | ConvertFrom-Json).properties.outputs.publicUrl.value)
curl.exe --fail --show-error "$publicUrl/health/live"
curl.exe --fail --show-error "$publicUrl/health/ready"
az containerapp revision list --resource-group $resourceGroup --name $containerAppName --output table
```

Agregar `$publicUrl`, `$publicUrl/` y los orígenes correspondientes a Allowed Callback URLs, Allowed Logout URLs y Allowed Web Origins de la SPA Auth0. Después probar login, REST, WebSocket, una partida, reconexión y outbox con dos cuentas.

### 11.6 Configurar GitHub y habilitar CD

Crear GitHub Environments `dev` y `prod`. En prod, activar required reviewers. Configurar OIDC con una app/identidad Azure y federated credentials limitadas al repositorio y a cada environment. Añadir como variables de cada environment:

`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `NAME_PREFIX`, `ACR_NAME`, `CONTAINER_APP_NAME`, `MIGRATION_JOB_NAME`, `AUTH0_AUTHORITY`, `AUTH0_AUDIENCE`, `AUTH0_CLIENT_ID` y finalmente `AZURE_DEPLOY_ENABLED=true`.

La primera ejecución debe ser manual en dev. Mantener prod deshabilitado hasta que dev, costos, migración y rollback hayan sido comprobados.

### 11.7 Rollback

Elegir el SHA de una imagen anterior que siga en ACR. Confirmar antes que sus consultas sean compatibles con la versión actual del esquema; las migraciones son forward-only y no se debe intentar un downgrade SQL automático. Volver a aplicar Bicep con `imageTag=<SHA_ANTERIOR>`, `deployData=false`, `deployMigrationJob=true` y `deployApplication=true`. Después comprobar `/health/live`, `/health/ready`, una conexión SignalR y una partida.

Container Apps usa una revisión activa y no se configuró división de tráfico. No se afirma un tiempo de recuperación hasta medirlo en 3B. Si el código anterior no es compatible con el esquema actual, corregir hacia adelante con una nueva imagen; no borrar ni revertir datos manualmente.
