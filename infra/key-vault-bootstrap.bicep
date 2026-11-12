targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string

@allowed(['dev', 'prod'])
param environmentName string

param location string

var suffix = '${toLower(namePrefix)}-${environmentName}'
var compact = take(replace('${toLower(namePrefix)}${environmentName}${uniqueString(resourceGroup().id)}', '-', ''), 24)
var vaultName = take('${compact}kv', 24)
var tags = { project: 'proyecto-server', environment: environmentName, managedBy: 'bicep' }

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${suffix}-identity'
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

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, 'secrets-user')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
output roleAssignmentId string = vaultReader.id
