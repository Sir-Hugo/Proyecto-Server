[CmdletBinding()]
param(
    [string] $SubscriptionId,
    [string] $Location = 'eastus2'
)

$ErrorActionPreference = 'Stop'

function Write-Check([string] $Title) {
    Write-Host "`n== $Title ==" -ForegroundColor Cyan
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error -ErrorAction Continue @'
Azure CLI no está instalado. En Windows ejecute:
  winget install --exact --id Microsoft.AzureCLI
Cierre y abra PowerShell, y compruebe:
  az version
'@
    exit 2
}

Write-Check 'Azure CLI'
az version --output table

try {
    $accounts = @(az account list --query "[?state=='Enabled'].{Name:name,IsDefault:isDefault}" --output json | ConvertFrom-Json)
    $current = az account show --output json 2>$null | ConvertFrom-Json
}
catch {
    Write-Error -ErrorAction Continue "No hay una sesión válida. Ejecute 'az login' manualmente y vuelva a ejecutar este script."
    exit 3
}

if ($accounts.Count -eq 0 -or $null -eq $current) {
    Write-Error -ErrorAction Continue "No hay una sesión o suscripción habilitada. Ejecute 'az login' manualmente."
    exit 3
}

Write-Check 'Suscripciones habilitadas (identificadores omitidos)'
$accounts | Format-Table -AutoSize

if ($SubscriptionId) {
    $target = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
    if ($target.state -ne 'Enabled') {
        Write-Error -ErrorAction Continue 'La suscripción indicada no está habilitada.'
        exit 4
    }
}
elseif ($accounts.Count -gt 1) {
    Write-Error -ErrorAction Continue 'Hay varias suscripciones habilitadas. No se seleccionó ninguna. Vuelva a ejecutar con -SubscriptionId después de elegirla.'
    exit 4
}
else {
    $target = $current
}

Write-Check 'Selección efectiva'
[pscustomobject]@{
    Subscription = $target.name
    State        = $target.state
    Location     = $Location
} | Format-Table -AutoSize

$subscriptionArg = @('--subscription', $target.id)
$providers = @(
    'Microsoft.App',
    'Microsoft.ContainerRegistry',
    'Microsoft.DBforPostgreSQL',
    'Microsoft.KeyVault',
    'Microsoft.Network',
    'Microsoft.OperationalInsights',
    'Microsoft.Insights',
    'Microsoft.ManagedIdentity',
    'Microsoft.Authorization'
)

Write-Check 'Estado de proveedores (solo lectura)'
$providerResults = @(
foreach ($provider in $providers) {
    $state = az provider show --namespace $provider @subscriptionArg --query registrationState --output tsv
    [pscustomobject]@{ Provider = $provider; State = $state }
}
)
$providerResults | Format-Table -AutoSize

Write-Check 'Disponibilidad regional declarada por los proveedores'
$resourceTypes = @(
    @{ Provider = 'Microsoft.App'; Type = 'managedEnvironments' },
    @{ Provider = 'Microsoft.App'; Type = 'containerApps' },
    @{ Provider = 'Microsoft.App'; Type = 'jobs' },
    @{ Provider = 'Microsoft.ContainerRegistry'; Type = 'registries' },
    @{ Provider = 'Microsoft.DBforPostgreSQL'; Type = 'flexibleServers' },
    @{ Provider = 'Microsoft.KeyVault'; Type = 'vaults' },
    @{ Provider = 'Microsoft.OperationalInsights'; Type = 'workspaces' },
    @{ Provider = 'Microsoft.Insights'; Type = 'components' }
)
$availabilityResults = @(
foreach ($item in $resourceTypes) {
    $locations = @(az provider show --namespace $item.Provider @subscriptionArg --query "resourceTypes[?resourceType=='$($item.Type)'].locations[]" --output tsv)
    [pscustomobject]@{
        Resource = "$($item.Provider)/$($item.Type)"
        Available = [bool]($locations | Where-Object { ($_ -replace '\s', '').ToLowerInvariant() -eq ($Location -replace '\s', '').ToLowerInvariant() })
    }
}
)
$availabilityResults | Format-Table -AutoSize

Write-Check 'SKU PostgreSQL Flexible Server'
try {
    az postgres flexible-server list-skus --location $Location @subscriptionArg `
        --query "[?name=='Standard_B1ms'].{Name:name,Tier:tier,Zones:supportedZones}" --output table
}
catch {
    Write-Warning "No se pudo consultar Standard_B1ms: $($_.Exception.Message)"
}

Write-Check 'Roles del usuario actual (nombres; ámbitos e identificadores omitidos)'
try {
    $principalId = az ad signed-in-user show --query id --output tsv
    $roleNames = az role assignment list --assignee $principalId --include-inherited --all @subscriptionArg `
        --query "[].roleDefinitionName" --output tsv
    $roleNames | Sort-Object -Unique | Format-Table
}
catch {
    Write-Warning 'No se pudieron enumerar roles del usuario. Puede faltar acceso al directorio o la sesión puede ser de una entidad de servicio.'
}

Write-Check 'Cuotas de Container Apps'
try {
    $usageUrl = "https://management.azure.com/subscriptions/$($target.id)/providers/Microsoft.App/locations/$Location/usages?api-version=2024-03-01"
    az rest --method get --url $usageUrl --query "value[].{Name:name.localizedValue,Current:currentValue,Limit:limit}" --output table
}
catch {
    Write-Warning "No se pudieron consultar las cuotas de Container Apps: $($_.Exception.Message)"
}

Write-Host "`nPreflight de solo lectura terminado. Este script no registra proveedores ni crea o modifica recursos." -ForegroundColor Green
