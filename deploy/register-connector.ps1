#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Registers a federated MCP connector with Microsoft 365 Copilot (design §9). PowerShell twin of register-connector.sh.

.EXAMPLE
  ./register-connector.ps1 -EnvName dev -Provider foundry-iq -DisplayName "Contoso Knowledge" -Route package `
      -PublisherName Contoso -PublisherUrl https://contoso.example -PrivacyUrl https://contoso.example/privacy -TermsUrl https://contoso.example/terms `
      -Inputs @{ 'search-service' = 'contoso-search'; 'search-resource-group' = 'rg-search'; 'knowledge-bases' = 'hr-kb,eng-kb' }

.NOTES
  Driven by deploy/providers/<provider>/provider.json. Idempotent: each step records its outputs in deploy/.env.<env>
  and is skipped on re-run when they exist (-Redeploy rebuilds the host image). -Destroy reverses the steps.
  Secrets are never written to the state file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Env')] [ValidatePattern('^[a-z0-9-]+$')] [string] $EnvName,
    [Parameter(Mandatory)] [string] $Provider,
    [string] $DisplayName,
    [ValidateSet('package', 'admin', '')] [string] $Route = '',
    [string] $Location,
    [string] $ResourceGroup,
    [string] $PublisherName,
    [string] $PublisherUrl,
    [string] $PrivacyUrl,
    [string] $TermsUrl,
    [string] $OAuthClientId,
    [hashtable] $Inputs = @{},
    [switch] $Publish,
    [switch] $Redeploy,
    [switch] $Destroy,
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$DeployDir = $PSScriptRoot
$RepoRoot = Split-Path $DeployDir -Parent
$TeamsRedirectUri = 'https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect'

function Log([string] $m) { Write-Host "`n==> $m" -ForegroundColor White }
function Info([string] $m) { Write-Host "    $m" }
function Warn([string] $m) { Write-Warning $m }
function Die([string] $m) { throw "error: $m" }

function Invoke-Native {
    # Runs a native command (first argument), failing on a non-zero exit code. Returns stdout as a string.
    # A simple function on purpose: all arguments, including ones like -o, pass through untouched in $args.
    $command = $args[0]
    $arguments = @($args | Select-Object -Skip 1)
    $out = & $command @arguments
    if ($LASTEXITCODE -ne 0) { Die "$command $($arguments[0..1] -join ' ') failed (exit $LASTEXITCODE)" }
    return ($out -join "`n")
}

# ---------------------------------------------------------------- state (.env.<env>)
$ProviderDir = Join-Path $DeployDir "providers/$Provider"
$ProviderJsonPath = Join-Path $ProviderDir 'provider.json'
if (-not (Test-Path $ProviderJsonPath)) { Die "unknown provider '$Provider' (no $ProviderJsonPath)" }
$ProviderJson = Get-Content $ProviderJsonPath -Raw | ConvertFrom-Json

$StateFile = Join-Path $DeployDir ".env.$EnvName"
$BuildDir = Join-Path $DeployDir "build/$EnvName"
if (-not (Test-Path $StateFile)) { New-Item -ItemType File -Path $StateFile | Out-Null }
if (-not $IsWindows) { chmod 600 $StateFile }

function Read-KeyValueFile([string] $Path) {
    $map = [ordered]@{}
    if (Test-Path $Path) {
        foreach ($line in Get-Content $Path) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) { $map[$line.Substring(0, $i)] = $line.Substring($i + 1) }
        }
    }
    return $map
}

