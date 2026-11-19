using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;

namespace DbLens.EfCore;

/// <summary>What the snapshot said, before the mapping is resolved into tables.</summary>
internal sealed class ModelState
{
    private readonly Dictionary<string, EntityState> _entities = new(StringComparer.Ordinal);
    private readonly List<EntityState> _order = [];

    public string? DefaultSchema { get; set; }

    public string? ProductVersion { get; set; }

    public List<RelationshipState> Relationships { get; } = [];

    /// <summary>A snapshot configures an entity in several Entity(…) blocks: they are one entity.</summary>
    public EntityState Entity(string name)
    {
        if (!_entities.TryGetValue(name, out var entity))
        {
            entity = new EntityState(name);
            _entities[name] = entity;
            _order.Add(entity);
        }

        return entity;
    }

    /// <summary>
    /// The type <paramref name="owner"/> owns through <paramref name="navigation"/>. The same CLR type
    /// owned twice (BillingAddress and ShippingAddress) is two entity types: the second takes EF's
    /// shared-type name, Owner.Navigation#Type, so neither overwrites the other's columns.
    /// </summary>
    public EntityState Owned(string type, string owner, string? navigation)
    {
        if (!_entities.TryGetValue(type, out var existing)
            || existing.Owner is null
            || (existing.Owner == owner && existing.OwnedNavigation == navigation))
            return Entity(type);

        return Entity($"{owner}.{navigation}#{Names.Short(type)}");
    }

    public EfModel Resolve(string context, IReadOnlyList<string> migrations)
    {
        List<EfUnsupported> unsupported = [];
        Dictionary<string, Placement> placements = new(StringComparer.Ordinal);

        foreach (var entity in _order)
            if (Place(entity, placements, unsupported) is { } placement)
                placements[entity.Name] = placement;

        // Table splitting: two independent entities in one table. Not compared, never half-compared.
        foreach (var shared in placements.Values
                     .Where(p => p.SharesTableWith is null)
                     .GroupBy(p => (p.Schema ?? DefaultSchema ?? "", p.Table), CaseInsensitivePairComparer.Instance)
                     .Where(g => g.Count() > 1)
                     .ToList())
        {
            var names = shared.Select(p => p.Entity.Name).ToList();
            unsupported.Add(new($"{string.Join(" and ", names)} share the table {shared.Key.Item2} (table splitting), which dblens does not compare.", names));
            foreach (var name in names) placements.Remove(name);
        }

        // A subtype or owned type whose table owner was not placed has no table to be compared in.
        for (var removed = true; removed;)
        {
            removed = false;
            foreach (var orphan in placements.Values
                         .Where(p => p.SharesTableWith is { } host && !placements.ContainsKey(host)).ToList())
            {
                unsupported.Add(new($"{orphan.Entity.Name} is stored in the table of {orphan.SharesTableWith}, which was not compared, so it was not either.", [orphan.Entity.Name, orphan.SharesTableWith!]));
                placements.Remove(orphan.Entity.Name);
                removed = true;
            }
        }

        var foreignKeys = Relationships
            .Select(r => ForeignKeyOf(r))
            .OfType<(string Dependent, EfForeignKey Key)>()
            .ToLookup(f => f.Dependent, f => f.Key, StringComparer.Ordinal);

        var entities = _order
            .Where(e => placements.ContainsKey(e.Name))
            .Select(e => Build(e, placements[e.Name], foreignKeys[e.Name]))
            .ToList();

        return new EfModel(context, ProductVersion, DefaultSchema, entities, migrations, unsupported);
    }

    private sealed record Placement(EntityState Entity, string? Schema, string Table, IReadOnlyList<string> Key, string? SharesTableWith, bool ColumnsNullable);

    private Placement? Place(EntityState entity, Dictionary<string, Placement> placed, List<EfUnsupported> unsupported)
    {
        if (entity.Owner is { } ownerName)
            return PlaceOwned(entity, ownerName, placed, unsupported);

        if (entity.BaseType is not null)
            return PlaceDerived(entity, unsupported);

        if (entity.View is { } view)
        {
            unsupported.Add(new($"{entity.Name} is mapped to the view {view}, which dblens does not compare.", [entity.Name]));
            return null;
        }

        if (entity.NoTable)
        {
            unsupported.Add(new($"{entity.Name} is mapped to no table, so it was not compared.", [entity.Name]));
            return null;
        }

        if (entity.Keyless || entity.Key.Count == 0)
        {
            unsupported.Add(new($"{entity.Name} has no key (keyless entity), which dblens does not compare.", [entity.Name]));
            return null;
        }

        if (entity.Strategy == "TPC")
        {
            unsupported.Add(new($"{entity.Name} uses table-per-concrete-type mapping, which dblens does not compare.", [entity.Name]));
            return null;
        }

        return new Placement(entity, entity.Schema, entity.Table ?? Names.Short(entity.Name), entity.Key, null, ColumnsNullable: false);
    }

    private Placement? PlaceOwned(EntityState owned, string ownerName, Dictionary<string, Placement> placed, List<EfUnsupported> unsupported)
    {
        if (owned.Json)
        {
            unsupported.Add(new($"{owned.Name}, owned by {ownerName}, is stored as JSON (ToJson), which dblens does not compare.", [owned.Name, ownerName]));
            return null;
        }

        if (!placed.TryGetValue(ownerName, out var owner))
        {
            unsupported.Add(new($"{owned.Name} is owned by {ownerName}, which was not compared, so it was not either.", [owned.Name]));
            return null;
        }

        var sameTable = owned.Table is null
            || (CaseInsensitivePairComparer.Instance.Equals((owned.Schema ?? "", owned.Table), (owner.Schema ?? "", owner.Table)) && !owned.OwnsMany);

        if (!sameTable)
            return new Placement(owned, owned.Schema, owned.Table!, owned.Key, null, ColumnsNullable: false);

        // An optional owned type sharing its owner's row stores NULL in every column when it is absent.
        var required = _entities[ownerName].RequiredNavigations.Contains(owned.OwnedNavigation ?? "");
        return new Placement(owned, owner.Schema, owner.Table, owner.Key, ownerName, ColumnsNullable: !required || owner.ColumnsNullable);
    }

