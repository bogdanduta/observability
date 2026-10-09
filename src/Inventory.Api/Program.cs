using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "inventory-api";

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}
else
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: "1.0.0"))
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddConsoleExporter())
        .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddConsoleExporter())
        .WithLogging(logging => logging.AddConsoleExporter());
}

var app = builder.Build();
var stock = new Dictionary<int, int> { [1] = 12, [2] = 4, [3] = 0 };

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/inventory/{bookId:int}", (int bookId, int quantity) =>
{
    if (!stock.TryGetValue(bookId, out var available))
        return Results.NotFound(new { message = $"Book {bookId} is not stocked." });

    return Results.Ok(new { bookId, available = quantity > 0 && available >= quantity, quantityRequested = quantity, quantityInStock = available });
});

app.Run();
