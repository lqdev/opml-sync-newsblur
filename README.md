# OPML Sync for NewsBlur

A small Azure Functions bridge that subscribes to OPML documents through WebSub and reconciles their feed outlines with a NewsBlur account. The OPML publisher remains the source of truth for feeds managed by this bridge; NewsBlur continues fetching and updating each feed normally.

The bridge understands OPML `type="include"` outlines, so a root list can include collection OPML documents. It subscribes to both the root and every included document, then rebuilds the desired feed set when any tracked document changes.

## What it changes in NewsBlur

- New OPML feeds are added to NewsBlur and placed in their OPML folder.
- Feeds already in NewsBlur are left alone until this bridge has recorded ownership of them.
- Feeds removed from an OPML document are removed only if this bridge added and manages them.
- If an included list is used by multiple configured roots, a feed remains managed until all roots stop including it.
- The default deployment is **dry-run**. It subscribes to WebSub topics and logs the planned adds/removals but does not log in to NewsBlur or change subscriptions.

This ownership rule intentionally protects subscriptions that existed before the bridge started. It does not treat the OPML file as authority to erase every unrelated NewsBlur subscription.

## Architecture

1. The anonymous `/callback` endpoint handles WebSub verification requests and signed content notifications.
2. Notifications are HMAC-SHA256 verified with the per-topic secret, stored in private Blob Storage, and queued.
3. A queue-triggered function fetches the latest root OPML, follows allowed includes, and reconciles the complete result.
4. A six-hour timer renews subscriptions before expiry and removes old notification payloads.
5. A function-key-protected `/admin/bootstrap` endpoint performs the initial root reconciliation.

The public Function App is HTTPS-only. Its callback is anonymous because WebSub hubs must call it; POST notifications are authenticated by the WebSub signature. Bootstrap requires a Function key. Storage and Key Vault use a user-assigned managed identity and RBAC, not storage keys or credentials embedded in source.

## Azure resources

`infra/main.bicep` provisions:

- Azure Functions **Flex Consumption (FC1)** with a single 2-GB maximum instance by default.
- Private Blob Storage containers for deployment packages and recent OPML payloads.
- Azure Tables and Queues on the same Storage account.
- A user-assigned managed identity with narrowly scoped Blob, Queue, Table, Key Vault Secrets User, and Application Insights roles.
- A standard Key Vault with versionless references for the two NewsBlur credentials. No secret values are created by the template.
- Log Analytics and workspace-based Application Insights.

FC1 is a consumption plan, not a promise that the whole deployment is free. Any Functions free grant depends on subscription eligibility and usage; Storage, Key Vault, and monitoring can incur charges. Check current Azure pricing and your subscription before deploying.

## Prerequisites

- .NET 10 SDK.
- Azure Developer CLI (`azd`) and an authenticated Azure account, or Azure CLI for provisioning.
- Azure Functions Core Tools for direct `func azure functionapp publish` deployments.
- An existing WebSub hub URL, for example `https://<hub-app>.azurewebsites.net/`.
- An Azure subscription that can create a Flex Consumption Function App in the selected region.
- A NewsBlur account before enabling live reconciliation.

## Deploy with Azure Developer CLI

The hub must be deployed first. Its URL is public configuration, not a credential.

```bash
azd auth login
azd env new opmlsync
azd env set AZURE_LOCATION eastus
azd env set AZURE_SUBSCRIPTION_ID "$(az account show --query id -o tsv)"
azd env set WEBSUB_HUB_URL https://YOUR-HUB-APP.azurewebsites.net/
azd env set SYNC_DRY_RUN true
azd up
```

`azd up` provisions the Bicep infrastructure and publishes the Function App. The deployment outputs include `SERVICE_SYNC_NAME`, `CALLBACK_URL`, `KEY_VAULT_NAME`, and `STORAGE_ACCOUNT_NAME`.

## Provision with Azure CLI

The same Bicep template can be deployed directly with Azure CLI. This provisions the resources; use Azure Functions Core Tools to publish the function package:

