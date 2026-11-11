using './main.bicep'
param namePrefix = 'gameproj'
param environmentName = 'dev'
param location = 'eastus2'
param auth0Authority = 'https://REPLACE.us.auth0.com/'
param auth0Audience = 'https://REPLACE-api'
param auth0ClientId = 'REPLACE_PUBLIC_CLIENT_ID'
param containerCpu = '0.5'
param containerMemory = '1Gi'
param postgresSkuName = 'Standard_B1ms'
param postgresSkuTier = 'Burstable'
param postgresStorageGb = 32
param logRetentionDays = 30
param minReplicas = 1
param maxReplicas = 1
param deployData = false
param deployMigrationJob = false
param deployApplication = false
