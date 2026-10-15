// Provider-specific infrastructure for Foundry IQ (design §9 step 2). Deployed into the search service's resource group.
// Grants the connector's managed identity read access to index data. Document-level trimming still applies because
// every user-scoped query also carries the user's token (x-ms-query-source-authorization).
targetScope = 'resourceGroup'

@description('Azure AI Search service name.')
param searchServiceName string

@description('Principal (object) id of the connector managed identity.')
param identityPrincipalId string

@description('Also grant Search Service Reader-equivalent read of definitions (knowledge bases, knowledge sources, index schema). Set false and configure Providers:FoundryIq:KnowledgeSources explicitly if not allowed.')
param grantDefinitionRead bool = true

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: searchServiceName
}

// Search Index Data Reader
resource indexDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, identityPrincipalId, '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  scope: search
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  }
}

// Reader (control plane) is not enough for data-plane definitions; Search Service Contributor is the documented role
// for reading knowledge base and index definitions. It also permits writes, so it is opt-in; the connector itself
// never writes (conformance-tested).
resource definitionReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantDefinitionRead) {
  name: guid(search.id, identityPrincipalId, '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  scope: search
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  }
}

output searchEndpoint string = 'https://${search.name}.search.windows.net'
