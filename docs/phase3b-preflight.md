# Fase 3B.1 — Preflight de Azure

Fecha de revisión: 10 de noviembre de 2026. Esta subfase no creó ni modificó recursos Azure, GitHub, Auth0, commits o ramas remotas.

## Resultado y bloqueo actual

Azure CLI no está instalado en el equipo. Por eso no se pudo detectar una suscripción, comprobar si Azure for Students está activa, consultar su crédito, permisos, cuotas o la disponibilidad de SKU específica de la cuenta. No se expone ningún identificador de suscripción. GitHub CLI tampoco está instalado.

Instalación requerida en Windows:

```powershell
winget install --exact --id Microsoft.AzureCLI
```

Después hay que cerrar y abrir PowerShell, ejecutar `az version` y autenticar manualmente con `az login`. No se deben compartir contraseñas, códigos ni tokens. Si aparecen varias suscripciones habilitadas, hay que elegir la correcta antes de continuar; no se debe asumir que la denominada Azure for Students mantiene el crédito inicial.

El script `scripts/azure-preflight.ps1` automatiza únicamente consultas de lectura. Omite identificadores en su salida resumida y se detiene si hay varias suscripciones:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\azure-preflight.ps1
# Solo después de decidir entre varias suscripciones:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\azure-preflight.ps1 `
  -SubscriptionId '<ID_ELEGIDO>' -Location eastus2
```

No registra proveedores ni crea recursos. Un proveedor en estado distinto de `Registered` es un paso manual pendiente para 3B.2, porque registrarlo modifica la suscripción.

## Región, servicios y cuotas

La región propuesta continúa siendo `eastus2`. La documentación pública lista PostgreSQL Flexible Server en East US 2 y PostgreSQL 17 como versión admitida. Container Apps, ACR, Key Vault, Log Analytics y Application Insights declaran presencia regional, pero la capacidad, cuotas y oferta `Standard_B1ms` dependen de la suscripción. Deben confirmarse con el script después del inicio de sesión. Si el SKU no está disponible, se debe cotizar otro SKU Burstable disponible o una región compatible; no se cambiará de arquitectura automáticamente.

Referencias oficiales: [regiones de PostgreSQL](https://learn.microsoft.com/azure/postgresql/overview), [versiones admitidas](https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-supported-versions), [cuotas de Container Apps](https://learn.microsoft.com/azure/container-apps/quotas) y [redes de Container Apps](https://learn.microsoft.com/azure/container-apps/networking).

## Recursos y costo

La configuración mínima prevista contiene:

- Container Apps Environment Consumption, una aplicación de 0.5 vCPU/1 GiB y una sola réplica, y un job manual de migración de 0.25 vCPU/0.5 GiB.
- PostgreSQL Flexible Server 17 Burstable `Standard_B1ms`, 32 GiB, sin HA ni backup georredundante.
- ACR Basic, Key Vault Standard, identidad administrada, Log Analytics con 30 días y Application Insights.
- Una VNet, subred `/27` delegada a Container Apps, subred `/28` delegada a PostgreSQL y una zona Private DNS.

PostgreSQL y su almacenamiento son el costo fijo principal probable. También continúan facturando ACR, backups, Private DNS y observabilidad aunque el tráfico sea bajo. Una réplica siempre activa puede superar las concesiones gratuitas de Container Apps. No se asignó un importe universal: antes de crear recursos debe completarse la [calculadora oficial](https://azure.microsoft.com/pricing/calculator/) con la oferta, moneda y crédito reales. El presupuesto recomendado es el total mensual calculado para 730 horas más 20 % de margen, limitado al crédito restante, con avisos al 50 %, 75 %, 90 % y 100 %. El detalle está en `docs/azure-costs.md`.

## Infraestructura revisada y correcciones

La Fase 3A ya contenía Docker multi-stage, health probes, configuración pública Auth0, Bicep, CI/CD OIDC y el job separado de migración. La revisión detectó dos riesgos:

1. PostgreSQL tenía acceso público y una regla `0.0.0.0`, que autoriza conectividad amplia desde Azure.
2. Cada ejecución del CD intentaba administrar asignaciones de roles, ampliando innecesariamente los permisos permanentes de GitHub.

Se corrigió `infra/main.bicep`: PostgreSQL ahora usa acceso privado, DNS privado y subred delegada; Container Apps entra a la misma VNet mediante otra subred; se eliminó la regla amplia. El app y el job acceden a la base desde Azure, por lo que un runner público de GitHub no necesita conectividad SQL. Las migraciones siguen ejecutándose en el job de Container Apps.

También se añadió `deployRoleAssignments`. El bootstrap inicial lo usa en `true` para otorgar `AcrPull` y `Key Vault Secrets User` a la identidad administrada. El workflow cotidiano lo fuerza a `false`; así, después del bootstrap, la identidad federada de GitHub necesita Contributor solo en el grupo de recursos y `AcrPush` en ACR, sin administrar roles. El operador de bootstrap sí necesita Contributor y Role Based Access Control Administrator —o privilegios equivalentes— en el grupo, no en toda la suscripción.

La conexión PostgreSQL se almacena como secreto de Key Vault, se referencia desde Container Apps y debe verificar TLS (`SSL Mode=VerifyFull`). La contraseña no debe entrar al repositorio, variables públicas del workflow ni argumentos registrados. Bicep conserva ACR y Key Vault con endpoints públicos protegidos por RBAC; endurecerlos con endpoints privados puede evaluarse después de validar el primer despliegue y su costo.

## Estado de GitHub y OIDC

El remoto es `https://github.com/Sir-Hugo/Proyecto-Server.git`, la rama local es `avance-11/10` y `HEAD` coincide con `origin/avance-11/10` en `1561c09`. Los cambios de Fase 3B.1 y varios informes locales anteriores todavía no están incorporados al remoto. CD escucha `main` y `workflow_dispatch`; el workflow debe existir en la rama predeterminada para poder operarse normalmente. No se hizo commit ni push.