    private Placement? PlaceDerived(EntityState derived, List<EfUnsupported> unsupported)
    {
        var root = derived;
        while (root.BaseType is { } baseName && _entities.TryGetValue(baseName, out var parent)) root = parent;

        if (root.Strategy == "TPC")
        {
            unsupported.Add(new($"{derived.Name} uses table-per-concrete-type mapping, which dblens does not compare.", [derived.Name]));
            return null;
        }

        if (root.Strategy == "TPT" || (derived.Table is not null && derived.Table != root.Table))
        {
            unsupported.Add(new($"{derived.Name} uses table-per-type mapping, which dblens does not compare.", [derived.Name]));
            return null;
        }

        // Table-per-hierarchy: a subtype's columns are NULL in the rows of every other subtype.
        return new Placement(derived, root.Schema, root.Table ?? Names.Short(root.Name), root.Key, root.Name, ColumnsNullable: true);
    }

    private (string Dependent, EfForeignKey Key)? ForeignKeyOf(RelationshipState r)
    {
        var arguments = r.ForeignKeyArguments;
        if (arguments.Count == 0) return null;

        var dependent = r.DeclaringEntity;
        var principal = r.Target;

        // One-to-one names the dependent type first: HasForeignKey("Ns.Profile", "UserId").
        if (r.OneToOne && arguments.Count > 1 && _entities.ContainsKey(arguments[0]))
        {
            dependent = arguments[0];
            principal = dependent == r.DeclaringEntity ? r.Target : r.DeclaringEntity;
            arguments = [.. arguments.Skip(1)];
        }

        var principalKey = r.PrincipalKeyArguments.Count > 0 && _entities.ContainsKey(r.PrincipalKeyArguments[0])
            ? [.. r.PrincipalKeyArguments.Skip(1)]
            : r.PrincipalKeyArguments;

        return (dependent, new EfForeignKey(principal, arguments, principalKey, r.OnDelete));
    }

    private static EfEntity Build(EntityState entity, Placement placement, IEnumerable<EfForeignKey> foreignKeys)
    {
        var properties = entity.Properties
            // An owned type's key is its owner's key, already a column of the owner.
            .Where(p => placement.SharesTableWith is null || entity.Owner is null || !entity.Key.Contains(p.Name))
            .Select(p => p.Build(placement.ColumnsNullable))
            .ToList();

        return new EfEntity(
            entity.Name, placement.Schema, placement.Table, properties, placement.Key,
            entity.Indexes, [.. foreignKeys], placement.SharesTableWith);
    }
}

internal sealed class EntityState(string name)
{
    private readonly Dictionary<string, PropertyState> _properties = new(StringComparer.Ordinal);

    public string Name { get; } = name;
    public List<PropertyState> Properties { get; } = [];
    public IReadOnlyList<string> Key { get; set; } = [];
    public List<EfIndex> Indexes { get; } = [];
    public string? Table { get; set; }
    public string? Schema { get; set; }
    public bool NoTable { get; set; }
    public string? View { get; set; }
    public bool Keyless { get; set; }
    public bool Json { get; set; }
    public string? BaseType { get; set; }
    public string? Strategy { get; set; }
    public string? Owner { get; set; }
    public string? OwnedNavigation { get; set; }
    public bool OwnsMany { get; set; }
    public HashSet<string> RequiredNavigations { get; } = new(StringComparer.Ordinal);

    /// <summary>The same property is configured again by later statements (identity, comments…).</summary>
    public PropertyState Property(string name, string? clrType, string defaultColumn)
    {
        if (!_properties.TryGetValue(name, out var property))
        {
            property = new PropertyState(name, defaultColumn);
            _properties[name] = property;
            Properties.Add(property);
        }

        property.ClrType ??= clrType;
        return property;
    }
}

internal sealed class PropertyState(string name, string defaultColumn)
{
    public string Name { get; } = name;
    public string? ClrType { get; set; }
    public string? Column { get; set; }
    public string? ColumnType { get; set; }
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public bool? Unicode { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }

    public EfProperty Build(bool columnNullable)
    {
        var type = ClrType ?? "object";
        var nullable = columnNullable || type.EndsWith('?') || (IsReferenceType(type) && !Required);
        return new EfProperty(Name, type, Column ?? defaultColumn, ColumnType, nullable, MaxLength, Unicode, Precision, Scale);
    }

    /// <summary>
    /// A snapshot writes value types as their own name ("long", "DateTimeOffset", an enum) and marks
    /// a nullable one with '?'; reference types are nullable unless IsRequired() says otherwise.
    /// </summary>
    private static bool IsReferenceType(string type) =>
        type is "string" or "object" || type.EndsWith("[]", StringComparison.Ordinal) || type.Contains('<', StringComparison.Ordinal);
}

internal sealed class RelationshipState(string declaringEntity, string target)
{
    public string DeclaringEntity { get; } = declaringEntity;
    public string Target { get; } = target;
    public bool OneToOne { get; set; }
    public IReadOnlyList<string> ForeignKeyArguments { get; set; } = [];
    public IReadOnlyList<string> PrincipalKeyArguments { get; set; } = [];
    public string? OnDelete { get; set; }
}
