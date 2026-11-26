var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("payments", client =>
    client.BaseAddress = new Uri(builder.Configuration["Payments:BaseUrl"]!));
builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.MapGet("/orders/{id}", (int id, HttpContext http, OrderStore store) =>
{
    var order = store.Find(id);
    return order is not null && order.Customer == http.Request.Headers["X-Customer"].ToString()
        ? Results.Ok(order)
        : Results.NotFound();
});

app.MapPost("/orders", (OrderRequest request, OrderStore store) =>
{
    var order = store.Add(request.Customer, request.Total);
    return Results.Created($"/orders/{order.Id}", order);
});

app.MapDelete("/orders/{id}", (int id, OrderStore store) =>
{
    var order = store.Find(id);
    if (order is null) return Results.NotFound();
    order.Status = "Cancelled";
    return Results.NoContent();
});

app.Run();

public sealed record OrderRequest(string Customer, decimal Total);

public sealed class Order
{
    public int Id { get; set; }

    public string Customer { get; set; } = "";

    public decimal Total { get; set; }

    public string Status { get; set; } = "Open";
}

public sealed class OrderStore
{
    private readonly List<Order> _orders = [];

    public Order? Find(int id) => _orders.FirstOrDefault(o => o.Id == id);

    public Order Add(string customer, decimal total)
    {
        var order = new Order { Id = _orders.Count + 1, Customer = customer, Total = total };
        _orders.Add(order);
        return order;
    }
}
