targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string

@allowed(['dev', 'prod'])
param environmentName string

param location string

param imageRepository string = 'game-server'

@minLength(71)
@maxLength(71)
param imageDigest string

@minValue(60)
@maxValue(3600)
param replicaTimeoutSeconds int = 900

var suffix = '${toLower(namePrefix)}-${environmentName}'
var compact = take(replace('${toLower(namePrefix)}${environmentName}${uniqueString(resourceGroup().id)}', '-', ''), 24)
var registryName = take('${compact}gameacr', 50)
var vaultName = take('${compact}kv', 24)
var tags = { project: 'proyecto-server', environment: environmentName, managedBy: 'bicep' }

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: '${suffix}-cae'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${suffix}-identity'
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource postgresConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: 'postgres-connection'
}

resource migrationJob 'Microsoft.App/jobs@2025-07-01' = {
  name: '${suffix}-migrate'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: replicaTimeoutSeconds
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [{
        server: registry.properties.loginServer
        identity: identity.id
      }]
      secrets: [{
        name: 'postgres-connection'
        keyVaultUrl: postgresConnectionSecret.properties.secretUriWithVersion
        identity: identity.id
      }]
    }
    template: {
      containers: [{
        name: 'migrate'
        image: '${registry.properties.loginServer}/${imageRepository}@${imageDigest}'
        command: ['dotnet']
        args: ['/app/database/Game.Database.dll', 'migrate']
        env: [{
          name: 'ConnectionStrings__GameDatabase'
          secretRef: 'postgres-connection'
        }]
        resources: {
          cpu: json('0.25')
          memory: '0.5Gi'
        }
      }]
    }
  }
}

output migrationJobName string = migrationJob.name
output triggerType string = 'Manual'
output imageReference string = '${registry.properties.loginServer}/${imageRepository}@${imageDigest}'
output managedIdentityId string = identity.id
output observability string = 'Container Apps Environment Log Analytics configuration'
