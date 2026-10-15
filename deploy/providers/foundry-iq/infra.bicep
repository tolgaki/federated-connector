// Provider-specific infrastructure for Foundry IQ (design §9 step 2). Deployed into the search service's resource group.
// Grants the connector's managed identity read access to index data. Document-level trimming still applies because
// every user-scoped query also carries the user's token (x-ms-query-source-authorization).
targetScope = 'resourceGroup'

@description('Azure AI Search service name.')
param searchServiceName string

@description('Principal (object) id of the connector managed identity.')
param identityPrincipalId string

@description('Role used to read knowledge base, knowledge source and index definitions. Reader is read-only; Search Service Contributor also allows writes and is only for services where Reader is not enough. None requires Providers:FoundryIq:KnowledgeSources (with KeyField) to be configured explicitly.')
@allowed([ 'Reader', 'SearchServiceContributor', 'None' ])
param definitionReadRole string = 'Reader'

var definitionRoleIds = {
  Reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  SearchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
}

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: searchServiceName
}

// Search Index Data Reader: read index content. Document-level trimming still applies via the user token.
resource indexDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, identityPrincipalId, '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  scope: search
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  }
}

// Definitions (knowledge bases, knowledge sources, index schema). The connector never writes (conformance-tested).
resource definitionReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (definitionReadRole != 'None') {
  name: guid(search.id, identityPrincipalId, definitionReadRole)
  scope: search
  properties: {
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', definitionReadRole == 'None' ? definitionRoleIds.Reader : definitionRoleIds[definitionReadRole])
  }
}

output searchEndpoint string = 'https://${search.name}.search.windows.net'
output searchServiceId string = search.id
