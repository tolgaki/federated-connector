#!/usr/bin/env bash
# Registers a federated MCP connector with Microsoft 365 Copilot (design §9).
#
#   ./register-connector.sh --env dev --provider foundry-iq --display-name "Contoso Knowledge" \
#       --route package|admin [--publish] [provider flags, e.g. --search-service, --search-resource-group, --knowledge-bases]
#
# Driven by deploy/providers/<provider>/provider.json. Idempotent: each step records its outputs in deploy/.env.<env>
# and is skipped on re-run when they exist (use --redeploy to rebuild and redeploy the host image).
# --destroy reverses the steps. Secrets are never written to the state file.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$DEPLOY_DIR/.." && pwd)"
TEAMS_REDIRECT_URI="https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect"

usage() {
  cat <<'EOF'
Usage: register-connector.sh --env <name> --provider <id> --display-name <name> --route package|admin [options]

Required:
  --env <name>                 Environment name (state is kept in deploy/.env.<name>)
  --provider <id>              Provider folder under deploy/providers (e.g. foundry-iq)
  --display-name <name>        Connector display name (tool titles, gallery entry)
  --route package|admin        6B: app package (ATK) or 6A: admin center (manual final step)

Options:
  --location <region>          Azure region for new resources (default: eastus)
  --resource-group <name>      Resource group for the connector (default: rg-<base>)
  --publisher-name <name>      App package developer name (package route)
  --publisher-url <url>        App package developer website (package route)
  --privacy-url <url>          Privacy statement URL (package route; see PRIVACY.md)
  --terms-url <url>            Terms of use URL (package route; see TERMS.md)
  --publish                    Package route: publish to the org catalogue instead of sideloading
  --oauth-client-id <id>       OAuthPassthrough: client id issued by the source's identity provider
                               (client secret is read from the OAUTH_CLIENT_SECRET environment variable)
  --redeploy                   Rebuild and redeploy the host image even if one is deployed
  --destroy                    Remove what earlier runs created
  --yes                        Do not prompt for confirmation
  --<input> <value>            Provider inputs declared in provider.json (e.g. --search-service)
EOF
}

