targetScope = 'resourceGroup'

@minLength(1)
@maxLength(30)
@description('Short environment label used to create unique Azure resource names.')
param environmentName string = 'opmlsync'

@metadata({
  azd: {
    type: 'location'
  }
})
param location string = resourceGroup().location

@minLength(1)
@description('HTTPS root URL of the deployed WebSub hub.')
param webSubHubUrl string

@description('Comma-separated HTTPS topic hosts allowed in OPML includes.')
param allowedIncludeHosts string = 'lqdev.me,www.lqdev.me,luisquintanilla.me,www.luisquintanilla.me'

@description('Comma-separated OPML root topic URLs to follow.')
param rootTopics string = 'https://lqdev.me/.well-known/following.opml'

@minValue(1)
@maxValue(10)
param maxIncludeDepth int = 3

@minValue(3600)
@maxValue(2592000)
param leaseSeconds int = 2592000

@allowed([
  'true'
  'false'
])
@description('Keep NewsBlur changes disabled until credentials are configured and a dry run is reviewed.')
param syncDryRun string = 'true'

@minValue(1)
@maxValue(100)
param maximumInstanceCount int = 1

@allowed([
  2048
  4096
])
param instanceMemoryMB int = 2048

var safeEnvironment = take(toLower(replace(replace(environmentName, '-', ''), '_', '')), 12)
var suffix = uniqueString(subscription().id, resourceGroup().id, environmentName)
var functionAppName = 'func${safeEnvironment}${suffix}'
var storageAccountName = take('st${safeEnvironment}${suffix}', 24)
var keyVaultName = 'kv${safeEnvironment}${suffix}'
var appInsightsName = 'appi${safeEnvironment}${suffix}'
var workspaceName = 'log${safeEnvironment}${suffix}'
var planName = 'plan-${safeEnvironment}-${suffix}'
var packageContainerName = 'function-packages'
var payloadContainerName = 'opml-payloads'
var applicationInsightsIdentity = 'ClientId=${identity.properties.clientId};Authorization=AAD'
var callbackUrl = 'https://${functionAppName}.azurewebsites.net/callback'
var usernameSecretReference = '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/newsblur-username/)'
var passwordSecretReference = '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}.vault.azure.net/secrets/newsblur-password/)'

var appSettings = [
  {
    name: 'FUNCTIONS_WORKER_RUNTIME'
    value: 'dotnet-isolated'
  }
  {
    name: 'FUNCTIONS_EXTENSION_VERSION'
    value: '~4'
  }
  {
    name: 'AzureWebJobsStorage__credential'
    value: 'managedidentity'
  }
  {
    name: 'AzureWebJobsStorage__clientId'
    value: identity.properties.clientId
  }
  {
    name: 'AzureWebJobsStorage__blobServiceUri'
    value: storage.properties.primaryEndpoints.blob
  }
  {
    name: 'AzureWebJobsStorage__queueServiceUri'
    value: storage.properties.primaryEndpoints.queue
  }
  {
    name: 'AzureWebJobsStorage__tableServiceUri'
    value: storage.properties.primaryEndpoints.table
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
    value: applicationInsightsIdentity
  }
  {
    name: 'SYNC_CALLBACK_URL'
    value: callbackUrl
  }
  {
    name: 'WEBSUB_HUB_URL'
    value: webSubHubUrl
  }
  {
    name: 'WEBSUB_ROOT_TOPICS'
    value: rootTopics
  }
  {
    name: 'WEBSUB_ALLOWED_INCLUDE_HOSTS'
    value: allowedIncludeHosts
  }
  {
    name: 'WEBSUB_MAX_INCLUDE_DEPTH'
    value: string(maxIncludeDepth)
  }
  {
    name: 'WEBSUB_LEASE_SECONDS'
    value: string(leaseSeconds)
  }
  {
    name: 'NEWSBLUR_USERNAME'
    value: usernameSecretReference
  }
  {
    name: 'NEWSBLUR_PASSWORD'
    value: passwordSecretReference
  }
  {
    name: 'SYNC_DRY_RUN'
    value: syncDryRun
  }
]

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${safeEnvironment}-${suffix}'
  location: location
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource packageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: packageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource payloadContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: payloadContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: workspaceName
  location: location
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: false
    publicNetworkAccess: 'Enabled'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource blobDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource queueDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, storageQueueDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource tableDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, storageTableDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource monitoringMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, identity.id, monitoringMetricsPublisherRoleId)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisherRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionApp 'Microsoft.Web/sites@2024-11-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${packageContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: appSettings
    }
  }
  dependsOn: [
    packageContainer
    payloadContainer
    blobDataOwner
    queueDataContributor
    tableDataContributor
    keyVaultSecretsUser
    monitoringMetricsPublisher
  ]
}

output SERVICE_SYNC_NAME string = functionApp.name
output CALLBACK_URL string = callbackUrl
output KEY_VAULT_NAME string = keyVault.name
output STORAGE_ACCOUNT_NAME string = storage.name
output WEBSUB_HUB_URL string = webSubHubUrl
