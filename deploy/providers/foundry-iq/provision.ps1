#!/usr/bin/env pwsh
# Foundry IQ provider provisioning (design §9 step 2). Called by register-connector.ps1 with these environment
# variables set: SEARCH_SERVICE, SEARCH_RESOURCE_GROUP, IDENTITY_PRINCIPAL_ID, optionally GRANT_DEFINITION_READ.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($name in 'SEARCH_SERVICE', 'SEARCH_RESOURCE_GROUP', 'IDENTITY_PRINCIPAL_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required" }
}

if ($env:DESTROY -eq 'true') {
    Write-Host "  foundry-iq: role assignments on $($env:SEARCH_SERVICE) are removed with the managed identity; nothing else to undo."
    return
}

$grant = if ($env:GRANT_DEFINITION_READ) { $env:GRANT_DEFINITION_READ } else { 'true' }
Write-Host "  foundry-iq: granting Search Index Data Reader on $($env:SEARCH_SERVICE) to the connector identity"
az deployment group create `
    --name 'fc-foundry-iq-roles' `
    --resource-group $env:SEARCH_RESOURCE_GROUP `
    --template-file (Join-Path $PSScriptRoot 'infra.bicep') `
    --parameters "searchServiceName=$($env:SEARCH_SERVICE)" "identityPrincipalId=$($env:IDENTITY_PRINCIPAL_ID)" "grantDefinitionRead=$grant" `
    --only-show-errors --output none
if ($LASTEXITCODE -ne 0) { throw 'Foundry IQ role assignment failed.' }