function Set-KeyValue([string] $Path, [string] $Key, [string] $Value) {
    $map = Read-KeyValueFile $Path
    $map[$Key] = $Value
    Set-Content -Path $Path -Value ($map.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
}

function Get-State([string] $Key) { $m = Read-KeyValueFile $StateFile; if ($m.Contains($Key)) { $m[$Key] } else { '' } }
function Set-State([string] $Key, [string] $Value) { Set-KeyValue $StateFile $Key $Value }
function Use-Remembered([string] $Key, [string] $Value) {
    if ($Value) { Set-State $Key $Value; return $Value }
    return (Get-State $Key)
}

$DisplayName = Use-Remembered 'DISPLAY_NAME' $DisplayName
$Route = Use-Remembered 'ROUTE' $Route
$Location = Use-Remembered 'LOCATION' $Location
if (-not $Location) { $Location = 'eastus'; Set-State 'LOCATION' $Location }
if (-not $DisplayName) { Die '-DisplayName is required' }
if ($Route -notin 'package', 'admin') { Die '-Route must be package or admin' }

$Slug = (($DisplayName.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-'))
$BaseName = Get-State 'BASE_NAME'
if (-not $BaseName) {
    $max = 19 - $EnvName.Length
    $BaseName = ($Slug.Substring(0, [Math]::Min($max, $Slug.Length))).TrimEnd('-') + "-$EnvName"
    Set-State 'BASE_NAME' $BaseName
}
$ResourceGroup = Use-Remembered 'RESOURCE_GROUP' $ResourceGroup
if (-not $ResourceGroup) { $ResourceGroup = "rg-$BaseName"; Set-State 'RESOURCE_GROUP' $ResourceGroup }
$ConnectorId = $BaseName
$AuthMode = $ProviderJson.auth.mode
$ProviderName = $ProviderJson.providerName
$Oauth = @{}

function Confirm-Action([string] $Message) {
    if ($Yes) { return $true }
    return (Read-Host "$Message [y/N]") -match '^[Yy]$'
}

function Export-ProviderInputs {
    foreach ($in in $ProviderJson.inputs) {
        [Environment]::SetEnvironmentVariable($in.env, (Get-State "INPUT_$($in.env)"))
    }
}

# ---------------------------------------------------------------- step 0: preflight
function Step-Preflight {
    Log '0. Preflight'
    foreach ($tool in 'az', 'atk') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Die "$tool is required" }
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Warn 'dotnet not found: the smoke test step will be skipped' }
    & az account show --only-show-errors --output none 2>$null
    if ($LASTEXITCODE -ne 0) { Die "run 'az login' first" }
    Set-State 'TENANT_ID' (Invoke-Native az account show --query tenantId -o tsv)
    Info "Azure subscription: $(Invoke-Native az account show --query name -o tsv)"
    Info "Provider: $Provider ($AuthMode), route: $Route, resource group: $ResourceGroup"

    foreach ($in in $ProviderJson.inputs) {
        $value = $Inputs[$in.name]
        if (-not $value) { $value = [Environment]::GetEnvironmentVariable($in.env) }
        if (-not $value) { $value = Get-State "INPUT_$($in.env)" }
        if (-not $value -and ($in.PSObject.Properties.Name -contains 'default')) { $value = $in.default }
        if (-not $value -and $in.required) { Die "missing provider input '$($in.name)' ($($in.description))" }
        Set-State "INPUT_$($in.env)" "$value"
        [Environment]::SetEnvironmentVariable($in.env, "$value")
    }

    if ($Route -eq 'package') {
        $script:PublisherName = Use-Remembered 'PUBLISHER_NAME' $PublisherName
        $script:PublisherUrl = Use-Remembered 'PUBLISHER_URL' $PublisherUrl
        $script:PrivacyUrl = Use-Remembered 'PRIVACY_URL' $PrivacyUrl
        $script:TermsUrl = Use-Remembered 'TERMS_URL' $TermsUrl
        if (-not $script:PublisherName) { Die 'package route needs -PublisherName' }
        if (-not $script:PublisherUrl) { Die 'package route needs -PublisherUrl' }
        if (-not $script:PrivacyUrl) { Die 'package route needs -PrivacyUrl (see PRIVACY.md)' }
        if (-not $script:TermsUrl) { Die 'package route needs -TermsUrl (see TERMS.md)' }
    }

    if ($ProviderJson.hooks.preflight) {
        Info 'running provider preflight hook'
        & (Join-Path $ProviderDir ($ProviderJson.hooks.preflight -replace '\.sh$', '.ps1'))
    }
}

# ---------------------------------------------------------------- step 1: core infrastructure
function Step-Core {
    Log '1. Core infrastructure'
    if (Get-State 'CONTAINER_APP_NAME') { Info 'already deployed (skip)'; return }
    Invoke-Native az group create --name $ResourceGroup --location $Location --only-show-errors --output none | Out-Null
    if (-not (Get-State 'NAME_SALT')) { Set-State 'NAME_SALT' ([Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(4)).ToLowerInvariant()) }
    # The signing key goes through a private parameters file so it never appears on a command line.
    $keyFile = New-TemporaryFile
    try {
        if (-not $IsWindows) { chmod 600 $keyFile }
        @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = @{ handleSigningKey = @{ value = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) } }
        } | ConvertTo-Json -Depth 5 | Set-Content $keyFile
        $outputs = Invoke-Native az deployment group create --name 'fc-core' --resource-group $ResourceGroup `
            --template-file (Join-Path $DeployDir 'infra/core.bicep') `
            --parameters "baseName=$BaseName" "location=$Location" "displayName=$DisplayName" "providerName=$ProviderName" "nameSalt=$(Get-State 'NAME_SALT')" `
            --parameters "@$keyFile" `
            --query properties.outputs --only-show-errors -o json | ConvertFrom-Json
    }
    finally { Remove-Item $keyFile -Force }
    foreach ($p in $outputs.PSObject.Properties) {
        Set-State (($p.Name -creplace '([A-Z])', '_$1').ToUpperInvariant()) "$($p.Value.value)"
    }
    $fqdn = Get-State 'CONTAINER_APP_FQDN'
    Set-State 'CONNECTOR_BASE_URL' "https://$fqdn"
    Set-State 'CONNECTOR_MCP_URL' "https://$fqdn/mcp"
    Info "connector URL: https://$fqdn/mcp"
}

