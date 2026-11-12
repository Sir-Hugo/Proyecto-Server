targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string

@allowed(['dev', 'prod'])
param environmentName string

@allowed(['northcentralus'])
param location string = 'northcentralus'

param imageRepository string = 'game-server'

@minLength(71)
@maxLength(71)
param imageDigest string = 'sha256:ffa4975c40a0a5a6cf34e5d801f9ff711e1ed753b4d093632dd7dc02aabebe12'

@minLength(1)
param auth0Authority string

@minLength(1)
param auth0Audience string

@minLength(1)
param auth0ClientId string

var suffix = '${toLower(namePrefix)}-${environmentName}'
var compact = take(replace('${toLower(namePrefix)}${environmentName}${uniqueString(resourceGroup().id)}', '-', ''), 24)
var registryName = take('${compact}gameacr', 50)
var vaultName = take('${compact}kv', 24)
var appName = '${suffix}-app'
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

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: '${suffix}-appi'
}

var publicOrigin = 'https://${appName}.${containerEnvironment.properties.defaultDomain}'

resource app 'Microsoft.App/containerApps@2025-07-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
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
        name: 'server'
        image: '${registry.properties.loginServer}/${imageRepository}@${imageDigest}'
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'Authentication__Authority', value: auth0Authority }
          { name: 'Authentication__Audience', value: auth0Audience }
          { name: 'Auth0__ClientId', value: auth0ClientId }
          { name: 'Cors__AllowedOrigins__0', value: publicOrigin }
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          { name: 'ConnectionStrings__GameDatabase', secretRef: 'postgres-connection' }
        ]
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        probes: [
          {
            type: 'Startup'
            httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
            initialDelaySeconds: 2
            periodSeconds: 3
            timeoutSeconds: 2
            failureThreshold: 30
          }
          {
            type: 'Liveness'
            httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
            initialDelaySeconds: 10
            periodSeconds: 10
            timeoutSeconds: 2
            failureThreshold: 3
          }
          {
            type: 'Readiness'
            httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }
            initialDelaySeconds: 5
            periodSeconds: 5
            timeoutSeconds: 3
            failureThreshold: 6
          }
        ]
      }]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output containerAppName string = app.name
output imageReference string = '${registry.properties.loginServer}/${imageRepository}@${imageDigest}'
output publicUrl string = publicOrigin
output managedIdentityId string = identity.id
