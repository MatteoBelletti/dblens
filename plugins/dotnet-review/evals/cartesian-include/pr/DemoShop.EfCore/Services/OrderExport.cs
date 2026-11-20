using Microsoft.EntityFrameworkCore;

namespace DemoShop.EfCore.Services;

/// <summary>Exports a customer's full order history as JSON for the GDPR data-access request.</summary>
public sealed class OrderExport(ShopContext db)
{
    public Task<User?> LoadAsync(long userId, CancellationToken ct) =>
        db.Users
            .Include(u => u.Orders).ThenInclude(o => o.Items).ThenInclude(i => i.Product)
            .Include(u => u.Orders).ThenInclude(o => o.User)
            .SingleOrDefaultAsync(u => u.Id == userId, ct);

    public Task<List<Order>> RecentAsync(CancellationToken ct) =>
        db.Orders
            .Include(o => o.Items)
            .Include(o => o.User).ThenInclude(u => u.Orders)
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
}
