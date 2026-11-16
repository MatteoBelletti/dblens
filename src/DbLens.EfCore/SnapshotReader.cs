using DbLens.Core.Analysis.EfCore;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.EfCore;

/// <summary>
/// Reads an EF Core model from its migrations snapshot (<c>*ModelSnapshot.cs</c>) and the migration
/// ids from the <c>[Migration("…")]</c> attributes of the <c>*.Designer.cs</c> files beside it.
/// The files are parsed as C# syntax and never compiled: nothing of the user's runs.
/// </summary>
public static class SnapshotReader
{
    /// <summary>A snapshot of a few thousand entities stays well under this.</summary>
    public const long MaxFileBytes = 16 * 1024 * 1024;

    public const int MaxDesignerFiles = 5_000;

    private const string SnapshotSuffix = "ModelSnapshot.cs";
    private const string DesignerSuffix = ".Designer.cs";

    /// <summary>
    /// <paramref name="path"/> is a snapshot file or a directory holding exactly one. Only files named
    /// <c>*ModelSnapshot.cs</c> and <c>*.Designer.cs</c> are opened.
    /// </summary>
    public static EfModel Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("snapshotPath must be an absolute path to a *ModelSnapshot.cs file or its Migrations folder.");

        var snapshot = FindSnapshot(path);
        var snapshotText = ReadLimited(snapshot);
        var directory = Path.GetDirectoryName(snapshot)!;

        var designers = Directory
            .EnumerateFiles(directory, "*" + DesignerSuffix, SearchOption.AllDirectories)
            .Take(MaxDesignerFiles + 1)
            .ToList();
        if (designers.Count > MaxDesignerFiles)
            throw new ArgumentException($"More than {MaxDesignerFiles} *.Designer.cs files under {directory}: point snapshotPath at the Migrations folder itself.");