Al iniciar esta subfase ya estaba modificado `docs/persistence-design.md` y estaban sin seguimiento `docs/azure-costs.md` y los informes de validación de fases 1, 2A, 2B, 2C y 3A. Se preservaron esos cambios; no se restauró ni sobrescribió trabajo previo. Esta subfase añadió el informe 3B.1 y el script, y modificó localmente Bicep, parámetros, CD, README y ROADMAP.

Para cada Environment de GitHub (`dev` y `prod`) se requiere una credencial federada Entra con issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange` y subject, respectivamente:

```text
repo:Sir-Hugo/Proyecto-Server:environment:dev
repo:Sir-Hugo/Proyecto-Server:environment:prod
```

`prod` debe exigir aprobación manual. Variables esperadas por el workflow:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
AZURE_RESOURCE_GROUP
AZURE_LOCATION
NAME_PREFIX
ACR_NAME
CONTAINER_APP_NAME
MIGRATION_JOB_NAME
AUTH0_AUTHORITY
AUTH0_AUDIENCE
AUTH0_CLIENT_ID
AZURE_DEPLOY_ENABLED
```

Los tres valores Auth0 son identificadores públicos, no Client Secrets. `AZURE_DEPLOY_ENABLED` debe permanecer distinto de `true` hasta completar bootstrap, secreto, permisos y revisión. La identidad federada no requiere un secreto de cliente permanente.

## Configuración Auth0 pendiente

En producción el servidor entrega `/config/client.json` con Authority, Audience y Client ID públicos. El cliente usa `window.location.origin` como `redirect_uri` y como `returnTo`; REST y SignalR comparten ese origen. Cuando Bicep entregue `publicUrl`, hay que agregar exactamente esa URL HTTPS, sin una ruta adicional, en la SPA de Auth0:

- Allowed Callback URLs: `https://<FQDN_CONTAINER_APP>`
- Allowed Logout URLs: `https://<FQDN_CONTAINER_APP>`
- Allowed Web Origins: `https://<FQDN_CONTAINER_APP>`

La API mantiene el Audience existente y firma RS256. No se añade bypass ni Client Secret al bundle. Bicep configura el mismo origen exacto en CORS.

## Secuencia preparada para Fase 3B.2

Estos pasos son una receta, no evidencia de ejecución:

1. Manual: seleccionar la suscripción, revisar crédito/cotización, permisos, proveedores, cuotas y SKU con el preflight.
2. Manual autorizado: crear un grupo de recursos en `eastus2` y configurar presupuesto/alertas.
3. Manual autorizado: crear la aplicación Entra y sus dos credenciales federadas; crear los Environments y variables GitHub.
4. Bootstrap mediante Bicep: desplegar recursos compartidos con `deployRoleAssignments=true`, `deployData=false`, `deployApplication=false`.
5. Manual: otorgar Contributor en el grupo y AcrPush en ACR a la identidad GitHub. No otorgar Owner de suscripción.
6. Bicep autorizado: desplegar PostgreSQL privado y la base con `deployRoleAssignments=false`, `deployData=true`, `deployMigrationJob=false`, `deployApplication=false`, pasando la contraseña como parámetro seguro fuera de logs.
7. Manual: guardar en Key Vault `postgres-connection`, con host privado resuelto dentro de la VNet y `SSL Mode=Require`.
8. Construir y publicar una imagen inmutable en ACR.
9. Bicep: crear el job con esa imagen; ejecutarlo una vez y exigir estado `Succeeded`. El job aplica 001–003 de forma idempotente dentro de Azure.
10. Bicep: desplegar la aplicación con una sola réplica y obtener `publicUrl`.
11. Manual: registrar la URL exacta en Auth0; después habilitar `AZURE_DEPLOY_ENABLED=true`.
12. Automático/manual: comprobar `/health/live`, `/health/ready`, REST, autenticación y SignalR; jugar con dos usuarios y cortar/reanudar una sesión para verificar recuperación.
13. Manual: registrar URL, revisiones, ejecución de migración, health checks y evidencias, sin incluir tokens ni cadenas de conexión.

