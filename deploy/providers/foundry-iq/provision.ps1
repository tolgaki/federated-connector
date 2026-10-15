#!/usr/bin/env pwsh
# Foundry IQ provider provisioning (design §9 step 2). Called by register-connector.ps1 with these environment
# variables set: SEARCH_SERVICE, SEARCH_RESOURCE_GROUP, IDENTITY_PRINCIPAL_ID, and optionally
# DEFINITION_READ_ROLE (Reader | SearchServiceContributor | None; default Reader). With DESTROY=true, removes them.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($name in 'SEARCH_SERVICE', 'SEARCH_RESOURCE_GROUP', 'IDENTITY_PRINCIPAL_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required" }
}

if ($env:DESTROY -eq 'true') {
    Write-Host "  foundry-iq: removing the connector identity's role assignments on $($env:SEARCH_SERVICE)"
    $scope = az search service show --name $env:SEARCH_SERVICE --resource-group $env:SEARCH_RESOURCE_GROUP --query id -o tsv --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the search service.' }
    az role assignment delete --assignee $env:IDENTITY_PRINCIPAL_ID --scope $scope --only-show-errors --output none
    if ($LASTEXITCODE -ne 0) { throw 'Role assignment removal failed.' }
    return
}

$role = if ($env:DEFINITION_READ_ROLE) { $env:DEFINITION_READ_ROLE } else { 'Reader' }
Write-Host "  foundry-iq: granting Search Index Data Reader (+ $role for definitions) on $($env:SEARCH_SERVICE)"
az deployment group create `
    --name 'fc-foundry-iq-roles' `
    --resource-group $env:SEARCH_RESOURCE_GROUP `
    --template-file (Join-Path $PSScriptRoot 'infra.bicep') `
    --parameters "searchServiceName=$($env:SEARCH_SERVICE)" "identityPrincipalId=$($env:IDENTITY_PRINCIPAL_ID)" "definitionReadRole=$role" `
    --only-show-errors --output none
if ($LASTEXITCODE -ne 0) { throw 'Foundry IQ role assignment failed.' }