        return Parse(snapshotText, designers.Select(ReadLimited));
    }

    private static string FindSnapshot(string path)
    {
        if (File.Exists(path))
        {
            if (!path.EndsWith(SnapshotSuffix, StringComparison.Ordinal))
                throw new ArgumentException($"snapshotPath must name a file ending in {SnapshotSuffix}, or a folder: dblens reads no other file.");
            return path;
        }

        if (!Directory.Exists(path))
            throw new ArgumentException($"snapshotPath does not exist: {path}");

        var found = Directory.EnumerateFiles(path, "*" + SnapshotSuffix, SearchOption.AllDirectories).Take(20).ToList();
        return found.Count switch
        {
            0 => throw new ArgumentException($"No *{SnapshotSuffix} under {path}. A project without migrations has no snapshot to read."),
            1 => found[0],
            _ => throw new ArgumentException(
                $"Several snapshots under {path}, one per DbContext: pass one of them as snapshotPath — " +
                string.Join(", ", found.Select(f => Path.GetRelativePath(path, f))) + "."),
        };
    }

    private static string ReadLimited(string file)
    {
        try
        {
            var length = new FileInfo(file).Length;
            if (length > MaxFileBytes)
                throw new ArgumentException($"{Path.GetFileName(file)} is {length / (1024 * 1024)} MB, over the {MaxFileBytes / (1024 * 1024)} MB dblens reads.");
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shown to the caller: the SDK hides any other exception's message.
            throw new ArgumentException($"{Path.GetFileName(file)} could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>The model in <paramref name="snapshot"/>, with the migrations of the same DbContext among <paramref name="designers"/>.</summary>
    public static EfModel Parse(string snapshot, IEnumerable<string> designers)
    {
        var root = CSharpSyntaxTree.ParseText(snapshot).GetCompilationUnitRoot();
        var snapshotClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.BaseList?.Types.Any(t => LastName(t.Type) == "ModelSnapshot") == true)
            ?? throw new ArgumentException("The file holds no class deriving from ModelSnapshot.");

        var context = ContextOf(snapshotClass)
            ?? throw new ArgumentException("The snapshot has no [DbContext(typeof(…))] attribute.");
        var buildModel = snapshotClass.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "BuildModel" && m.Body is not null)
            ?? throw new ArgumentException("The snapshot has no BuildModel method.");

        var model = new ModelState();
        var modelBuilder = buildModel.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text ?? "modelBuilder";
        foreach (var statement in buildModel.Body!.Statements)
            ReadModelStatement(statement, modelBuilder, model);

        var migrations = designers
            .Select(text => CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot())
            .SelectMany(r => r.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(c => ContextOf(c) == context)
            .Select(MigrationIdOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return model.Resolve(context, migrations);
    }

    // ── Model statements ───────────────────────────────────────────────────────

    private static void ReadModelStatement(StatementSyntax statement, string modelBuilder, ModelState model)
    {
        if (statement is not ExpressionStatementSyntax { Expression: var expression }
            || Flatten(expression, modelBuilder) is not { } chain) return;

        foreach (var call in chain)
        {
            switch (call.Name)
            {
                case "HasDefaultSchema":
                    model.DefaultSchema = Str(call.Arg(0));
                    break;
                case "HasAnnotation" when Str(call.Arg(0)) == "ProductVersion":
                    model.ProductVersion = Str(call.Arg(1));
                    break;
                case "Entity" when Str(call.Arg(0)) is { } name && call.Arg(1) is LambdaExpressionSyntax lambda:
                    ReadEntity(model.Entity(name), lambda, model);
                    break;
            }
        }
    }

    private static void ReadEntity(EntityState entity, LambdaExpressionSyntax lambda, ModelState model)
    {
        if (ParameterOf(lambda) is not { } builder) return;

        foreach (var statement in BodyOf(lambda))
        {
            if (statement is not ExpressionStatementSyntax { Expression: var expression }
                || Flatten(expression, builder) is not { Count: > 0 } chain) continue;

            var head = chain[0];
            var rest = chain.Skip(1).ToList();

            switch (head.Name)
            {
                case "Property" when Str(head.Arg(0)) is { } name:
                    ReadProperty(entity.Property(name, head.TypeArgument), rest);
                    break;
                case "HasKey":
                    entity.Key = Strings(head.Arguments);
                    break;
                case "HasAlternateKey":
                    // A unique constraint in the database, which the catalog lists among the indexes.
                    entity.Indexes.Add(new EfIndex(Strings(head.Arguments), null, IsUnique: true, null));
                    break;
                case "HasNoKey":
                    entity.Keyless = true;
                    break;
                case "HasIndex":
                    entity.Indexes.Add(ReadIndex(head, rest));
                    break;
                case "ToTable":
                    ReadToTable(entity, head);
                    break;
                case "ToView":
                    entity.View = Str(head.Arg(0)) ?? "(unnamed)";
                    break;
                case "ToJson":
                    entity.Json = true;
                    break;
                case "HasBaseType":
                    entity.BaseType = Str(head.Arg(0));
                    break;
                case "UseTptMappingStrategy":
                    entity.Strategy = "TPT";
                    break;
                case "UseTpcMappingStrategy":
                    entity.Strategy = "TPC";
                    break;
                case "HasOne" when Str(head.Arg(0)) is { } target:
                    model.Relationships.Add(ReadRelationship(entity.Name, target, rest));
                    break;
                case "OwnsOne" or "OwnsMany" when Str(head.Arg(0)) is { } ownedType && head.Arguments.LastOrDefault()?.Expression is LambdaExpressionSyntax ownedLambda:
                    var owned = model.Entity(ownedType);
                    owned.Owner = entity.Name;
                    owned.OwnedNavigation = Str(head.Arg(1));
                    owned.OwnsMany = head.Name == "OwnsMany";
                    ReadEntity(owned, ownedLambda, model);
                    break;
                case "ComplexProperty" when head.Arguments.LastOrDefault()?.Expression is LambdaExpressionSyntax complexLambda:
                    // A complex type's properties are columns of the entity's own table.
                    ReadEntity(entity, complexLambda, model);
                    break;
                case "Navigation" when Str(head.Arg(0)) is { } navigation && rest.Any(c => c.Name == "IsRequired" && Bool(c.Arg(0)) != false):
                    entity.RequiredNavigations.Add(navigation);
                    break;
            }
        }
    }

    private static void ReadProperty(PropertyState property, List<Call> calls)
    {
        foreach (var call in calls)
        {
            switch (call.Name)
            {
                case "IsRequired": property.Required = Bool(call.Arg(0)) ?? true; break;
                case "HasMaxLength": property.MaxLength = Int(call.Arg(0)); break;
                case "IsUnicode": property.Unicode = Bool(call.Arg(0)) ?? true; break;
                case "HasPrecision": property.Precision = Int(call.Arg(0)); property.Scale = Int(call.Arg(1)); break;
                case "HasColumnType": property.ColumnType = Str(call.Arg(0)); break;
                case "HasColumnName": property.Column = Str(call.Arg(0)); break;
            }
        }
    }

    private static EfIndex ReadIndex(Call head, List<Call> calls)
    {
        // HasIndex("A", "B") or, for a named index in older snapshots, HasIndex(new[] { "A", "B" }, "IX_Name").
        var array = StringArray(head.Arg(0));
        var properties = array ?? Strings(head.Arguments);
        var name = array is not null ? Str(head.Arg(1)) : null;
        var unique = false;
        string? filter = null;

        foreach (var call in calls)
        {
            switch (call.Name)
            {
                case "HasDatabaseName" or "HasName": name = Str(call.Arg(0)); break;
                case "IsUnique": unique = Bool(call.Arg(0)) ?? true; break;
                case "HasFilter": filter = Str(call.Arg(0)); break;
            }
        }

        return new EfIndex(properties, name, unique, filter);
    }

    private static void ReadToTable(EntityState entity, Call head)
    {
        // ToTable("t"), ToTable("t", "schema"), ToTable("t", "schema", t => …), ToTable((string)null).
        if (IsNull(head.Arg(0)))
        {
            entity.NoTable = true;
            return;
        }

        entity.Table = Str(head.Arg(0));
        if (head.Arguments.Count > 1 && Str(head.Arg(1)) is { } schema) entity.Schema = schema;
    }

    private static RelationshipState ReadRelationship(string declaringEntity, string target, List<Call> calls)
    {
        var relationship = new RelationshipState(declaringEntity, target);

        foreach (var call in calls)
        {
            switch (call.Name)
            {
                case "WithOne": relationship.OneToOne = true; break;
                case "HasForeignKey":
                    relationship.ForeignKeyArguments = Strings(call.Arguments);
                    break;
                case "HasPrincipalKey":
                    relationship.PrincipalKeyArguments = Strings(call.Arguments);
                    break;
                case "OnDelete": relationship.OnDelete = EnumMember(call.Arg(0)); break;
            }
        }

        return relationship;
    }

    // ── Syntax helpers ─────────────────────────────────────────────────────────

    private sealed record Call(string Name, string? TypeArgument, IReadOnlyList<ArgumentSyntax> Arguments)
    {
        public ExpressionSyntax? Arg(int index) => index < Arguments.Count ? Arguments[index].Expression : null;
    }

    /// <summary>
    /// <c>b.A(x).B(y)</c> → [A(x), B(y)], rooted at the builder parameter <paramref name="root"/>. A
    /// static extension call such as <c>SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property("Id"))</c>
    /// reads as its first argument's chain followed by the call. Anything else is not a builder chain.
    /// </summary>
    private static List<Call>? Flatten(ExpressionSyntax expression, string root)
    {
        List<Call> calls = [];

        while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation)
        {
            var arguments = invocation.ArgumentList.Arguments;
            var name = access.Name.Identifier.Text;
            var typeArgument = (access.Name as GenericNameSyntax)?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();

            switch (access.Expression)
            {
                case IdentifierNameSyntax id when id.Identifier.Text == root:
                    calls.Insert(0, new Call(name, typeArgument, arguments));
                    return calls;
                case InvocationExpressionSyntax inner:
                    calls.Insert(0, new Call(name, typeArgument, arguments));
                    expression = inner;
                    break;
                default:
                    if (arguments.Count == 0) return null;
                    calls.Insert(0, new Call(name, typeArgument, [.. arguments.Skip(1)]));
                    expression = arguments[0].Expression;
                    break;
            }
        }

        return null;
    }

    private static string? ParameterOf(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
        ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var first, ..] } => first.Identifier.Text,
        _ => null,
    };

    private static IEnumerable<StatementSyntax> BodyOf(LambdaExpressionSyntax lambda) => lambda.Body switch
    {
        BlockSyntax block => block.Statements,
        ExpressionSyntax expression => [SyntaxFactory.ExpressionStatement(expression)],
        _ => [],
    };

    private static string? ContextOf(ClassDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => LastName(a.Name) is "DbContext" or "DbContextAttribute")
            .Select(a => a.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .Select(t => LastName(t.Type))
            .FirstOrDefault();

    private static string? MigrationIdOf(ClassDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => LastName(a.Name) is "Migration" or "MigrationAttribute")
            .Select(a => Str(a.ArgumentList?.Arguments.FirstOrDefault()?.Expression))
            .FirstOrDefault(id => id is not null);

    private static string LastName(SyntaxNode name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => name.ToString(),
    };

    private static bool IsNull(ExpressionSyntax? e) => e switch
    {
        LiteralExpressionSyntax l => l.IsKind(SyntaxKind.NullLiteralExpression),
        CastExpressionSyntax c => IsNull(c.Expression),
        ParenthesizedExpressionSyntax p => IsNull(p.Expression),
        _ => false,
    };

    private static string? Str(ExpressionSyntax? e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        CastExpressionSyntax c => Str(c.Expression),
        ParenthesizedExpressionSyntax p => Str(p.Expression),
        _ => null,
    };

    private static int? Int(ExpressionSyntax? e) =>
        e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.NumericLiteralExpression) && l.Token.Value is int i ? i : null;

    private static bool? Bool(ExpressionSyntax? e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.TrueLiteralExpression) => true,
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.FalseLiteralExpression) => false,
        _ => null,
    };

    private static string? EnumMember(ExpressionSyntax? e) => (e as MemberAccessExpressionSyntax)?.Name.Identifier.Text;

    private static List<string>? StringArray(ExpressionSyntax? e)
    {
        IEnumerable<ExpressionSyntax>? elements = e switch
        {
            ImplicitArrayCreationExpressionSyntax i => i.Initializer.Expressions,
            ArrayCreationExpressionSyntax a => a.Initializer?.Expressions,
            CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().Select(x => x.Expression),
            _ => null,
        };
        return elements is null ? null : [.. elements.Select(Str).OfType<string>()];
    }

    /// <summary>The string arguments, flattening an array argument.</summary>
    private static List<string> Strings(IEnumerable<ArgumentSyntax> arguments) =>
        [.. arguments.SelectMany(a => StringArray(a.Expression) ?? (Str(a.Expression) is { } s ? [s] : []))];
}
