// Core infrastructure for one connector deployment (design §9 step 1):
// user-assigned managed identity, Key Vault holding the handle-signing key, Log Analytics + Application Insights,
// a container registry, and a Container Apps environment + app running the host.
targetScope = 'resourceGroup'

@description('Short name used to derive resource names, e.g. "contoso-kb-dev".')
@minLength(3)
@maxLength(20)
param baseName string

param location string = resourceGroup().location

@description('Container image for the host. Leave empty on the first deployment; the script builds and sets it in step 4.')
param containerImage string = ''

@secure()
@description('Base64 HMAC key (32+ bytes) for result handles. Only written when provided.')
param handleSigningKey string = ''

@description('Connector display name shown in tool titles.')
param displayName string

@description('Compiled-in provider name, e.g. FoundryIq.')
param providerName string

@description('Random salt kept in the script state. A new salt after --destroy avoids colliding with the soft-deleted, purge-protected Key Vault name.')
param nameSalt string = ''

@description('Additional container environment variables ({ name, value }): provider settings and Entra configuration. Applied in the same deployment as the image so a revision never starts without them.')
param extraEnv array = []

param tags object = {}

var suffix = uniqueString(resourceGroup().id, baseName, nameSalt)
var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${baseName}'
  location: location
  tags: tags
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${baseName}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${baseName}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv${replace(baseName, '-', '')}${suffix}', 24)
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
  }
}

resource signingKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(handleSigningKey)) {
  parent: vault
  name: 'handle-signing-key'
  properties: {
    value: handleSigningKey
    contentType: 'base64 HMAC-SHA256 key'
  }
}

// Key Vault Secrets User
resource vaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: vault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: take('cr${replace(baseName, '-', '')}${suffix}', 50)
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// AcrPull
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${baseName}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${baseName}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [ vaultSecretsUser, acrPull ]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        // The placeholder image listens on 80; the host listens on 8080.
        targetPort: empty(containerImage) ? 80 : 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: empty(containerImage) ? [] : [
        { server: registry.properties.loginServer, identity: identity.id }
      ]
      secrets: [
        {
          name: 'handle-signing-key'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/handle-signing-key'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'host'
          image: empty(containerImage) ? placeholderImage : containerImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'Connector__DisplayName', value: displayName }
            { name: 'Connector__Provider', value: providerName }
            { name: 'Connector__HandleSigningKey', secretRef: 'handle-signing-key' }
            { name: 'Connector__ManagedIdentityClientId', value: identity.properties.clientId }
            { name: 'Connector__KeyVaultUri', value: vault.properties.vaultUri }
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          ], extraEnv)
          probes: empty(containerImage) ? [] : [
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 } }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 5 }
    }
  }
}

output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output identityResourceId string = identity.id
output keyVaultName string = vault.name
output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
output containerAppName string = app.name
output containerAppFqdn string = app.properties.configuration.ingress.fqdn