log()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '\033[33m    ! %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- arguments
ENV_NAME="" PROVIDER="" DISPLAY_NAME="" ROUTE="" LOCATION="" RESOURCE_GROUP=""
PUBLISHER_NAME="" PUBLISHER_URL="" PRIVACY_URL="" TERMS_URL="" OAUTH_CLIENT_ID_ARG=""
PUBLISH=false REDEPLOY=false DESTROY=false ASSUME_YES=false
# Provider inputs arrive as --<name> <value> and are kept in ARG_<NAME> variables (bash 3.2 compatible).
input_var() { printf 'ARG_%s' "$(tr '[:lower:]-' '[:upper:]_' <<<"$1")"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env) ENV_NAME="$2"; shift 2 ;;
    --provider) PROVIDER="$2"; shift 2 ;;
    --display-name) DISPLAY_NAME="$2"; shift 2 ;;
    --route) ROUTE="$2"; shift 2 ;;
    --location) LOCATION="$2"; shift 2 ;;
    --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
    --publisher-name) PUBLISHER_NAME="$2"; shift 2 ;;
    --publisher-url) PUBLISHER_URL="$2"; shift 2 ;;
    --privacy-url) PRIVACY_URL="$2"; shift 2 ;;
    --terms-url) TERMS_URL="$2"; shift 2 ;;
    --oauth-client-id) OAUTH_CLIENT_ID_ARG="$2"; shift 2 ;;
    --publish) PUBLISH=true; shift ;;
    --redeploy) REDEPLOY=true; shift ;;
    --destroy) DESTROY=true; shift ;;
    --yes|-y) ASSUME_YES=true; shift ;;
    -h|--help) usage; exit 0 ;;
    --*) [[ $# -ge 2 ]] || die "$1 needs a value"; printf -v "$(input_var "${1#--}")" '%s' "$2"; shift 2 ;;
    *) die "unexpected argument: $1" ;;
  esac
done

[[ -n "$ENV_NAME" ]] || { usage; die "--env is required"; }
[[ "$ENV_NAME" =~ ^[a-z0-9-]+$ ]] || die "--env must be lowercase letters, digits and dashes"
[[ -n "$PROVIDER" ]] || die "--provider is required"
PROVIDER_DIR="$DEPLOY_DIR/providers/$PROVIDER"
PROVIDER_JSON="$PROVIDER_DIR/provider.json"
[[ -f "$PROVIDER_JSON" ]] || die "unknown provider '$PROVIDER' (no $PROVIDER_JSON)"

# ---------------------------------------------------------------- state (.env.<env>)
STATE_FILE="$DEPLOY_DIR/.env.$ENV_NAME"
BUILD_DIR="$DEPLOY_DIR/build/$ENV_NAME"
touch "$STATE_FILE"
chmod 600 "$STATE_FILE"

state_get() { grep -E "^$1=" "$STATE_FILE" | tail -n1 | cut -d= -f2- || true; }
state_set() {
  local tmp; tmp="$(mktemp)"
  grep -vE "^$1=" "$STATE_FILE" > "$tmp" || true
  printf '%s=%s\n' "$1" "$2" >> "$tmp"
  mv "$tmp" "$STATE_FILE"; chmod 600 "$STATE_FILE"
}
# Remember the invocation so later runs (and --destroy) can omit flags.
remember() { local k="$1" v="$2"; if [[ -n "$v" ]]; then state_set "$k" "$v"; else v="$(state_get "$k")"; fi; printf '%s' "$v"; }

DISPLAY_NAME="$(remember DISPLAY_NAME "$DISPLAY_NAME")"
ROUTE="$(remember ROUTE "$ROUTE")"
LOCATION="$(remember LOCATION "$LOCATION")"
if [[ -z "$LOCATION" ]]; then LOCATION="eastus"; state_set LOCATION "$LOCATION"; fi
[[ -n "$DISPLAY_NAME" ]] || die "--display-name is required"
[[ "$ROUTE" == "package" || "$ROUTE" == "admin" ]] || die "--route must be package or admin"

SLUG="$(printf '%s' "$DISPLAY_NAME" | tr '[:upper:]' '[:lower:]' | tr -cs 'a-z0-9' '-' | sed 's/^-*//;s/-*$//')"
BASE_NAME="$(state_get BASE_NAME)"
if [[ -z "$BASE_NAME" ]]; then
  BASE_NAME="$(printf '%s' "${SLUG:0:$((19 - ${#ENV_NAME}))}" | sed 's/-*$//')-$ENV_NAME"
  state_set BASE_NAME "$BASE_NAME"
fi
RESOURCE_GROUP="$(remember RESOURCE_GROUP "$RESOURCE_GROUP")"
[[ -n "$RESOURCE_GROUP" ]] || { RESOURCE_GROUP="rg-$BASE_NAME"; state_set RESOURCE_GROUP "$RESOURCE_GROUP"; }
CONNECTOR_ID="$BASE_NAME"

pj() { jq -r "$1" "$PROVIDER_JSON"; }
AUTH_MODE="$(pj '.auth.mode')"
PROVIDER_NAME="$(pj '.providerName')"

confirm() {
  $ASSUME_YES && return 0
  read -r -p "$1 [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]]
}

# ---------------------------------------------------------------- step 0: preflight
step_preflight() {
  log "0. Preflight"
  for tool in az jq openssl curl; do command -v "$tool" >/dev/null || die "$tool is required"; done
  command -v atk >/dev/null || die "Microsoft 365 Agents Toolkit CLI (atk) is required: npm install -g @microsoft/m365agentstoolkit-cli"
  command -v dotnet >/dev/null || warn "dotnet not found: the smoke test step will be skipped"
  az account show --only-show-errors --output none 2>/dev/null || die "run 'az login' first"
  TENANT_ID="$(az account show --query tenantId -o tsv)"
  state_set TENANT_ID "$TENANT_ID"
  info "Azure subscription: $(az account show --query name -o tsv)"
  info "Provider: $PROVIDER ($AUTH_MODE), route: $ROUTE, resource group: $RESOURCE_GROUP"

  local count i name env required default value
  count="$(pj '.inputs | length')"
  for ((i = 0; i < count; i++)); do
    name="$(pj ".inputs[$i].name")"; env="$(pj ".inputs[$i].env")"
    required="$(pj ".inputs[$i].required")"; default="$(pj ".inputs[$i].default // empty")"
    local arg_var; arg_var="$(input_var "$name")"
    value="${!arg_var:-${!env:-$(state_get "INPUT_$env")}}"
    value="${value:-$default}"
    if [[ -z "$value" && "$required" == "true" ]]; then
      die "missing provider input --$name ($(pj ".inputs[$i].description"))"
    fi
    state_set "INPUT_$env" "$value"
    export "$env=$value"
  done

  if [[ "$ROUTE" == "package" ]]; then
    PUBLISHER_NAME="$(remember PUBLISHER_NAME "$PUBLISHER_NAME")"
    PUBLISHER_URL="$(remember PUBLISHER_URL "$PUBLISHER_URL")"
    PRIVACY_URL="$(remember PRIVACY_URL "$PRIVACY_URL")"
    TERMS_URL="$(remember TERMS_URL "$TERMS_URL")"
    [[ -n "$PUBLISHER_NAME" ]] || die "package route needs --publisher-name"
    [[ -n "$PUBLISHER_URL" ]] || die "package route needs --publisher-url"
    [[ -n "$PRIVACY_URL" ]] || die "package route needs --privacy-url (see PRIVACY.md)"
    [[ -n "$TERMS_URL" ]] || die "package route needs --terms-url (see TERMS.md)"
  fi

  local hook; hook="$(pj '.hooks.preflight // empty')"
  if [[ -n "$hook" ]]; then
    info "running provider preflight hook"
    bash "$PROVIDER_DIR/$hook"
  fi
}

# ---------------------------------------------------------------- step 1: core infrastructure
step_core() {
  log "1. Core infrastructure"
  if [[ -n "$(state_get CONTAINER_APP_NAME)" ]]; then info "already deployed (skip)"; return; fi
  az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --only-show-errors --output none
  if [[ -z "$(state_get NAME_SALT)" ]]; then state_set NAME_SALT "$(openssl rand -hex 4)"; fi
  # The signing key goes through a private parameters file so it never appears on a command line.
  local keyfile outputs; keyfile="$(mktemp)"; chmod 600 "$keyfile"
  jq -n --arg k "$(openssl rand -base64 32)" '{"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#", contentVersion: "1.0.0.0", parameters: {handleSigningKey: {value: $k}}}' > "$keyfile"
  outputs="$(az deployment group create \
    --name "fc-core" --resource-group "$RESOURCE_GROUP" \
    --template-file "$DEPLOY_DIR/infra/core.bicep" \
    --parameters baseName="$BASE_NAME" location="$LOCATION" displayName="$DISPLAY_NAME" providerName="$PROVIDER_NAME" \
                 nameSalt="$(state_get NAME_SALT)" \
    --parameters "@$keyfile" \
    --query properties.outputs --only-show-errors -o json)" || { rm -f "$keyfile"; die "core deployment failed"; }
  rm -f "$keyfile"
  for k in identityClientId identityPrincipalId identityResourceId keyVaultName registryLoginServer registryName containerAppName containerAppFqdn; do
    state_set "$(sed -E 's/([A-Z])/_\1/g' <<<"$k" | tr '[:lower:]' '[:upper:]')" "$(jq -r ".${k}.value" <<<"$outputs")"
  done
  local fqdn; fqdn="$(state_get CONTAINER_APP_FQDN)"
  state_set CONNECTOR_BASE_URL "https://$fqdn"
  state_set CONNECTOR_MCP_URL "https://$fqdn/mcp"
  info "connector URL: https://$fqdn/mcp"
}

# ---------------------------------------------------------------- step 2: provider provisioning
step_provider() {
  log "2. Provider provisioning ($PROVIDER)"
  if [[ "$(state_get PROVIDER_PROVISIONED)" == "true" ]]; then info "already provisioned (skip)"; return; fi
  local hook; hook="$(pj '.hooks.provision // empty')"
  if [[ -n "$hook" ]]; then
    IDENTITY_PRINCIPAL_ID="$(state_get IDENTITY_PRINCIPAL_ID)" bash "$PROVIDER_DIR/$hook"
  fi
  state_set PROVIDER_PROVISIONED true
}

# ---------------------------------------------------------------- step 3: auth
graph_patch_app() { az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$1" --headers 'Content-Type=application/json' --body "$2" --only-show-errors --output none; }

step_auth_entra() {
  log "3a. Auth: Entra SSO + on-behalf-of"
  local app_id; app_id="$(state_get ENTRA_APP_ID)"
  if [[ -z "$app_id" ]]; then
    app_id="$(az ad app create --display-name "$DISPLAY_NAME connector ($ENV_NAME)" --sign-in-audience AzureADMyOrg --query appId -o tsv --only-show-errors)"
    state_set ENTRA_APP_ID "$app_id"
    info "created Entra app $app_id"
  fi
  local object_id; object_id="$(az ad app show --id "$app_id" --query id -o tsv)"

  if [[ "$(state_get ENTRA_APP_CONFIGURED)" != "true" ]]; then
    # Downstream delegated permissions declared by the provider.
    local rra="[]" count i resource scope sp
    count="$(pj '.auth.downstream | length')"
    for ((i = 0; i < count; i++)); do
      resource="$(pj ".auth.downstream[$i].resource")"; scope="$(pj ".auth.downstream[$i].scope")"
      sp="$(az ad sp show --id "$resource" --query "{appId: appId, scopeId: oauth2PermissionScopes[?value=='$scope'].id | [0]}" -o json --only-show-errors)"
      rra="$(jq --argjson sp "$sp" '. + [{resourceAppId: $sp.appId, resourceAccess: [{id: $sp.scopeId, type: "Scope"}]}]' <<<"$rra")"
    done
    local scope_id; scope_id="$(state_get ENTRA_SCOPE_ID)"
    if [[ -z "$scope_id" ]]; then
      scope_id="$( (uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid) | tr '[:upper:]' '[:lower:]')"
      state_set ENTRA_SCOPE_ID "$scope_id"
    fi
    graph_patch_app "$object_id" "$(jq -n --arg id "$scope_id" --arg name "$DISPLAY_NAME" --arg redirect "$TEAMS_REDIRECT_URI" --argjson rra "$rra" '{
      identifierUris: [],
      api: { requestedAccessTokenVersion: 2, oauth2PermissionScopes: [{
        id: $id, value: "access_as_user", type: "User", isEnabled: true,
        adminConsentDisplayName: ("Access " + $name + " as the signed-in user"),
        adminConsentDescription: ("Lets Microsoft 365 Copilot search and read " + $name + " as the signed-in user."),
        userConsentDisplayName: ("Access " + $name + " as you"),
        userConsentDescription: ("Lets Microsoft 365 Copilot search and read " + $name + " as you.") }] },
      web: { redirectUris: [$redirect] },
      requiredResourceAccess: $rra }')"
    az ad app update --id "$app_id" --identifier-uris "api://$app_id" --only-show-errors
    az ad sp show --id "$app_id" --only-show-errors --output none 2>/dev/null || az ad sp create --id "$app_id" --only-show-errors --output none
    # Federated credential: the managed identity authenticates as the app for OBO, so there is no client secret.
    az ad app federated-credential create --id "$app_id" --only-show-errors --output none --parameters "$(jq -n \
      --arg issuer "https://login.microsoftonline.com/$(state_get TENANT_ID)/v2.0" --arg subject "$(state_get IDENTITY_PRINCIPAL_ID)" \
      '{name: "connector-managed-identity", issuer: $issuer, subject: $subject, audiences: ["api://AzureADTokenExchange"]}')"
    if ! az ad app permission admin-consent --id "$app_id" --only-show-errors 2>/dev/null; then
      warn "admin consent failed; a tenant admin must grant consent for app $app_id"
    fi
    state_set ENTRA_APP_CONFIGURED true
  fi

  OAUTH_IDENTITY_PROVIDER="MicrosoftEntra"
  OAUTH_CLIENT_ID="$app_id"
  OAUTH_SCOPES="api://$app_id/access_as_user"
  atk_oauth_register

  # Developer Portal issues an api://auth-… URI that must be on the app and in the accepted audiences.
  local sso_uri; sso_uri="$(state_get OAUTH_APPLICATION_ID_URI)"
  if [[ -n "$sso_uri" && "$(state_get ENTRA_SSO_URI_ADDED)" != "true" ]]; then
    az ad app update --id "$app_id" --identifier-uris "api://$app_id" "$sso_uri" --only-show-errors
    state_set ENTRA_SSO_URI_ADDED true
  fi
}

step_auth_passthrough() {
  log "3b. Auth: OAuth 2.0 pass-through"
  OAUTH_CLIENT_ID="$(remember OAUTH_CLIENT_ID "$OAUTH_CLIENT_ID_ARG")"
  [[ -n "$OAUTH_CLIENT_ID" ]] || die "OAuthPassthrough needs --oauth-client-id (issued by the source's identity provider)"
  if [[ -z "$(state_get OAUTH_CONFIGURATION_ID)" && -z "${OAUTH_CLIENT_SECRET:-}" ]]; then
    die "set OAUTH_CLIENT_SECRET in the environment (it is passed to Developer Portal and never stored here)"
  fi
  OAUTH_IDENTITY_PROVIDER="Custom"
  OAUTH_AUTHORIZE_URL="$(pj '.auth.authorizeUrl')"
  OAUTH_TOKEN_URL="$(pj '.auth.tokenUrl')"
  OAUTH_SCOPES="$(pj '.auth.scopes | join(" ")')"
  OAUTH_PKCE="$(pj '.auth.supportsPkce // false')"
  atk_oauth_register
}

atk_env_file() { # $1 = project dir; writes the ATK environment file with the variables the templates need
  local dir="$1"; mkdir -p "$dir/env"
  local f="$dir/env/.env.$ENV_NAME"
  touch "$f"
  set_kv() { local tmp; tmp="$(mktemp)"; grep -vE "^$2=" "$1" > "$tmp" || true; printf '%s=%s\n' "$2" "$3" >> "$tmp"; mv "$tmp" "$1"; }
  set_kv "$f" TEAMSFX_ENV "$ENV_NAME"
  set_kv "$f" APP_VERSION "1.0.0"
  set_kv "$f" CONNECTOR_ID "$CONNECTOR_ID"
  set_kv "$f" CONNECTOR_DISPLAY_NAME "$DISPLAY_NAME"
  set_kv "$f" CONTENT_DESCRIPTION "${CONTENT_DESCRIPTION:-$(pj '.displayName')}"
  set_kv "$f" CONNECTOR_BASE_URL "$(state_get CONNECTOR_BASE_URL)"
  set_kv "$f" CONNECTOR_MCP_URL "$(state_get CONNECTOR_MCP_URL)"
  set_kv "$f" CONNECTOR_HOST "$(state_get CONTAINER_APP_FQDN)"
  local v
  for v in TEAMS_APP_ID OAUTH_CONFIGURATION_ID; do
    if [[ -n "$(state_get "$v")" ]]; then set_kv "$f" "$v" "$(state_get "$v")"; fi
  done
  for v in OAUTH_IDENTITY_PROVIDER OAUTH_CLIENT_ID OAUTH_AUTHORIZE_URL OAUTH_TOKEN_URL OAUTH_SCOPES OAUTH_PKCE \
           PUBLISHER_NAME PUBLISHER_URL PRIVACY_URL TERMS_URL; do
    if [[ -n "${!v:-}" ]]; then set_kv "$f" "$v" "${!v}"; fi
  done
  if [[ -n "${PUBLISHER_URL:-}" ]]; then set_kv "$f" PUBLISHER_WEBSITE_URL "$PUBLISHER_URL"; fi
  printf '%s' "$f"
}

atk_read() { grep -E "^$2=" "$1" | tail -n1 | cut -d= -f2- || true; }

atk_oauth_register() {
  if [[ -n "$(state_get OAUTH_CONFIGURATION_ID)" ]]; then info "OAuth registration exists (skip)"; return; fi
  local dir="$BUILD_DIR/oauth"; mkdir -p "$dir"
  if [[ "$OAUTH_IDENTITY_PROVIDER" == "MicrosoftEntra" ]]; then
    sed '/# custom-only$/d' "$DEPLOY_DIR/templates/atk/oauth.m365agents.yml" > "$dir/m365agents.yml"
  else
    sed 's/  # custom-only$//' "$DEPLOY_DIR/templates/atk/oauth.m365agents.yml" > "$dir/m365agents.yml"
    mkdir -p "$dir/env"
    (umask 077; printf 'SECRET_OAUTH_CLIENT_SECRET=%s\n' "$OAUTH_CLIENT_SECRET" > "$dir/env/.env.$ENV_NAME.user")
  fi
  local envfile; envfile="$(atk_env_file "$dir")"
  info "registering with Teams Developer Portal (atk provision)"
  (cd "$dir" && atk provision --env "$ENV_NAME" --interactive false)
  rm -f "$dir/env/.env.$ENV_NAME.user"
  local cfg; cfg="$(atk_read "$envfile" OAUTH_CONFIGURATION_ID)"
  [[ -n "$cfg" ]] || die "atk did not return an OAuth configuration id"
  state_set OAUTH_CONFIGURATION_ID "$cfg"
  state_set TEAMS_APP_ID "$(atk_read "$envfile" TEAMS_APP_ID)"
  local uri; uri="$(atk_read "$envfile" OAUTH_APPLICATION_ID_URI)"
  if [[ -n "$uri" ]]; then state_set OAUTH_APPLICATION_ID_URI "$uri"; fi
  info "registration id: $cfg"
}

# ---------------------------------------------------------------- step 4: deploy the host
provider_env_vars() {
  local key value item i
  while IFS=$'\t' read -r key value; do
    if [[ "$value" =~ \$\{([A-Z_]+)\[\]\} ]]; then
      local var="${BASH_REMATCH[1]}"; IFS=',' read -r -a items <<<"${!var:-}"; i=0
      for item in "${items[@]}"; do
        item="$(xargs <<<"$item")"; [[ -n "$item" ]] || continue
        printf '%s=%s\n' "${key//\{i\}/$i}" "$item"; i=$((i + 1))
      done
    else
      while [[ "$value" =~ \$\{([A-Z_]+)\} ]]; do
        local name="${BASH_REMATCH[1]}"; value="${value//\$\{$name\}/${!name:-}}"
      done
      printf '%s=%s\n' "$key" "$value"
    fi
  done < <(jq -r '.settings | to_entries[] | select(.key | startswith("$") | not) | [.key, .value] | @tsv' "$PROVIDER_JSON")
}

step_deploy() {
  log "4. Deploy the host"
  if [[ -n "$(state_get HOST_IMAGE)" && "$REDEPLOY" != "true" ]]; then info "image $(state_get HOST_IMAGE) deployed (skip; --redeploy to rebuild)"; return; fi
  for inputVar in $(jq -r '.inputs[].env' "$PROVIDER_JSON"); do export "$inputVar=$(state_get "INPUT_$inputVar")"; done

  local tag image registry
  tag="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)-$(date +%s)"
  registry="$(state_get REGISTRY_NAME)"
  image="$(state_get REGISTRY_LOGIN_SERVER)/federated-connector:$tag"
  info "building $image in the registry"
  az acr build --registry "$registry" --image "federated-connector:$tag" --file "$REPO_ROOT/Dockerfile" "$REPO_ROOT" --only-show-errors --output none

  local -a vars=()
  while IFS= read -r line; do vars+=("$line"); done < <(provider_env_vars)
  if [[ "$AUTH_MODE" == "EntraObo" ]]; then
    local app_id; app_id="$(state_get ENTRA_APP_ID)"
    vars+=(
      "AzureAd__Instance=https://login.microsoftonline.com/"
      "AzureAd__TenantId=$(state_get TENANT_ID)"
      "AzureAd__ClientId=$app_id"
      "AzureAd__ClientCredentials__0__SourceType=SignedAssertionFromManagedIdentity"
      "AzureAd__ClientCredentials__0__ManagedIdentityClientId=$(state_get IDENTITY_CLIENT_ID)"
      "Connector__AllowedTenants__0=$(state_get TENANT_ID)"
      "Connector__AcceptedAudiences__0=api://$app_id"
      "Connector__AcceptedAudiences__1=$app_id"
    )
    if [[ -n "$(state_get OAUTH_APPLICATION_ID_URI)" ]]; then vars+=("Connector__AcceptedAudiences__2=$(state_get OAUTH_APPLICATION_ID_URI)"); fi
  fi

  # One deployment sets the image, registry identity, probes AND every setting, so no revision starts half-configured.
  # The signing key secret already exists in Key Vault and is referenced, not passed again.
  local params; params="$(mktemp)"
  printf '%s\n' "${vars[@]}" | jq -R -s --arg image "$image" '{
      "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
      contentVersion: "1.0.0.0",
      parameters: {
        containerImage: { value: $image },
        extraEnv: { value: [ split("\n")[] | select(length > 0) | capture("^(?<name>[^=]+)=(?<value>.*)$") ] }
      } }' > "$params"
  az deployment group create --name "fc-core" --resource-group "$RESOURCE_GROUP" \
    --template-file "$DEPLOY_DIR/infra/core.bicep" \
    --parameters baseName="$BASE_NAME" location="$LOCATION" displayName="$DISPLAY_NAME" providerName="$PROVIDER_NAME" \
                 nameSalt="$(state_get NAME_SALT)" \
    --parameters "@$params" \
    --only-show-errors --output none || { rm -f "$params"; die "host deployment failed"; }
  rm -f "$params"
  state_set HOST_IMAGE "$image"
}

# ---------------------------------------------------------------- step 5: smoke test
step_smoke() {
  log "5. Smoke test"
  local base; base="$(state_get CONNECTOR_BASE_URL)"
  for _ in {1..30}; do curl -fsS "$base/healthz" >/dev/null 2>&1 && break; sleep 5; done
  curl -fsS "$base/healthz" >/dev/null || die "host is not healthy at $base/healthz"
  info "health check passed"
  if [[ -z "${CONNECTOR_TOKEN:-}" && "$AUTH_MODE" != "None" ]]; then
    warn "set CONNECTOR_TOKEN to a user token for this connector to run the MCP smoke test (initialize, tools/list, search, fetch)"
    return
  fi
  command -v dotnet >/dev/null || { warn "dotnet not installed; skipping MCP smoke test"; return; }
  dotnet run --project "$REPO_ROOT/tools/FederatedConnector.SmokeTest" -c Release -- --url "$(state_get CONNECTOR_MCP_URL)"
}

# ---------------------------------------------------------------- step 6
step_admin_route() {
  log "6A. Admin center route (manual)"
  cat <<EOF
    In the Microsoft 365 admin center: Copilot → Connectors → Gallery → Connect to MCP server, then enter:
      Display name:     $DISPLAY_NAME
      Base URL:         $(state_get CONNECTOR_MCP_URL)
      Registration ID:  $(state_get OAUTH_CONFIGURATION_ID)
    Then configure a staged rollout. Admin center: https://admin.microsoft.com/
EOF
}

step_package_route() {
  log "6B. App package route"
  local dir="$BUILD_DIR/package"
  mkdir -p "$dir/appPackage"
  cp "$DEPLOY_DIR/templates/atk/package.m365agents.yml" "$dir/m365agents.yml"
  cp "$DEPLOY_DIR/templates/color.png" "$DEPLOY_DIR/templates/outline.png" "$dir/appPackage/"
  if [[ "$AUTH_MODE" == "None" ]]; then
    # Public data: no OAuth registration exists, so the connector entry carries no authorization block.
    jq 'del(.agentConnectors[].toolSource.remoteMcpServer.authorization)' "$DEPLOY_DIR/templates/manifest.json" > "$dir/appPackage/manifest.json"
  else
    cp "$DEPLOY_DIR/templates/manifest.json" "$dir/appPackage/"
  fi
  local envfile; envfile="$(atk_env_file "$dir")"
  if $PUBLISH; then
    (cd "$dir" && atk publish --env "$ENV_NAME" --interactive false)
    info "submitted to the organisation catalogue; an admin must approve it"
  else
    (cd "$dir" && atk provision --env "$ENV_NAME" --interactive false)
    info "sideloaded for the signed-in Microsoft 365 account"
  fi
  if [[ -z "$(state_get TEAMS_APP_ID)" ]]; then state_set TEAMS_APP_ID "$(atk_read "$envfile" TEAMS_APP_ID)"; fi
}

# ---------------------------------------------------------------- destroy
destroy() {
  log "Destroying environment '$ENV_NAME'"
  confirm "Delete resource group $RESOURCE_GROUP, the Entra app and local state for '$ENV_NAME'?" || die "aborted"
  local hook; hook="$(pj '.hooks.provision // empty')"
  if [[ -n "$hook" ]]; then
    for inputVar in $(jq -r '.inputs[].env' "$PROVIDER_JSON"); do export "$inputVar=$(state_get "INPUT_$inputVar")"; done
    DESTROY=true IDENTITY_PRINCIPAL_ID="$(state_get IDENTITY_PRINCIPAL_ID)" bash "$PROVIDER_DIR/$hook" || warn "provider teardown hook failed"
  fi
  local app_id; app_id="$(state_get ENTRA_APP_ID)"
  if [[ -n "$app_id" ]]; then az ad app delete --id "$app_id" --only-show-errors || warn "could not delete Entra app $app_id"; fi
  az group delete --name "$RESOURCE_GROUP" --yes --no-wait --only-show-errors || warn "could not delete $RESOURCE_GROUP"
  cat <<EOF
    Manual clean-up (no public API):
      - Teams Developer Portal: delete the OAuth/SSO registration $(state_get OAUTH_CONFIGURATION_ID) and app $(state_get TEAMS_APP_ID)
      - Microsoft 365 admin center: remove the connector if it was added there
EOF
  rm -f "$STATE_FILE"; rm -rf "$BUILD_DIR"
}

# ---------------------------------------------------------------- main
if $DESTROY; then destroy; exit 0; fi
step_preflight
step_core
step_provider
case "$AUTH_MODE" in
  EntraObo) step_auth_entra ;;
  OAuthPassthrough) step_auth_passthrough ;;
  None) info "auth mode None: no registration needed" ;;
  *) die "unknown auth mode $AUTH_MODE in provider.json" ;;
esac
step_deploy
step_smoke
if [[ "$ROUTE" == "admin" ]]; then step_admin_route; else step_package_route; fi
log "Done. State: $STATE_FILE"
