#!/usr/bin/env bash
# Foundry IQ provider provisioning (design §9 step 2). Called by register-connector.sh with these variables exported:
#   SEARCH_SERVICE, SEARCH_RESOURCE_GROUP, IDENTITY_PRINCIPAL_ID, and optionally GRANT_DEFINITION_READ (true|false).
# Idempotent: role assignments use deterministic names.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${SEARCH_SERVICE:?SEARCH_SERVICE is required}"
: "${SEARCH_RESOURCE_GROUP:?SEARCH_RESOURCE_GROUP is required}"
: "${IDENTITY_PRINCIPAL_ID:?IDENTITY_PRINCIPAL_ID is required}"

if [[ "${DESTROY:-false}" == "true" ]]; then
  echo "  foundry-iq: role assignments on ${SEARCH_SERVICE} are removed with the managed identity; nothing else to undo."
  exit 0
fi

echo "  foundry-iq: granting Search Index Data Reader on ${SEARCH_SERVICE} to the connector identity"
az deployment group create \
  --name "fc-foundry-iq-roles" \
  --resource-group "$SEARCH_RESOURCE_GROUP" \
  --template-file "$here/infra.bicep" \
  --parameters searchServiceName="$SEARCH_SERVICE" \
               identityPrincipalId="$IDENTITY_PRINCIPAL_ID" \
               grantDefinitionRead="${GRANT_DEFINITION_READ:-true}" \
  --only-show-errors --output none
