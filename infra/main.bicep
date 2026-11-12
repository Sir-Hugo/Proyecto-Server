targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string
@allowed(['dev', 'prod'])
param environmentName string
param location string
param imageRepository string = 'game-server'
param imageTag string = 'bootstrap'
param auth0Authority string
param auth0Audience string
param auth0ClientId string
param containerCpu string = '0.5'
param containerMemory string = '1Gi'
@allowed([1])
param minReplicas int = 1
@allowed([1])
param maxReplicas int = 1
param logRetentionDays int = 30
param postgresSkuName string = 'Standard_B1ms'
param postgresSkuTier string = 'Burstable'
param postgresStorageGb int = 32
param postgresAdministratorLogin string = 'gameadmin'
@secure()
param postgresAdministratorPassword string = ''
param vnetAddressPrefix string = '10.20.0.0/16'
param containerAppsSubnetPrefix string = '10.20.0.0/27'
param postgresSubnetPrefix string = '10.20.1.0/28'
@minValue(1)
param monthlyBudgetAmount int = 20
param budgetContactEmail string
param budgetStartDate string = '2026-11-01T00:00:00Z'
param budgetEndDate string = '2027-11-01T00:00:00Z'
param deployBudget bool = true
param deployRoleAssignments bool = true
param deployData bool = false
param deployMigrationJob bool = false
param deployApplication bool = false

var suffix = '${toLower(namePrefix)}-${environmentName}'
var compact = take(replace('${toLower(namePrefix)}${environmentName}${uniqueString(resourceGroup().id)}', '-', ''), 24)
var registryName = take('${compact}gameacr', 50)
var vaultName = take('${compact}kv', 24)
var postgresName = take('${suffix}-pg-${uniqueString(resourceGroup().id)}', 63)
var appName = '${suffix}-app'
var containerAppsSubnetName = 'container-apps'
var postgresSubnetName = 'postgresql'
var privateDnsZoneName = '${suffix}.postgres.database.azure.com'
var tags = { project: 'proyecto-server', environment: environmentName, managedBy: 'bicep' }