# ---------------------------------------------------------------- step 2: provider provisioning
function Step-Provider {
    Log "2. Provider provisioning ($Provider)"
    if ((Get-State 'PROVIDER_PROVISIONED') -eq 'true') { Info 'already provisioned (skip)'; return }
    if ($ProviderJson.hooks.provisionPowerShell) {
        $env:IDENTITY_PRINCIPAL_ID = Get-State 'IDENTITY_PRINCIPAL_ID'
        & (Join-Path $ProviderDir $ProviderJson.hooks.provisionPowerShell)
    }
    Set-State 'PROVIDER_PROVISIONED' 'true'
}

# ---------------------------------------------------------------- step 3: auth
function Update-GraphApplication([string] $ObjectId, [object] $Body) {
    $file = New-TemporaryFile
    try {
        $Body | ConvertTo-Json -Depth 10 | Set-Content $file
        Invoke-Native az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$ObjectId" `
            --headers 'Content-Type=application/json' --body "@$file" --only-show-errors --output none | Out-Null
    }
    finally { Remove-Item $file -Force }
}

function Step-AuthEntra {
    Log '3a. Auth: Entra SSO + on-behalf-of'
    $appId = Get-State 'ENTRA_APP_ID'
    if (-not $appId) {
        $appId = Invoke-Native az ad app create --display-name "$DisplayName connector ($EnvName)" --sign-in-audience AzureADMyOrg --query appId -o tsv --only-show-errors
        Set-State 'ENTRA_APP_ID' $appId
        Info "created Entra app $appId"
    }
    $objectId = Invoke-Native az ad app show --id $appId --query id -o tsv

    if ((Get-State 'ENTRA_APP_CONFIGURED') -ne 'true') {
        $rra = @()
        foreach ($d in $ProviderJson.auth.downstream) {
            $sp = Invoke-Native az ad sp show --id $d.resource --query "{appId: appId, scopeId: oauth2PermissionScopes[?value=='$($d.scope)'].id | [0]}" -o json --only-show-errors | ConvertFrom-Json
            $rra += @{ resourceAppId = $sp.appId; resourceAccess = @(@{ id = $sp.scopeId; type = 'Scope' }) }
        }
        $scopeId = Get-State 'ENTRA_SCOPE_ID'
        if (-not $scopeId) { $scopeId = [guid]::NewGuid().ToString(); Set-State 'ENTRA_SCOPE_ID' $scopeId }
        Update-GraphApplication $objectId @{
            identifierUris         = @()
            api                    = @{
                requestedAccessTokenVersion = 2
                oauth2PermissionScopes      = @(@{
                        id = $scopeId; value = 'access_as_user'; type = 'User'; isEnabled = $true
                        adminConsentDisplayName = "Access $DisplayName as the signed-in user"
                        adminConsentDescription = "Lets Microsoft 365 Copilot search and read $DisplayName as the signed-in user."
                        userConsentDisplayName  = "Access $DisplayName as you"
                        userConsentDescription  = "Lets Microsoft 365 Copilot search and read $DisplayName as you."
                    })
            }
            web                    = @{ redirectUris = @($TeamsRedirectUri) }
            requiredResourceAccess = $rra
        }
        Invoke-Native az ad app update --id $appId --identifier-uris "api://$appId" --only-show-errors | Out-Null
        & az ad sp show --id $appId --only-show-errors --output none 2>$null
        if ($LASTEXITCODE -ne 0) { Invoke-Native az ad sp create --id $appId --only-show-errors --output none | Out-Null }
        # Federated credential: the managed identity authenticates as the app for OBO, so there is no client secret.
        $fic = @{
            name      = 'connector-managed-identity'
            issuer    = "https://login.microsoftonline.com/$(Get-State 'TENANT_ID')/v2.0"
            subject   = Get-State 'IDENTITY_PRINCIPAL_ID'
            audiences = @('api://AzureADTokenExchange')
        } | ConvertTo-Json -Compress
        $ficFile = New-TemporaryFile
        try {
            Set-Content $ficFile $fic
            Invoke-Native az ad app federated-credential create --id $appId --parameters "@$ficFile" --only-show-errors --output none | Out-Null
        }
        finally { Remove-Item $ficFile -Force }
        & az ad app permission admin-consent --id $appId --only-show-errors 2>$null
        if ($LASTEXITCODE -ne 0) { Warn "admin consent failed; a tenant admin must grant consent for app $appId" }
        Set-State 'ENTRA_APP_CONFIGURED' 'true'
    }

    $script:Oauth = @{
        OAUTH_IDENTITY_PROVIDER = 'MicrosoftEntra'
        OAUTH_CLIENT_ID         = $appId
        OAUTH_SCOPES            = "api://$appId/access_as_user"
    }
    Register-AtkOAuth

    # Developer Portal issues an api://auth-… URI that must be on the app and in the accepted audiences.
    $ssoUri = Get-State 'OAUTH_APPLICATION_ID_URI'
    if ($ssoUri -and (Get-State 'ENTRA_SSO_URI_ADDED') -ne 'true') {
        Invoke-Native az ad app update --id $appId --identifier-uris "api://$appId" $ssoUri --only-show-errors | Out-Null
        Set-State 'ENTRA_SSO_URI_ADDED' 'true'
    }
}

