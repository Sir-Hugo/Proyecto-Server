targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string

@allowed(['dev', 'prod'])
param environmentName string

param location string

param postgresSkuName string = 'Standard_B1ms'

@allowed(['Burstable'])
param postgresSkuTier string = 'Burstable'

@minValue(32)
param postgresStorageGb int = 32

param postgresAdministratorLogin string = 'gameadmin'

@secure()
@minLength(16)
param postgresAdministratorPassword string

var suffix = '${toLower(namePrefix)}-${environmentName}'
var compact = take(replace('${toLower(namePrefix)}${environmentName}${uniqueString(resourceGroup().id)}', '-', ''), 24)
var vaultName = take('${compact}kv', 24)
var postgresName = take('${suffix}-pg-${uniqueString(resourceGroup().id)}', 63)
var privateDnsZoneName = '${suffix}.postgres.database.azure.com'
var tags = { project: 'proyecto-server', environment: environmentName, managedBy: 'bicep' }

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: '${suffix}-vnet'
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: network
  name: 'postgresql'
}

resource postgresPrivateDns 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privateDnsZoneName
}

resource postgresPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' existing = {
  parent: postgresPrivateDns
  name: '${suffix}-vnet-link'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${suffix}-identity'
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
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

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgres
  name: 'game'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource postgresConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-connection'
  properties: {
    value: 'Host=${postgresName}.postgres.database.azure.com;Port=5432;Database=game;Username=${postgresAdministratorLogin};Password=${postgresAdministratorPassword};SSL Mode=VerifyFull;Include Error Detail=false'
  }
  dependsOn: [database]
}

output postgresServerName string = postgres.name
output postgresHost string = '${postgresName}.postgres.database.azure.com'
output databaseName string = database.name
output connectionSecretId string = postgresConnectionSecret.id
output validatedExistingResourceIds array = [
  network.id
  postgresSubnet.id
  postgresPrivateDns.id
  postgresPrivateDnsLink.id
  identity.id
  vault.id
]
output networkMode string = 'Private access (VNet integration)'
output tlsValidationMode string = 'VerifyFull'
