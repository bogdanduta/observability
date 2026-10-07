using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BookCatalog>();
builder.Services.AddSingleton<OrderStore>();

// Lab 2: collect incoming ASP.NET Core request traces and print them locally.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "bookshop-api",
        serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/books", (BookCatalog catalog) => Results.Ok(catalog.All()));

app.MapGet("/books/{id:int}", (int id, BookCatalog catalog) =>
    catalog.Find(id) is { } book ? Results.Ok(book) : Results.NotFound());

app.MapPost("/orders", (CreateOrderRequest request, BookCatalog catalog, OrderStore orders) =>
{
    if (request.Quantity <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Quantity)] = ["Quantity must be greater than zero."]
        });
    }

    if (catalog.Find(request.BookId) is not { } book)
    {
        return Results.NotFound(new { message = $"Book {request.BookId} was not found." });
    }

    var order = orders.Create(book, request.Quantity);
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