function Step-AuthPassthrough {
    Log '3b. Auth: OAuth 2.0 pass-through'
    $clientId = Use-Remembered 'OAUTH_CLIENT_ID' $OAuthClientId
    if (-not $clientId) { Die "OAuthPassthrough needs -OAuthClientId (issued by the source's identity provider)" }
    if (-not (Get-State 'OAUTH_CONFIGURATION_ID') -and -not $env:OAUTH_CLIENT_SECRET) {
        Die 'set OAUTH_CLIENT_SECRET in the environment (it is passed to Developer Portal and never stored here)'
    }
    $script:Oauth = @{
        OAUTH_IDENTITY_PROVIDER = 'Custom'
        OAUTH_CLIENT_ID         = $clientId
        OAUTH_AUTHORIZE_URL     = $ProviderJson.auth.authorizeUrl
        OAUTH_TOKEN_URL         = $ProviderJson.auth.tokenUrl
        OAUTH_SCOPES            = ($ProviderJson.auth.scopes -join ' ')
        OAUTH_PKCE              = "$(if ($ProviderJson.auth.PSObject.Properties.Name -contains 'supportsPkce') { $ProviderJson.auth.supportsPkce.ToString().ToLowerInvariant() } else { 'false' })"
    }
    Register-AtkOAuth
}