```bash
WEBSUB_HUB_URL="https://YOUR-HUB-APP.azurewebsites.net/"
az group create --name rg-opml-sync-newsblur --location eastus
az deployment group what-if \
  --resource-group rg-opml-sync-newsblur \
  --name opml-sync-newsblur \
  --template-file infra/main.bicep \
  --parameters environmentName=opmlsync location=eastus \
    webSubHubUrl="$WEBSUB_HUB_URL" syncDryRun=true
az deployment group create \
  --resource-group rg-opml-sync-newsblur \
  --name opml-sync-newsblur \
  --template-file infra/main.bicep \
  --parameters environmentName=opmlsync location=eastus \
    webSubHubUrl="$WEBSUB_HUB_URL" syncDryRun=true

FUNCTION_APP_NAME=$(az deployment group show \
  --resource-group rg-opml-sync-newsblur \
  --name opml-sync-newsblur \
  --query properties.outputs.SERVICE_SYNC_NAME.value -o tsv)
KEY_VAULT_NAME=$(az deployment group show \
  --resource-group rg-opml-sync-newsblur \
  --name opml-sync-newsblur \
  --query properties.outputs.KEY_VAULT_NAME.value -o tsv)
func azure functionapp publish "$FUNCTION_APP_NAME"
```

Set `WEBSUB_HUB_URL` and `SYNC_DRY_RUN` when using `azd`; the parameter file maps those environment values into Bicep.

The default root is `https://lqdev.me/.well-known/following.opml`. To follow different lists, override `rootTopics` and `allowedIncludeHosts` in the Bicep parameters. Every root and included OPML host must be in the allowlist.

## Add credentials and enable syncing

The deployment creates the Key Vault but never creates or prints credentials. Set the credentials directly in that vault. In Bash:

```bash
read -r -s -p "NewsBlur username: " NEWSBLUR_USERNAME; echo
read -r -s -p "NewsBlur password: " NEWSBLUR_PASSWORD; echo
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name newsblur-username --value "$NEWSBLUR_USERNAME" --output none
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name newsblur-password --value "$NEWSBLUR_PASSWORD" --output none
unset NEWSBLUR_USERNAME NEWSBLUR_PASSWORD
```

First leave `SYNC_DRY_RUN=true`, invoke bootstrap, and inspect Application Insights to confirm the root and includes parse as expected. The bootstrap endpoint is protected by a Function key; use the Function App's **Bootstrap** function key as the `x-functions-key` header when calling `POST https://<app>.azurewebsites.net/admin/bootstrap`.

After confirming the dry run and Key Vault references are healthy, set and provision:

```bash
azd env set SYNC_DRY_RUN false
azd provision --no-prompt
```

Then call bootstrap again. Do not put credentials in `local.settings.json.example`, Bicep parameters, GitHub variables, or committed files.

## CI/CD with GitHub OIDC

`.github/workflows/azure-dev.yml` tests, provisions, and deploys on pushes to `main` and on manual runs. Configure these **repository variables**:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_ENV_NAME`
- `AZURE_LOCATION`
- `WEBSUB_HUB_URL`
- Optional `SYNC_DRY_RUN` (`true` by default)

Configure a federated identity credential on the Azure app registration for this repository and the `main` branch, then grant it permission to deploy the resource group. GitHub Actions uses OIDC; no publish profile or Azure client secret is needed. The workflow never receives NewsBlur credentials; those remain in Key Vault.

## Local development

Copy the example settings to the ignored local file, then use Azurite for local Storage:

```bash
cp src/OpmlSyncNewsBlur/local.settings.json.example src/OpmlSyncNewsBlur/local.settings.json
dotnet test WebSubSync.slnx --nologo
dotnet build WebSubSync.slnx --nologo
func start --script-root src/OpmlSyncNewsBlur
```

The sample uses loopback HTTP for the local callback and hub. For a live WebSub callback, the app must be reachable over HTTPS. Keep `SYNC_DRY_RUN=true` until the local configuration and output are reviewed.

## Operational notes

- The queue trigger processes one reconciliation at a time; the Flex app defaults to one instance to avoid concurrent subscription mutations.
- Notification bodies are limited to 5 MiB and old payload blobs are removed after seven days.
- OPML includes must use HTTPS and a host in `WEBSUB_ALLOWED_INCLUDE_HOSTS`. Feed addresses must use HTTP or HTTPS and cannot embed credentials.
- OPML fetches reject private, loopback, and link-local DNS results and do not follow redirects.
- If a root list changes its includes, no-longer-referenced child topics are unsubscribed.
- If Azure reports a Key Vault reference unresolved, verify the secret names and the Function App's managed-identity role assignment before disabling dry-run.
