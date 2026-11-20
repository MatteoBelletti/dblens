using Microsoft.EntityFrameworkCore;

namespace DemoShop.EfCore.Services;

public sealed record OrderLine(long OrderId, string Customer, int Items, decimal Total);

/// <summary>The monthly order report shown on the back-office dashboard.</summary>
public sealed class OrderReport(ShopContext db)
{
    public async Task<List<OrderLine>> ForMonthAsync(int year, int month, CancellationToken ct)
    {
        var from = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var orders = await db.Orders.Where(o => o.CreatedAt >= from && o.CreatedAt < from.AddMonths(1)).ToListAsync(ct);

        var lines = new List<OrderLine>();
        foreach (var order in orders)
        {
            var user = await db.Users.SingleAsync(u => u.Id == order.UserId, ct);
            var items = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync(ct);
            lines.Add(new OrderLine(order.Id, $"{user.FirstName} {user.LastName}", items.Sum(i => i.Quantity), order.Total));
        }

        return lines;
    }
}