function Write-AtkEnvFile([string] $Dir) {
    $envDir = Join-Path $Dir 'env'
    New-Item -ItemType Directory -Force -Path $envDir | Out-Null
    $f = Join-Path $envDir ".env.$EnvName"
    if (-not (Test-Path $f)) { New-Item -ItemType File -Path $f | Out-Null }
    $values = [ordered]@{
        TEAMSFX_ENV            = $EnvName
        APP_VERSION            = '1.0.0'
        CONNECTOR_ID           = $ConnectorId
        CONNECTOR_DISPLAY_NAME = $DisplayName
        CONTENT_DESCRIPTION    = $(if ($env:CONTENT_DESCRIPTION) { $env:CONTENT_DESCRIPTION } else { $ProviderJson.displayName })
        CONNECTOR_BASE_URL     = Get-State 'CONNECTOR_BASE_URL'
        CONNECTOR_MCP_URL      = Get-State 'CONNECTOR_MCP_URL'
        CONNECTOR_HOST         = Get-State 'CONTAINER_APP_FQDN'
        TEAMS_APP_ID           = Get-State 'TEAMS_APP_ID'
        OAUTH_CONFIGURATION_ID = Get-State 'OAUTH_CONFIGURATION_ID'
        PUBLISHER_NAME         = $script:PublisherName
        PUBLISHER_WEBSITE_URL  = $script:PublisherUrl
        PRIVACY_URL            = $script:PrivacyUrl
        TERMS_URL              = $script:TermsUrl
    }
    foreach ($k in $script:Oauth.Keys) { $values[$k] = $script:Oauth[$k] }
    foreach ($k in $values.Keys) { if ($values[$k]) { Set-KeyValue $f $k "$($values[$k])" } }
    return $f
}

function Register-AtkOAuth {
    if (Get-State 'OAUTH_CONFIGURATION_ID') { Info 'OAuth registration exists (skip)'; return }
    $dir = Join-Path $BuildDir 'oauth'
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'env') | Out-Null
    $template = Get-Content (Join-Path $DeployDir 'templates/atk/oauth.m365agents.yml')
    $userFile = Join-Path $dir "env/.env.$EnvName.user"
    if ($script:Oauth.OAUTH_IDENTITY_PROVIDER -eq 'MicrosoftEntra') {
        $template | Where-Object { $_ -notmatch '# custom-only$' } | Set-Content (Join-Path $dir 'm365agents.yml')
    }
    else {
        $template | ForEach-Object { $_ -replace '  # custom-only$', '' } | Set-Content (Join-Path $dir 'm365agents.yml')
        Set-Content $userFile "SECRET_OAUTH_CLIENT_SECRET=$($env:OAUTH_CLIENT_SECRET)"
        if (-not $IsWindows) { chmod 600 $userFile }
    }
    $envFile = Write-AtkEnvFile $dir
    Info 'registering with Teams Developer Portal (atk provision)'
    Push-Location $dir
    try { Invoke-Native atk provision --env $EnvName --interactive false | Out-Host }
    finally { Pop-Location; Remove-Item $userFile -Force -ErrorAction SilentlyContinue }
    $atk = Read-KeyValueFile $envFile
    if (-not $atk['OAUTH_CONFIGURATION_ID']) { Die 'atk did not return an OAuth configuration id' }
    Set-State 'OAUTH_CONFIGURATION_ID' $atk['OAUTH_CONFIGURATION_ID']
    Set-State 'TEAMS_APP_ID' "$($atk['TEAMS_APP_ID'])"
    if ($atk['OAUTH_APPLICATION_ID_URI']) { Set-State 'OAUTH_APPLICATION_ID_URI' $atk['OAUTH_APPLICATION_ID_URI'] }
    Info "registration id: $($atk['OAUTH_CONFIGURATION_ID'])"
}

# ---------------------------------------------------------------- step 4: deploy the host
function Get-ProviderEnvVars {
    $vars = @()
    foreach ($p in $ProviderJson.settings.PSObject.Properties) {
        if ($p.Name.StartsWith('$')) { continue }
        $value = [string] $p.Value
        if ($value -match '^\$\{([A-Z_]+)\[\]\}$') {
            $i = 0
            foreach ($item in ([Environment]::GetEnvironmentVariable($Matches[1]) -split ',')) {
                $item = $item.Trim()
                if (-not $item) { continue }
                $vars += "$($p.Name.Replace('{i}', "$i"))=$item"
                $i++
            }
        }
        else {
            $expanded = [regex]::Replace($value, '\$\{([A-Z_]+)\}', { param($m) [Environment]::GetEnvironmentVariable($m.Groups[1].Value) })
            $vars += "$($p.Name)=$expanded"
        }
    }
    return $vars
}