resource budget 'Microsoft.Consumption/budgets@2024-08-01' = if (deployBudget) {
  name: '${suffix}-monthly-budget'
  properties: {
    amount: monthlyBudgetAmount
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: budgetEndDate
    }
    notifications: {
      Actual_GreaterThanOrEqualTo_50_Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        contactGroups: []
        contactRoles: ['Owner']
        locale: 'es-es'
      }
      Actual_GreaterThanOrEqualTo_75_Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 75
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        contactGroups: []
        contactRoles: ['Owner']
        locale: 'es-es'
      }
      Actual_GreaterThanOrEqualTo_90_Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 90
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        contactGroups: []
        contactRoles: ['Owner']
        locale: 'es-es'
      }
      Actual_GreaterThanOrEqualTo_100_Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        contactGroups: []
        contactRoles: ['Owner']
        locale: 'es-es'
      }
    }
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${suffix}-vnet'
  location: location
  tags: tags
  properties: { addressSpace: { addressPrefixes: [vnetAddressPrefix] } }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: network
  name: containerAppsSubnetName
  properties: {
    addressPrefix: containerAppsSubnetPrefix
    delegations: [{ name: 'container-apps-delegation', properties: { serviceName: 'Microsoft.App/environments' } }]
  }
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: network
  name: postgresSubnetName
  properties: {
    addressPrefix: postgresSubnetPrefix
    delegations: [{ name: 'postgresql-delegation', properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' } }]
  }
}

resource postgresPrivateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: privateDnsZoneName
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresPrivateDns
  name: '${suffix}-vnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: network.id }
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${suffix}-logs'
  location: location
  tags: tags
  properties: { retentionInDays: logRetentionDays, features: { enableLogAccessUsingOnlyResourcePermissions: true } }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${suffix}-appi'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, publicNetworkAccess: 'Enabled', zoneRedundancy: 'Disabled' }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${suffix}-identity'
  location: location
  tags: tags
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: union({
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }, environmentName == 'prod' ? { enablePurgeProtection: true } : {})
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(registry.id, identity.id, 'acrpull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(vault.id, identity.id, 'secrets-user')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = if (deployData) {
  name: postgresName
  location: location
  tags: tags
  sku: { name: postgresSkuName, tier: postgresSkuTier }
  properties: {
    administratorLogin: postgresAdministratorLogin
    administratorLoginPassword: postgresAdministratorPassword
    version: '17'
    authConfig: { activeDirectoryAuth: 'Disabled', passwordAuth: 'Enabled' }
    backup: { backupRetentionDays: environmentName == 'prod' ? 14 : 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresPrivateDns.id
      publicNetworkAccess: 'Disabled'
    }
    storage: { autoGrow: 'Enabled', storageSizeGB: postgresStorageGb }
  }
  dependsOn: [postgresPrivateDnsLink]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = if (deployData) {
  parent: postgres
  name: 'game'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource postgresConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployData) {
  parent: vault
  name: 'postgres-connection'
  properties: {
    value: 'Host=${postgresName}.postgres.database.azure.com;Port=5432;Database=game;Username=${postgresAdministratorLogin};Password=${postgresAdministratorPassword};SSL Mode=VerifyFull;Include Error Detail=false'
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: '${suffix}-cae'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
    vnetConfiguration: {
      infrastructureSubnetId: containerAppsSubnet.id
      internal: false
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

var commonEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'Authentication__Authority', value: auth0Authority }
  { name: 'Authentication__Audience', value: auth0Audience }
  { name: 'Auth0__ClientId', value: auth0ClientId }
  { name: 'Cors__AllowedOrigins__0', value: 'https://${appName}.${containerEnvironment.properties.defaultDomain}' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
  { name: 'ConnectionStrings__GameDatabase', secretRef: 'postgres-connection' }
]

resource app 'Microsoft.App/containerApps@2025-07-01' = if (deployApplication) {
  name: appName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: 8080, transport: 'auto' }
      registries: [{ server: registry.properties.loginServer, identity: identity.id }]
      secrets: [{ name: 'postgres-connection', keyVaultUrl: '${vault.properties.vaultUri}secrets/postgres-connection', identity: identity.id }]
    }
    template: {
      containers: [{
        name: 'server'
        image: '${registry.properties.loginServer}/${imageRepository}:${imageTag}'
        env: commonEnv
        resources: { cpu: json(containerCpu), memory: containerMemory }
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }, initialDelaySeconds: 2, periodSeconds: 3, timeoutSeconds: 2, failureThreshold: 30 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }, initialDelaySeconds: 10, periodSeconds: 10, timeoutSeconds: 2, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }, initialDelaySeconds: 5, periodSeconds: 5, timeoutSeconds: 3, failureThreshold: 6 }
        ]
      }]
      scale: { minReplicas: minReplicas, maxReplicas: maxReplicas }
    }
  }
}

resource migrationJob 'Microsoft.App/jobs@2025-07-01' = if (deployMigrationJob || deployApplication) {
  name: '${suffix}-migrate'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registry.properties.loginServer, identity: identity.id }]
      secrets: [{ name: 'postgres-connection', keyVaultUrl: '${vault.properties.vaultUri}secrets/postgres-connection', identity: identity.id }]
    }
    template: { containers: [{ name: 'migrate', image: '${registry.properties.loginServer}/${imageRepository}:${imageTag}', command: ['dotnet'], args: ['/app/database/Game.Database.dll', 'migrate'], env: [{ name: 'ConnectionStrings__GameDatabase', secretRef: 'postgres-connection' }], resources: { cpu: json('0.25'), memory: '0.5Gi' } }] }
  }
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output keyVaultName string = vault.name
output postgresHost string = '${postgresName}.postgres.database.azure.com'
output containerAppName string = appName
output migrationJobName string = migrationJob.name
output virtualNetworkName string = network.name
output postgresNetworkMode string = 'Private access (VNet integration)'
output monthlyBudgetName string = budget.name
output publicUrl string = deployApplication ? 'https://${appName}.${containerEnvironment.properties.defaultDomain}' : ''
