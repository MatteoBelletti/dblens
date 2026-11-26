using Microsoft.EntityFrameworkCore;

namespace DemoShop.EfCore.Api;

/// <summary>The public orders API, consumed by the mobile app and by partners.</summary>
public sealed record OrderDto(long Id, decimal Total, string Status, DateTimeOffset CreatedAt);

public sealed class OrdersApi(ShopContext db)
{
    /// <summary>GET /api/orders/{id}</summary>
    public async Task<OrderDto?> GetAsync(long id, CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new OrderDto(o.Id, o.Total, o.Status, o.CreatedAt))
            .SingleOrDefaultAsync(ct);
}