function Step-Deploy {
    Log '4. Deploy the host'
    if ((Get-State 'HOST_IMAGE') -and -not $Redeploy) { Info "image $(Get-State 'HOST_IMAGE') deployed (skip; -Redeploy to rebuild)"; return }
    Export-ProviderInputs
    $sha = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
    $tag = "$(if ($sha) { $sha } else { 'local' })-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
    $image = "$(Get-State 'REGISTRY_LOGIN_SERVER')/federated-connector:$tag"
    Info "building $image in the registry"
    Invoke-Native az acr build --registry (Get-State 'REGISTRY_NAME') --image "federated-connector:$tag" --file (Join-Path $RepoRoot 'Dockerfile') $RepoRoot --only-show-errors --output none | Out-Null

    $vars = @(Get-ProviderEnvVars)
    if ($AuthMode -eq 'EntraObo') {
        $appId = Get-State 'ENTRA_APP_ID'
        $tenant = Get-State 'TENANT_ID'
        $vars += @(
            'AzureAd__Instance=https://login.microsoftonline.com/'
            "AzureAd__TenantId=$tenant"
            "AzureAd__ClientId=$appId"
            'AzureAd__ClientCredentials__0__SourceType=SignedAssertionFromManagedIdentity'
            "AzureAd__ClientCredentials__0__ManagedIdentityClientId=$(Get-State 'IDENTITY_CLIENT_ID')"
            "Connector__AllowedTenants__0=$tenant"
            "Connector__AcceptedAudiences__0=api://$appId"
            "Connector__AcceptedAudiences__1=$appId"
        )
        if (Get-State 'OAUTH_APPLICATION_ID_URI') { $vars += "Connector__AcceptedAudiences__2=$(Get-State 'OAUTH_APPLICATION_ID_URI')" }
    }

    # One deployment sets the image, registry identity, probes AND every setting, so no revision starts half-configured.
    # The signing key secret already exists in Key Vault and is referenced, not passed again.
    $extraEnv = @($vars | ForEach-Object { $i = $_.IndexOf('='); @{ name = $_.Substring(0, $i); value = $_.Substring($i + 1) } })
    $paramsFile = New-TemporaryFile
    try {
        @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = @{ containerImage = @{ value = $image }; extraEnv = @{ value = $extraEnv } }
        } | ConvertTo-Json -Depth 6 | Set-Content $paramsFile
        Invoke-Native az deployment group create --name 'fc-core' --resource-group $ResourceGroup `
            --template-file (Join-Path $DeployDir 'infra/core.bicep') `
            --parameters "baseName=$BaseName" "location=$Location" "displayName=$DisplayName" "providerName=$ProviderName" "nameSalt=$(Get-State 'NAME_SALT')" `
            --parameters "@$paramsFile" `
            --only-show-errors --output none | Out-Null
    }
    finally { Remove-Item $paramsFile -Force }
    Set-State 'HOST_IMAGE' $image
}

# ---------------------------------------------------------------- step 5: smoke test
function Step-Smoke {
    Log '5. Smoke test'
    $base = Get-State 'CONNECTOR_BASE_URL'
    $healthy = $false
    for ($i = 0; $i -lt 30 -and -not $healthy; $i++) {
        try { Invoke-RestMethod "$base/healthz" -TimeoutSec 10 | Out-Null; $healthy = $true } catch { Start-Sleep -Seconds 5 }
    }
    if (-not $healthy) { Die "host is not healthy at $base/healthz" }
    Info 'health check passed'
    if (-not $env:CONNECTOR_TOKEN -and $AuthMode -ne 'None') {
        Warn 'set CONNECTOR_TOKEN to a user token for this connector to run the MCP smoke test (initialize, tools/list, search, fetch)'
        return
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Warn 'dotnet not installed; skipping MCP smoke test'; return }
    & dotnet run --project (Join-Path $RepoRoot 'tools/FederatedConnector.SmokeTest') -c Release -- --url (Get-State 'CONNECTOR_MCP_URL')
    if ($LASTEXITCODE -ne 0) { Die 'smoke test failed' }
}

