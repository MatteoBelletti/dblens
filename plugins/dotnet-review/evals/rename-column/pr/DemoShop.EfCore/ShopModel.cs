using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DemoShop.EfCore;

public sealed class User
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Phone { get; set; }
#if SHOP_STAGE1 || SHOP_STAGE2
    public DateOnly? DateOfBirth { get; set; }
#endif
    public DateTimeOffset CreatedAt { get; set; }
    public PostalAddress? HomeAddress { get; set; }
    public List<Order> Orders { get; } = [];
}

/// <summary>Owned, stored in the users table.</summary>
public sealed class PostalAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

public sealed class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    public Category? Parent { get; set; }
    public List<Category> Children { get; } = [];
}

public sealed class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public long? CategoryId { get; set; }
    public Category? Category { get; set; }
}

public sealed class Order
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Total { get; set; }
    public string State { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<OrderItem> Items { get; } = [];
}

public sealed class OrderItem
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>Table-per-hierarchy: one payments table with a kind discriminator.</summary>
public abstract class Payment
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public decimal Amount { get; set; }
}

public sealed class CardPayment : Payment
{
    public string? CardLast4 { get; set; }
}

public sealed class BankTransfer : Payment
{
    public string? Iban { get; set; }
}

/// <summary>Keyless and mapped to a view: outside what dblens reads, so it must be named in a caveat.</summary>
public sealed class OrderSummary
{
    public long OrderId { get; set; }
    public decimal Total { get; set; }
}

public abstract class ShopContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
#if !SHOP_STAGE1
    public DbSet<Payment> Payments => Set<Payment>();
#endif
    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("shop");

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.Email).HasMaxLength(320);
            b.Property(u => u.FirstName).HasMaxLength(100);
            b.Property(u => u.LastName).HasMaxLength(100);
            b.Property(u => u.Phone).HasMaxLength(30);
            b.HasIndex(u => u.Email).IsUnique();
            b.OwnsOne(u => u.HomeAddress, a =>
            {
                a.Property(x => x.Street).HasColumnName("home_street").HasMaxLength(200);
                a.Property(x => x.City).HasColumnName("home_city").HasMaxLength(100);
            });
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("categories");
            b.Property(c => c.Name).HasMaxLength(100);
            b.HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.Property(p => p.Name).HasMaxLength(200);
            b.Property(p => p.Sku).HasMaxLength(20).IsUnicode(false);
            b.Property(p => p.Price).HasPrecision(10, 2);
#if !SHOP_STAGE1
            b.HasIndex(p => p.Sku).HasDatabaseName("ix_products_sku");
#endif
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.Property(o => o.Total).HasPrecision(10, 2);
            b.Property(o => o.State).HasMaxLength(20);
            b.HasIndex(o => o.State).HasDatabaseName("ix_orders_state");
            b.HasOne(o => o.User).WithMany(u => u.Orders).HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.ToTable("order_items");
            b.Property(i => i.UnitPrice).HasPrecision(10, 2);
            b.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId);
            b.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

#if !SHOP_STAGE1
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.Property(p => p.Amount).HasPrecision(10, 2);
            b.HasDiscriminator<string>("kind").HasValue<CardPayment>("card").HasValue<BankTransfer>("bank");
            b.Property("kind").HasMaxLength(10);
        });
        modelBuilder.Entity<CardPayment>().Property(p => p.CardLast4).HasMaxLength(4);
        modelBuilder.Entity<BankTransfer>().Property(p => p.Iban).HasMaxLength(34);
#endif

        modelBuilder.Entity<OrderSummary>(b => b.HasNoKey().ToView("order_summaries"));

        // snake_case columns, as the demo schema names them.
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => !e.IsOwned()))
            foreach (var property in entity.GetProperties())
                if (property.GetColumnName() == property.Name)
                    property.SetColumnName(SnakeCase(property.Name));
    }

    private static string SnakeCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

public sealed class ShopPostgresContext(DbContextOptions<ShopPostgresContext> options) : ShopContext(options);

public sealed class ShopSqlServerContext(DbContextOptions<ShopSqlServerContext> options) : ShopContext(options);

/// <summary>
/// Design-time only: `dotnet ef migrations add` and `migrations script` need no database, just the
/// provider. The history table lives in shop, beside the tables, as in the demo database.
/// </summary>
public sealed class ShopPostgresContextFactory : IDesignTimeDbContextFactory<ShopPostgresContext>
{
    public ShopPostgresContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShopPostgresContext>().UseNpgsql("Host=localhost;Database=shop", o => o.MigrationsHistoryTable("__EFMigrationsHistory", "shop")).Options);
}

public sealed class ShopSqlServerContextFactory : IDesignTimeDbContextFactory<ShopSqlServerContext>
{
    public ShopSqlServerContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShopSqlServerContext>().UseSqlServer("Server=localhost;Database=shop", o => o.MigrationsHistoryTable("__EFMigrationsHistory", "shop")).Options);
}
