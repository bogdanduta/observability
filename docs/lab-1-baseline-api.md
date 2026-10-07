# Lab 1 — Create and explore the baseline API

## Goal

Run a small ASP.NET Core API locally and establish its behavior before adding telemetry. This gives us a baseline: when later labs show requests, failures, latency, and dependencies, we can connect the signals to work we understand.

## What we built

`src/BookShop.Api` is a .NET 10 minimal API with an in-memory catalog and order store. It has no database or cloud dependency. `GET /health` gives a simple health response; book and order routes cover successful reads and writes; `/lab/failure` and `/lab/slow` create repeatable failure and latency examples.

The app is intentionally one process for now. The catalog and order store are singleton in-memory services, so data disappears when the process stops. The lab endpoints are intentionally unsafe for production and must not be deployed publicly as-is.

## Run it

From the repository root:

```powershell
dotnet run --project src/BookShop.Api --urls http://localhost:5080
```

Leave the process running. In a second PowerShell window, call each route:

```powershell
Invoke-RestMethod http://localhost:5080/health
Invoke-RestMethod http://localhost:5080/books
Invoke-RestMethod http://localhost:5080/books/1
Invoke-RestMethod http://localhost:5080/books/999
```

Create an order and inspect the returned ID and total:

```powershell
$order = Invoke-RestMethod -Method Post -Uri http://localhost:5080/orders `
  -ContentType 'application/json' -Body '{"bookId":1,"quantity":2}'
$order
Invoke-RestMethod "http://localhost:5080/orders/$($order.id)"
```

The order total should be twice the selected book's price. A nonexistent book should return 404, and a nonpositive quantity should return a validation response. These are useful contrasts: an expected client error is different from an unhandled server exception.

Exercise the lab endpoints:

```powershell
Measure-Command { Invoke-RestMethod http://localhost:5080/lab/slow }
Invoke-RestMethod http://localhost:5080/lab/failure
```

The slow route should take about two seconds. The failure route intentionally returns an HTTP 500 and prints an exception in the server console. PowerShell may display an error for this expected failure; that is the experiment.

## Observe the baseline

ASP.NET Core already writes basic hosting/request diagnostics to the console. At this stage there is no Azure Monitor exporter and no OpenTelemetry setup in the app. The console output is not yet our structured, correlated telemetry experience. In Lab 2, we will add OpenTelemetry and compare what becomes available.

## Git checkpoint

After the run-through, commit the implementation and lab notes:

```powershell
git add .gitignore src/BookShop.Api docs/lab-1-baseline-api.md
git commit -m "feat: add baseline bookshop api"
```

## Questions to answer in your notes

1. Which routes returned 2xx, 404, validation failure, and 500 responses?
2. Where did the failure appear: HTTP response, app console, or both?
3. What is lost when the app restarts?
4. Why would it be a mistake to attach book title, order ID, or another unbounded value as a metric dimension later?

## Completion check

The API runs locally, you have exercised a normal request, an expected client error, an intentional server failure, and a slow request, and you have committed the baseline.