# ---------------------------------------------------------------- step 6
function Step-AdminRoute {
    Log '6A. Admin center route (manual)'
    Info 'In the Microsoft 365 admin center: Copilot → Connectors → Gallery → Connect to MCP server, then enter:'
    Info "  Display name:     $DisplayName"
    Info "  Base URL:         $(Get-State 'CONNECTOR_MCP_URL')"
    Info "  Registration ID:  $(Get-State 'OAUTH_CONFIGURATION_ID')"
    Info 'Then configure a staged rollout. Admin center: https://admin.microsoft.com/'
}

function Step-PackageRoute {
    Log '6B. App package route'
    $dir = Join-Path $BuildDir 'package'
    $appPackage = Join-Path $dir 'appPackage'
    New-Item -ItemType Directory -Force -Path $appPackage | Out-Null
    Copy-Item (Join-Path $DeployDir 'templates/atk/package.m365agents.yml') (Join-Path $dir 'm365agents.yml') -Force
    foreach ($f in 'color.png', 'outline.png') { Copy-Item (Join-Path $DeployDir "templates/$f") $appPackage -Force }
    $manifest = Get-Content (Join-Path $DeployDir 'templates/manifest.json') -Raw
    if ($AuthMode -eq 'None') {
        # Public data: no OAuth registration exists, so the connector entry carries no authorization block.
        $m = $manifest | ConvertFrom-Json
        foreach ($c in $m.agentConnectors) { $c.toolSource.remoteMcpServer.PSObject.Properties.Remove('authorization') }
        $manifest = $m | ConvertTo-Json -Depth 20
    }
    Set-Content (Join-Path $appPackage 'manifest.json') $manifest
    $envFile = Write-AtkEnvFile $dir
    Push-Location $dir
    try {
        if ($Publish) {
            Invoke-Native atk publish --env $EnvName --interactive false | Out-Host
            Info 'submitted to the organisation catalogue; an admin must approve it'
        }
        else {
            Invoke-Native atk provision --env $EnvName --interactive false | Out-Host
            Info 'sideloaded for the signed-in Microsoft 365 account'
        }
    }
    finally { Pop-Location }
    if (-not (Get-State 'TEAMS_APP_ID')) { Set-State 'TEAMS_APP_ID' "$((Read-KeyValueFile $envFile)['TEAMS_APP_ID'])" }
}

# ---------------------------------------------------------------- destroy
function Invoke-Destroy {
    Log "Destroying environment '$EnvName'"
    if (-not (Confirm-Action "Delete resource group $ResourceGroup, the Entra app and local state for '$EnvName'?")) { Die 'aborted' }
    if ($ProviderJson.hooks.provisionPowerShell) {
        Export-ProviderInputs
        $env:DESTROY = 'true'
        $env:IDENTITY_PRINCIPAL_ID = Get-State 'IDENTITY_PRINCIPAL_ID'
        try { & (Join-Path $ProviderDir $ProviderJson.hooks.provisionPowerShell) } catch { Warn 'provider teardown hook failed' }
    }
    $appId = Get-State 'ENTRA_APP_ID'
    if ($appId) { & az ad app delete --id $appId --only-show-errors; if ($LASTEXITCODE -ne 0) { Warn "could not delete Entra app $appId" } }
    & az group delete --name $ResourceGroup --yes --no-wait --only-show-errors
    if ($LASTEXITCODE -ne 0) { Warn "could not delete $ResourceGroup" }
    Info 'Manual clean-up (no public API):'
    Info "  - Teams Developer Portal: delete the OAuth/SSO registration $(Get-State 'OAUTH_CONFIGURATION_ID') and app $(Get-State 'TEAMS_APP_ID')"
    Info '  - Microsoft 365 admin center: remove the connector if it was added there'
    Remove-Item $StateFile -Force
    Remove-Item $BuildDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- main
if ($Destroy) { Invoke-Destroy; return }
Step-Preflight
Step-Core
Step-Provider
switch ($AuthMode) {
    'EntraObo' { Step-AuthEntra }
    'OAuthPassthrough' { Step-AuthPassthrough }
    'None' { Info 'auth mode None: no registration needed' }
    default { Die "unknown auth mode $AuthMode in provider.json" }
}
Step-Deploy
Step-Smoke
if ($Route -eq 'admin') { Step-AdminRoute } else { Step-PackageRoute }
Log "Done. State: $StateFile"
