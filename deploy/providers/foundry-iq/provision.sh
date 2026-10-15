#!/usr/bin/env bash
# Foundry IQ provider provisioning (design §9 step 2). Called by register-connector.sh with these variables exported:
#   SEARCH_SERVICE, SEARCH_RESOURCE_GROUP, IDENTITY_PRINCIPAL_ID, and optionally
#   DEFINITION_READ_ROLE (Reader | SearchServiceContributor | None; default Reader).
# Idempotent: role assignments use deterministic names. With DESTROY=true, removes them again.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${SEARCH_SERVICE:?SEARCH_SERVICE is required}"
: "${SEARCH_RESOURCE_GROUP:?SEARCH_RESOURCE_GROUP is required}"
: "${IDENTITY_PRINCIPAL_ID:?IDENTITY_PRINCIPAL_ID is required}"

if [[ "${DESTROY:-false}" == "true" ]]; then
  echo "  foundry-iq: removing the connector identity's role assignments on ${SEARCH_SERVICE}"
  scope="$(az search service show --name "$SEARCH_SERVICE" --resource-group "$SEARCH_RESOURCE_GROUP" --query id -o tsv --only-show-errors)"
  az role assignment delete --assignee "$IDENTITY_PRINCIPAL_ID" --scope "$scope" --only-show-errors --output none
  exit 0
fi

echo "  foundry-iq: granting Search Index Data Reader (+ ${DEFINITION_READ_ROLE:-Reader} for definitions) on ${SEARCH_SERVICE}"
az deployment group create \
  --name "fc-foundry-iq-roles" \
  --resource-group "$SEARCH_RESOURCE_GROUP" \
  --template-file "$here/infra.bicep" \
  --parameters searchServiceName="$SEARCH_SERVICE" \
               identityPrincipalId="$IDENTITY_PRINCIPAL_ID" \
               definitionReadRole="${DEFINITION_READ_ROLE:-Reader}" \
  --only-show-errors --output none
