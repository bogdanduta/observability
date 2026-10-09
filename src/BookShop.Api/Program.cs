using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BookCatalog>();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddHttpClient<InventoryClient>(client =>
{
    var baseUrl = builder.Configuration["INVENTORY_BASE_URL"] ?? "http://localhost:5081";
    client.BaseAddress = new Uri(baseUrl);
});

// Use Azure Monitor when a connection string is supplied; otherwise keep the
// local console exporters so Labs 2–4 remain runnable without Azure resources.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
    builder.Services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
        tracing.AddSource(BookShopTelemetry.SourceName));
    builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) =>
        metrics.AddMeter(BookShopMetrics.MeterName));
}
else
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? "bookshop-api",
            serviceVersion: "1.0.0"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(BookShopTelemetry.SourceName)
            .AddConsoleExporter())
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(BookShopMetrics.MeterName)
            .AddConsoleExporter((_, metricReaderOptions) =>
                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000))
        .WithLogging(logging => logging
            .AddConsoleExporter());
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/books", (BookCatalog catalog) => Results.Ok(catalog.All()));

app.MapGet("/books/{id:int}", (int id, BookCatalog catalog) =>
    catalog.Find(id) is { } book ? Results.Ok(book) : Results.NotFound());

app.MapPost("/orders", async (CreateOrderRequest request, BookCatalog catalog, OrderStore orders, InventoryClient inventory, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    using var activity = BookShopTelemetry.ActivitySource.StartActivity("orders.create");
    activity?.SetTag("bookshop.order.quantity", request.Quantity);

    if (request.Quantity <= 0)
    {
        activity?.SetTag("bookshop.order.result", "rejected_invalid_quantity");
        BookShopMetrics.RecordOrderAttempt("rejected_invalid_quantity");
        logger.LogWarning("Order rejected because quantity must be positive. Quantity: {Quantity}", request.Quantity);
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Quantity)] = ["Quantity must be greater than zero."]
        });
    }

    activity?.SetTag("bookshop.book.id", request.BookId);
    if (catalog.Find(request.BookId) is not { } book)
    {
        activity?.SetTag("bookshop.order.result", "rejected_book_not_found");
        BookShopMetrics.RecordOrderAttempt("rejected_book_not_found");
        logger.LogWarning("Order rejected because book {BookId} was not found", request.BookId);
        return Results.NotFound(new { message = $"Book {request.BookId} was not found." });
    }

    bool available;
    try
    {
        available = await inventory.IsAvailableAsync(request.BookId, request.Quantity, cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "Inventory service unavailable");
        logger.LogError(exception, "Inventory check failed for book {BookId}", request.BookId);
        return Results.Problem("Inventory service is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!available)
    {
        activity?.SetTag("bookshop.order.result", "rejected_out_of_stock");
        BookShopMetrics.RecordOrderAttempt("rejected_out_of_stock");
        logger.LogWarning("Order rejected because book {BookId} has insufficient stock", request.BookId);
        return Results.Conflict(new { message = "Insufficient stock." });
    }

    var order = orders.Create(book, request.Quantity);
    activity?.SetTag("bookshop.order.result", "created");
    activity?.AddEvent(new ActivityEvent("order.created"));
    BookShopMetrics.RecordOrderAttempt("created");
    BookShopMetrics.RecordOrderValue((double)order.Total);
    logger.LogInformation(
        "Order {OrderId} created for book {BookId}. Quantity: {Quantity}; total: {OrderTotal}",
        order.Id, order.BookId, order.Quantity, order.Total);
    return Results.Created($"/orders/{order.Id}", order);
});

app.MapGet("/orders/{id:guid}", (Guid id, OrderStore orders) =>
    orders.Find(id) is { } order ? Results.Ok(order) : Results.NotFound());

// Lab-only endpoints make failures and latency reproducible. Remove or protect them in a real app.
app.MapGet("/lab/failure", (Func<IResult>)(() =>
    throw new InvalidOperationException("Intentional Lab 1 failure.")));

app.MapGet("/lab/slow", async (CancellationToken cancellationToken) =>
{
    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    return Results.Ok(new { message = "Slow response completed." });
});

app.Run();

public static class BookShopTelemetry
{
    public const string SourceName = "BookShop.Api";
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
}

public sealed class InventoryClient(HttpClient httpClient)
{
    public async Task<bool> IsAvailableAsync(int bookId, int quantity, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/inventory/{bookId}?quantity={quantity}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<InventoryCheckResponse>(cancellationToken);
        return result?.Available == true;
    }

    private sealed record InventoryCheckResponse(bool Available);
}

public static class BookShopMetrics
{
    public const string MeterName = "BookShop.Api";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> OrderAttempts = Meter.CreateCounter<long>(
        "bookshop.orders.attempts", "{attempt}", "Number of order attempts grouped by outcome.");
    private static readonly Histogram<double> OrderValues = Meter.CreateHistogram<double>(
        "bookshop.order.value", "unit", "Value of successfully created orders in demo price units.");

    public static void RecordOrderAttempt(string result) =>
        OrderAttempts.Add(1, new KeyValuePair<string, object?>("bookshop.order.result", result));

    public static void RecordOrderValue(double value) => OrderValues.Record(value);
}

public sealed record Book(int Id, string Title, decimal Price);
public sealed record CreateOrderRequest(int BookId, int Quantity);
public sealed record BookOrder(Guid Id, int BookId, string BookTitle, int Quantity, decimal Total, DateTimeOffset CreatedAt);

public sealed class BookCatalog
{
    private readonly Book[] _books =
    [
        new(1, "The Pragmatic Programmer", 42.50m),
        new(2, "Clean Code", 39.00m),
        new(3, "Designing Data-Intensive Applications", 49.95m)
    ];

    public IReadOnlyList<Book> All() => _books;
    public Book? Find(int id) => _books.FirstOrDefault(book => book.Id == id);
}

public sealed class OrderStore
{
    private readonly Dictionary<Guid, BookOrder> _orders = [];

    public BookOrder Create(Book book, int quantity)
    {
        var order = new BookOrder(Guid.NewGuid(), book.Id, book.Title, quantity,
            book.Price * quantity, DateTimeOffset.UtcNow);
        _orders.Add(order.Id, order);
        return order;
    }

    public BookOrder? Find(Guid id) => _orders.GetValueOrDefault(id);
}