Plantilla de comandos preparada para una ejecución autorizada. Los valores se eligen después del preflight; el secreto se lee de forma interactiva y el parámetro Bicep está marcado `@secure()`:

```powershell
$subscriptionId = '<SUSCRIPCION_ELEGIDA>'
$resourceGroup = '<GRUPO_DEV>'
$location = 'eastus2'
$namePrefix = '<PREFIJO_3_A_12_CARACTERES>'
$auth0Authority = 'https://<TENANT>.auth0.com/'
$auth0Audience = '<AUDIENCE_API>'
$auth0ClientId = '<CLIENT_ID_PUBLICO>'

az account set --subscription $subscriptionId
az group create --name $resourceGroup --location $location

# Bootstrap compartido y asignaciones de la identidad administrada.
az deployment group create --resource-group $resourceGroup --name phase3b-bootstrap `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=dev location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    deployRoleAssignments=true deployData=false deployMigrationJob=false deployApplication=false

# Base privada. No escribir el valor literal en el historial.
$securePassword = Read-Host 'Contraseña administrativa temporal de PostgreSQL' -AsSecureString
$env:POSTGRES_BOOTSTRAP_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password
az deployment group create --resource-group $resourceGroup --name phase3b-data `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=dev location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    postgresAdministratorPassword="$env:POSTGRES_BOOTSTRAP_PASSWORD" `
    deployRoleAssignments=false deployData=true deployMigrationJob=false deployApplication=false

# Después de publicar <SHA> en el ACR y guardar postgres-connection en Key Vault:
az deployment group create --resource-group $resourceGroup --name phase3b-job `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=dev location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag='<SHA>' deployRoleAssignments=false deployData=false deployMigrationJob=true deployApplication=false
az containerapp job start --resource-group $resourceGroup --name "$namePrefix-dev-migrate"

# Solo tras comprobar que la ejecución de migración terminó en Succeeded:
az deployment group create --resource-group $resourceGroup --name phase3b-app `
  --template-file infra/main.bicep `
  --parameters namePrefix=$namePrefix environmentName=dev location=$location `
    auth0Authority=$auth0Authority auth0Audience=$auth0Audience auth0ClientId=$auth0ClientId `
    imageTag='<SHA>' deployRoleAssignments=false deployData=false deployMigrationJob=true deployApplication=true

Remove-Item Env:POSTGRES_BOOTSTRAP_PASSWORD
Remove-Variable securePassword
```

No se ejecutó ninguno de esos comandos. Antes del paso del job hay que obtener los nombres desde los outputs del deployment, publicar la imagen inmutable y crear el secreto mediante una operación que no imprima su valor. El primer CD presupone que el bootstrap ya creó ACR, el job, Key Vault, PostgreSQL y sus permisos.

## Validación local

| Verificación | Resultado |
|---|---|
| `dotnet build ProyectoServer.slnx --configuration Release` | Aprobada, 0 errores y 0 advertencias |
| Suite .NET con `GAME_POSTGRES_TESTS=1` | Aprobadas 139/139: dominio 25, servidor 27, PostgreSQL real 87 |
| ESLint y build Vite | Aprobados; Vite conserva el aviso no bloqueante por un chunk de 531.25 kB |
| Bicep principal y parámetros dev/prod | Aprobados con Bicep CLI local, sin diagnósticos |
| GitHub Actions | Aprobado con actionlint 1.7.7 |
| OpenAPI | Aprobado con Redocly CLI 2.77.0 |
| AsyncAPI | Válido con CLI 6.3.0; solo informa que 3.1 es la versión más reciente |
| Script PowerShell de preflight | Sintaxis aprobada; la consulta real queda bloqueada por falta de Azure CLI/login |
| `git diff --check` | Aprobado; solo avisos de conversión LF/CRLF de Git en archivos existentes |

La primera ejecución de PostgreSQL/actionlint dentro del sandbox no pudo acceder al pipe de Docker. Al repetirla con acceso al Docker local, toda la suite y actionlint pasaron. Esto fue una restricción del entorno de ejecución, no un fallo funcional.

## Pasos manuales pendientes

1. Instalar Azure CLI, abrir una terminal nueva y ejecutar `az login` manualmente.
2. Ejecutar el preflight de solo lectura y elegir explícitamente la suscripción si aparece más de una.
3. Confirmar crédito, presupuesto, `Standard_B1ms`, cuotas y permisos.
4. Autorizar por separado la Fase 3B.2 antes de crear recursos, modificar GitHub/Auth0, hacer commit o push.
