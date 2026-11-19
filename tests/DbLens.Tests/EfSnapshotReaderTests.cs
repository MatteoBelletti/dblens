using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// The snapshot format changes between EF Core versions, so the reader is tested on snapshots the
/// real `dotnet ef` of each major wrote (scripts/generate-ef-fixtures.sh), not on hand-written ones.
/// </summary>
public sealed class EfSnapshotReaderTests
{
    public static TheoryData<int, string> Fixtures => new()
    {
        { 8, "PostgreSql" }, { 8, "SqlServer" },
        { 9, "PostgreSql" }, { 9, "SqlServer" },
        { 10, "PostgreSql" }, { 10, "SqlServer" },
    };

    private static string FixtureDirectory(int major, string provider) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", $"ef{major}", provider);

    private static EfModel ReadFixture(int major, string provider) => SnapshotReader.Read(FixtureDirectory(major, provider));

    private static EfEntity Entity(EfModel model, string name) => model.Entities.Single(e => e.Name == $"DemoShop.EfCore.{name}");

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ReadsTheModelHeader_AndTheMigrationsOfItsContext(int major, string provider)
    {
        var model = ReadFixture(major, provider);

        model.ContextName.Should().Be(provider == "PostgreSql" ? "ShopPostgresContext" : "ShopSqlServerContext");
        model.ProductVersion.Should().StartWith($"{major}.");
        model.DefaultSchema.Should().Be("shop");
        model.MigrationIds.Should().HaveCount(2);
        model.MigrationIds[0].Should().EndWith("_Init");
        model.MigrationIds[1].Should().EndWith("_AddPayments");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ReadsTablesColumnsKeysAndIndexes(int major, string provider)
    {
        var model = ReadFixture(major, provider);

        model.Entities.Select(e => e.Table).Distinct().Should().BeEquivalentTo(
            ["users", "categories", "products", "orders", "order_items", "payments"]);

        var user = Entity(model, "User");
        user.Schema.Should().Be("shop");
        user.Key.Should().Equal("Id");
        var email = user.Properties.Single(p => p.Name == "Email");
        email.Column.Should().Be("email");
        email.IsNullable.Should().BeFalse();
        email.MaxLength.Should().Be(320);
        email.ColumnType.Should().Be(provider == "PostgreSql" ? "character varying(320)" : "nvarchar(320)");
        user.Properties.Single(p => p.Name == "Phone").IsNullable.Should().BeTrue();
        user.Properties.Single(p => p.Name == "DateOfBirth").ClrType.Should().Be("DateOnly?");
        user.Indexes.Should().ContainSingle(i => i.Properties.SequenceEqual(new[] { "Email" }) && i.IsUnique);

        var sku = Entity(model, "Product").Properties.Single(p => p.Name == "Sku");
        sku.IsUnicode.Should().BeFalse();
        if (provider == "SqlServer") sku.ColumnType.Should().Be("varchar(20)");

        Entity(model, "Product").Properties.Single(p => p.Name == "Price")
            .Should().Match<EfProperty>(p => p.Precision == 10 && p.Scale == 2);

        Entity(model, "Order").Indexes.Should().Contain(i => i.Name == "ix_orders_status" && !i.IsUnique)
            .And.Contain(i => i.Name == null && i.Properties.SequenceEqual(new[] { "UserId" }));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ReadsRelationshipsOnTheirDependentSide(int major, string provider)
    {
        var model = ReadFixture(major, provider);

        Entity(model, "Order").ForeignKeys.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EfForeignKey("DemoShop.EfCore.User", ["UserId"], [], "Cascade"));
        Entity(model, "OrderItem").ForeignKeys.Select(f => f.Principal)
            .Should().BeEquivalentTo(["DemoShop.EfCore.Order", "DemoShop.EfCore.Product"]);
        Entity(model, "Category").ForeignKeys.Should().ContainSingle()
            .Which.Should().Match<EfForeignKey>(f => f.Principal == "DemoShop.EfCore.Category" && f.OnDelete == "Restrict");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FoldsOwnedTypesAndSubtypesIntoTheirTable_WithNullableColumns(int major, string provider)
    {
        var model = ReadFixture(major, provider);

        var address = Entity(model, "PostalAddress");
        address.Table.Should().Be("users");
        address.SharesTableWith.Should().Be("DemoShop.EfCore.User");
        address.Properties.Select(p => p.Column).Should().BeEquivalentTo(["home_street", "home_city"],
            "the owned key is the owner's id column, already the user's");
        address.Properties.Should().OnlyContain(p => p.IsNullable, "an optional owned type stores NULL in every column when absent");

        var card = Entity(model, "CardPayment");
        card.Table.Should().Be("payments");
        card.SharesTableWith.Should().Be("DemoShop.EfCore.Payment");
        card.Key.Should().Equal("Id");
        card.Properties.Single().Should().Match<EfProperty>(p => p.Column == "card_last4" && p.IsNullable);

        Entity(model, "Payment").Properties.Single(p => p.Name == "kind").IsNullable.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NamesWhatItDoesNotCompare(int major, string provider)
    {
        var model = ReadFixture(major, provider);

        model.Entities.Should().NotContain(e => e.Name.EndsWith("OrderSummary"));
        model.Unsupported.Select(u => u.Reason).Should().ContainSingle(u => u.Contains("OrderSummary") && u.Contains("view"));
    }

    // ── Constructs the demo model does not use ─────────────────────────────────

    private static EfModel ParseBody(string body, params string[] designers) => SnapshotReader.Parse(
        $$"""
        [DbContext(typeof(TestContext))]
        partial class TestContextModelSnapshot : ModelSnapshot
        {
            protected override void BuildModel(ModelBuilder modelBuilder)
            {
                {{body}}
            }
        }
        """, designers);

    [Fact]
    public void TableMappingsItDoesNotCompare_AreEachNamed()
    {
        var model = ParseBody("""
            modelBuilder.Entity("N.Animal", b => { b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("animals"); b.UseTptMappingStrategy(); });
            modelBuilder.Entity("N.Dog", b => { b.HasBaseType("N.Animal"); b.ToTable("dogs"); });
            modelBuilder.Entity("N.Shape", b => { b.Property<int>("Id"); b.HasKey("Id"); b.ToTable((string)null); b.UseTpcMappingStrategy(); });
            modelBuilder.Entity("N.Circle", b => { b.HasBaseType("N.Shape"); b.ToTable("circles"); });
            modelBuilder.Entity("N.Report", b => { b.Property<int>("Total"); b.ToTable("reports"); });
            modelBuilder.Entity("N.Head", b => { b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("people"); });
            modelBuilder.Entity("N.Body", b => { b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("people"); });
            modelBuilder.Entity("N.Customer", b =>
            {
                b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("customers");
                b.OwnsOne("N.Preferences", "Preferences", b1 => { b1.Property<int>("CustomerId"); b1.HasKey("CustomerId"); b1.ToTable("customers"); b1.ToJson("preferences"); });
            });
            """);

        model.Entities.Select(e => e.Name).Should().BeEquivalentTo(["N.Animal", "N.Customer"]);
        model.Unsupported.Select(u => u.Reason).Should().Contain(u => u.Contains("N.Dog") && u.Contains("table-per-type"))
            .And.Contain(u => u.Contains("N.Circle") && u.Contains("table-per-concrete-type"))
            .And.Contain(u => u.Contains("N.Report") && u.Contains("keyless"))
            .And.Contain(u => u.Contains("N.Head and N.Body") && u.Contains("table splitting"))
            .And.Contain(u => u.Contains("N.Preferences") && u.Contains("JSON"));
    }

    [Fact]
    public void ATemporalTablesLambdaOnlyToTable_KeepsItsName()
    {
        var model = ParseBody("""
            modelBuilder.Entity("N.Employee", b =>
            {
                b.Property<int>("Id"); b.HasKey("Id");
                b.ToTable("Employees");
                b.ToTable(tb => tb.IsTemporal(ttb => { ttb.UseHistoryTable("EmployeesHistory"); }));
            });
            """);

        model.Entities.Single().Table.Should().Be("Employees");
        model.OwnTables.Should().Equal([(null, "EmployeesHistory")], "EF's migration creates the history table");
    }

    [Fact]
    public void AnEntitySplitOverTables_IsNotCompared_AndItsTablesAreTheModels()
    {
        var model = ParseBody("""
            modelBuilder.Entity("N.Customer", b =>
            {
                b.Property<int>("Id"); b.Property<string>("Phone"); b.HasKey("Id");
                b.ToTable("customers", "app");
                b.SplitToTable("customer_phones", "app", t => { t.Property("Id"); t.Property("Phone"); });
            });
            """);

        model.Entities.Should().BeEmpty();
        model.Unsupported.Should().ContainSingle(u => u.Reason.Contains("SplitToTable"));
        model.OwnTables.Should().BeEquivalentTo([("app", "customers"), ("app", "customer_phones")]);
    }

    [Fact]
    public void OneTypeOwnedTwice_IsTwoEntities_EachWithItsColumns()
    {
        var model = ParseBody("""
            modelBuilder.Entity("N.Order", b =>
            {
                b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("orders");
                b.OwnsOne("N.Address", "BillingAddress", b1 => { b1.Property<int>("OrderId"); b1.Property<string>("City").HasColumnName("BillingAddress_City"); b1.HasKey("OrderId"); b1.ToTable("orders"); });
                b.OwnsOne("N.Address", "ShippingAddress", b1 => { b1.Property<int>("OrderId"); b1.Property<string>("City").HasColumnName("ShippingAddress_City"); b1.HasKey("OrderId"); b1.ToTable("orders"); });
            });
            modelBuilder.Entity("N.Order", b =>
            {
                b.OwnsOne("N.Address", "BillingAddress", b1 => { b1.WithOwner().HasForeignKey("OrderId"); });
                b.OwnsOne("N.Address", "ShippingAddress", b1 => { b1.WithOwner().HasForeignKey("OrderId"); });
            });
            """);

        model.Entities.Where(e => e.SharesTableWith == "N.Order")
            .Select(e => (e.Name, e.Properties.Single(p => p.Name == "City").Column))
            .Should().BeEquivalentTo([("N.Address", "BillingAddress_City"), ("N.Order.ShippingAddress#Address", "ShippingAddress_City")]);
    }

    [Fact]
    public void ReadsOlderIndexAndOneToOneForms()
    {
        var model = ParseBody("""
            modelBuilder.Entity("N.User", b => { b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("users"); });
            modelBuilder.Entity("N.Profile", b =>
            {
                b.Property<int>("Id");
                b.Property<int>("UserId");
                b.Property<string>("Code").IsRequired().HasColumnName("code");
                b.HasKey("Id");
                b.HasIndex(new[] { "UserId", "Code" }, "IX_Profile_User_Code").IsUnique().HasFilter("[Code] IS NOT NULL");
                SqlServerIndexBuilderExtensions.IncludeProperties(b.HasIndex("Code"), new[] { "UserId" });
                b.HasAlternateKey("Id", "Code");
                b.ToTable("profiles", "app");
            });
            modelBuilder.Entity("N.User", b =>
            {
                b.HasOne("N.Profile", "Profile").WithOne("User").HasForeignKey("N.Profile", "UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            });
            """);

        var profile = model.Entities.Single(e => e.Name == "N.Profile");
        profile.Schema.Should().Be("app");
        profile.Indexes.Should().ContainSingle(i => i.Name == "IX_Profile_User_Code")
            .Which.Should().Match<EfIndex>(i => i.IsUnique && i.Filter == "[Code] IS NOT NULL" && i.Properties.SequenceEqual(new[] { "UserId", "Code" }));
        profile.Indexes.Should().Contain(i => i.Properties.SequenceEqual(new[] { "Code" }))
            .And.Contain(i => i.IsUnique && i.Properties.SequenceEqual(new[] { "Id", "Code" }), "an alternate key is a unique constraint");
        profile.ForeignKeys.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EfForeignKey("N.User", ["UserId"], [], "Cascade"));
        model.Entities.Single(e => e.Name == "N.User").ForeignKeys.Should().BeEmpty();
    }

    [Fact]
    public void MigrationsOfAnotherContext_AreNotCounted()
    {
        const string mine = """
            [DbContext(typeof(TestContext))] [Migration("20240101000000_Init")] partial class Init { }
            """;
        const string other = """
            [DbContext(typeof(OtherContext))] [Migration("20240202000000_Other")] partial class Other { }
            """;

        ParseBody("modelBuilder.HasAnnotation(\"ProductVersion\", \"9.0.1\");", mine, other)
            .Should().Match<EfModel>(m => m.MigrationIds.SequenceEqual(new[] { "20240101000000_Init" }) && m.ProductVersion == "9.0.1");
    }

    // ── What may be opened ─────────────────────────────────────────────────────

    [Fact]
    public void ARelativePath_IsRefused()
    {
        var read = () => SnapshotReader.Read("Migrations");
        read.Should().Throw<ArgumentException>().WithMessage("*absolute path*");
    }

    [Fact]
    public void AFileThatIsNotASnapshot_IsRefused()
    {
        var file = Path.Combine(FixtureDirectory(10, "SqlServer"), "Init.sql");
        var read = () => SnapshotReader.Read(file);
        read.Should().Throw<ArgumentException>().WithMessage("*ModelSnapshot.cs*reads no other file*");
    }

    [Fact]
    public void AFolderWithSeveralSnapshots_AsksForOne()
    {
        var read = () => SnapshotReader.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", "ef10"));
        read.Should().Throw<ArgumentException>().WithMessage("*Several snapshots*ShopPostgresContextModelSnapshot.cs*");
    }
}
