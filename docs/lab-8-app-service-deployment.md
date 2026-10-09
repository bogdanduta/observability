# Lab 8: Deploy both APIs to Azure App Service

## Goal

Deploy BookShop and Inventory as two .NET 10 web apps. Follow an order trace across the public HTTP boundary and verify that both services send telemetry to the Application Insights resource from Lab 5.

This lab uses one Linux **Free F1** App Service Plan for both web apps. F1 has no App Service compute charge, no SLA, and per-app limits including 60 CPU minutes per day and 1 GB storage. It is suitable for a short learning exercise, not production. Apps on Free can be unloaded and cold-start, so the first cross-service request may be slow. Application Insights ingestion and retention are separate and may still incur charges. See [App Service Linux pricing](https://azure.microsoft.com/pricing/details/app-service/linux/) and review your existing Application Insights cost settings.

These demo APIs have no authentication and will be publicly reachable. Use only synthetic data, do not add secrets or real customer information, and delete the apps after the lab. If you want to keep them online, add an access restriction or authentication before sharing the URL.

## Architecture

```mermaid
flowchart LR
    Client[Your browser or curl]
    BookShop[BookShop API\nAzure App Service]
    Inventory[Inventory API\nAzure App Service]
    Plan[One Linux F1 Free App Service Plan]
    AI[Application Insights\nfrom Lab 5]
    Client -->|HTTPS POST /orders| BookShop
    BookShop -->|HTTPS + traceparent| Inventory
    BookShop -->|OTel telemetry| AI
    Inventory -->|OTel telemetry| AI
    Plan -. hosts .-> BookShop
    Plan -. hosts .-> Inventory
```

## 1. Confirm the Azure target and cost

The active Azure subscription is **Visual Studio Professional Subscription**. The existing resource group is `rg-observability-labs`, in use for the Lab 5 Application Insights resource. Note the Application Insights connection string without putting it in a source file or Git.

Choose:

- **Resource group:** `rg-observability-labs`. Keep this group at cleanup because it contains monitoring resources used in Lab 9.
- **Region:** `westeurope` (West Europe).
- **Plan:** one Linux F1 Free plan shared by two apps. Free quotas are metered per app; check the subscription's quotas and regional availability.
- **Resource names:** `asp-observability-bd`, `bookshop-bd`, and `inventory-bd`. Web app names must be globally unique. If either name is already taken, retain the `-bd` suffix and append a short extra suffix, then use that hostname consistently below.

F1 currently has no App Service compute charge. Limits include 60 CPU minutes per day per app and 1 GB storage; there is no SLA. This is for a short learning deployment. Both demo APIs are public and unauthenticated, so use only synthetic data and delete the apps after the lab. Application Insights ingestion and retention are separate from the free compute plan and may cost money.

## 2. Create the plan and two web apps

In PowerShell, set these values to the names and region you selected. Replace every placeholder before running the commands. Do not paste a subscription ID or connection string into this tracked document.

```powershell
$resourceGroup = "rg-observability-labs"
$location = "westeurope"
$planName = "asp-observability-bd"
$bookShopApp = "bookshop-bd"
$inventoryApp = "inventory-bd"
```

Confirm the active subscription and resource group before the first create command:

```powershell
az account show --output table
az group show --name $resourceGroup --output table
```

Create one Linux Free plan, then two .NET 10 Linux apps on that plan:

```powershell
az appservice plan create `
  --name $planName `
  --resource-group $resourceGroup `
  --location $location `
  --sku F1 `
  --is-linux

az webapp create `
  --name $bookShopApp `
  --resource-group $resourceGroup `
  --plan $planName `
  --runtime "DOTNETCORE:10.0" `
  --os-type linux

az webapp create `
  --name $inventoryApp `
  --resource-group $resourceGroup `
  --plan $planName `
  --runtime "DOTNETCORE:10.0" `
  --os-type linux
```

Microsoft's current App Service CLI examples use the `DOTNETCORE:10.0` runtime identifier; the authenticated CLI runtime list reports it active for Linux. `az webapp deploy` deploys the ZIP artifacts. If the F1 tier is unavailable in West Europe or for this subscription, stop and inspect the available tiers and quotas before selecting any paid plan. [App Service .NET quickstart](https://learn.microsoft.com/en-us/azure/app-service/quickstart-dotnetcore) · [Azure CLI `az webapp`](https://learn.microsoft.com/en-us/cli/azure/webapp?view=azure-cli-latest)

## 3. Configure service names, dependency address, and telemetry

Use the App Service portal for each app: **Settings > Environment variables** (older portal wording may say **Configuration > Application settings**). Add these settings, then save and allow the apps to restart:

| App | Setting | Value |
| --- | --- | --- |
| BookShop | `OTEL_SERVICE_NAME` | `bookshop-api` |
| BookShop | `INVENTORY_BASE_URL` | `https://<inventory-app-name>.azurewebsites.net` |
| BookShop | `APPLICATIONINSIGHTS_CONNECTION_STRING` | Connection string from the Lab 5 Application Insights resource |
| Inventory | `OTEL_SERVICE_NAME` | `inventory-api` |
| Inventory | `APPLICATIONINSIGHTS_CONNECTION_STRING` | The same Lab 5 Application Insights connection string |

The connection string is an app setting (environment variable) read by the Azure Monitor OpenTelemetry Distro. Add it only in the Azure portal or through a secure secret workflow. Do not commit it, add it to `appsettings.json`, or put its literal value in shell history. App Service settings are encrypted at rest by the platform, but users with sufficient app configuration permissions can read them; restrict those permissions appropriately. [Configure App Service settings](https://learn.microsoft.com/en-us/azure/app-service/configure-common)

The different service names distinguish telemetry roles in Application Insights. The Inventory URL must be HTTPS so the public API-to-API call is encrypted. App Service supplies the server URL/port and HTTPS endpoint; the application code does not need hard-coded deployment ports.

## 4. Publish and deploy the applications

From the repository root, publish each API in Release mode. The output paths below are ignored build artifacts and should not be committed.

```powershell
dotnet publish src/Inventory.Api/Inventory.Api.csproj -c Release -o artifacts/publish/inventory
dotnet publish src/BookShop.Api/BookShop.Api.csproj -c Release -o artifacts/publish/bookshop

Compress-Archive -Path artifacts/publish/inventory/* -DestinationPath artifacts/inventory.zip -Force
Compress-Archive -Path artifacts/publish/bookshop/* -DestinationPath artifacts/bookshop.zip -Force
```

The ZIP must contain the published files at its root, including the app's `.dll` and `.deps.json`; do not zip the parent folder itself. Deploy each ZIP to its already-created web app:

```powershell
az webapp deploy --resource-group $resourceGroup --name $inventoryApp --src-path artifacts/inventory.zip --type zip
az webapp deploy --resource-group $resourceGroup --name $bookShopApp --src-path artifacts/bookshop.zip --type zip
```

ZIP deployment does not build source code in Azure. These commands deploy the prebuilt Release output. [Deploy files to App Service](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip?tabs=cli)

## 5. Generate and inspect a distributed trace

Check both health endpoints:

```powershell
Invoke-RestMethod "https://$bookShopApp.azurewebsites.net/health"
Invoke-RestMethod "https://$inventoryApp.azurewebsites.net/health"
```

Submit an order:

```powershell
Invoke-RestMethod -Method Post `
  -Uri "https://$bookShopApp.azurewebsites.net/orders" `
  -ContentType "application/json" `
  -Body '{"bookId":1,"quantity":2}'
```

Expect an order response. Then try the out-of-stock path (`bookId: 2`, `quantity: 5`) and the invalid-book path (`bookId: 99`). Allow a few minutes for stored telemetry to arrive.

In Application Insights, open **Application Map** or **Transaction search** and select the BookShop order. Look for BookShop as the caller, the HTTP dependency to Inventory, and Inventory's incoming request. Both apps must use the same Application Insights resource for one end-to-end view. The HTTP instrumentation propagates W3C trace context across the HTTPS call; different `OTEL_SERVICE_NAME` values label the services within the shared trace.

In the linked Log Analytics workspace, run:

```kusto
AppRequests
| where TimeGenerated > ago(30m)
| where AppRoleName in ("bookshop-api", "inventory-api")
| project TimeGenerated, AppRoleName, Name, Success, ResultCode, OperationId, ParentId, Id
| order by TimeGenerated desc
```

Find the order trace's `OperationId`, then query its dependencies:

```kusto
AppDependencies
| where TimeGenerated > ago(30m)
| where OperationId == "<paste-operation-id>"
| project TimeGenerated, AppRoleName, Name, Target, Success, ResultCode, OperationId, ParentId, Id
| order by TimeGenerated asc
```

If App Service doesn't appear in Application Map, first verify both apps' `APPLICATIONINSIGHTS_CONNECTION_STRING` and `OTEL_SERVICE_NAME`, verify that BookShop's `INVENTORY_BASE_URL` is the Inventory HTTPS hostname, and check **Monitoring > App Service logs** or **Log stream** for startup errors. App Service's deployment and application logs are separate from OpenTelemetry telemetry.

## 6. Clean up the apps when finished

When you have completed the investigation, delete the two web apps and then their shared App Service Plan. Keep the Lab 5 Application Insights and Log Analytics resources for Lab 9. Do not delete a shared plan if it hosts another app.

```powershell
az webapp delete --resource-group $resourceGroup --name $bookShopApp
az webapp delete --resource-group $resourceGroup --name $inventoryApp
az appservice plan delete --resource-group $resourceGroup --name $planName --yes
```

Confirm in the portal that the apps and plan are gone. The F1 plan has no compute charge, but removing it avoids leaving unused resources. Retained Application Insights data may continue to incur retention costs according to its configuration.

## What to notice

- App Service is the managed runtime host; the F1 plan applies per-app free quotas to both apps.
- The deployed topology produces the same parent/child trace structure as Lab 7, now across public HTTPS endpoints.
- Both services can export to one Application Insights resource and still be distinguished by service name.
- A successful deployment does not prove the telemetry configuration is correct; verify both the endpoint behavior and the correlated telemetry.
- Free compute quotas and telemetry ingestion/retention are separate concerns. Clean up the plan and review Monitor retention after the lab.

## Git checkpoint

After the deployment is running, verify correlated data in Application Insights and record the selected region/tier and observed trace in your private learning notes. Do not record the connection string. Then commit the completed lab notes:

```powershell
git add docs/lab-8-app-service-deployment.md docs/observability-learning-path.md
git commit -m "docs: add app service deployment lab"
git push
```

## Completion check

Both APIs are reachable over HTTPS, an order trace includes a request in each service and the HTTP dependency between them, telemetry is correlated in the shared Application Insights resource, and the App Service Plan is deleted after the exercise.
