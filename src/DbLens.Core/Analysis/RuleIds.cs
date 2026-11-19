namespace DbLens.Core.Analysis;

public static class IndexRuleIds
{
    public const string FkWithoutIndex = "fk-without-index";
    public const string DuplicateIndex = "duplicate-index";
    public const string RedundantIndex = "redundant-index";
    public const string UnusedIndex    = "unused-index";
    public const string FkTypeMismatch = "fk-type-mismatch";

    public static readonly string[] All = [FkWithoutIndex, DuplicateIndex, RedundantIndex, UnusedIndex, FkTypeMismatch];
}

public static class GdprRuleIds
{
    public const string PiiIdentifier       = "pii-identifier";
    public const string PiiContact          = "pii-contact";
    public const string PiiFinancial        = "pii-financial";
    public const string PiiSpecialCategory  = "pii-special-category";
    public const string PiiLocation         = "pii-location";
    public const string MissingGdprControls = "missing-gdpr-controls";

    public static readonly string[] All =
        [PiiIdentifier, PiiContact, PiiFinancial, PiiSpecialCategory, PiiLocation, MissingGdprControls];
}

public static class DenormalizationRuleIds
{
    public const string RepeatingGroup   = "denorm-repeating-group";
    public const string DerivedColumn    = "denorm-derived-column";
    public const string DuplicatedColumn = "denorm-duplicated-column";
    public const string MultiValueColumn = "denorm-multivalue-column";
    public const string WideTable        = "denorm-wide-table";

    public static readonly string[] All =
        [RepeatingGroup, DerivedColumn, DuplicatedColumn, MultiValueColumn, WideTable];
}

public static class FragmentationRuleIds
{
    public const string HighFragmentation     = "idx-fragmentation-high";
    public const string ModerateFragmentation = "idx-fragmentation-moderate";
    public const string LowPageDensity        = "idx-low-page-density";

    public static readonly string[] All =
        [HighFragmentation, ModerateFragmentation, LowPageDensity];
}

public static class QueryPlanRuleIds
{
    public const string SeqScanLargeTable  = "plan-seq-scan-large-table";
    public const string EstimateMismatch   = "plan-estimate-mismatch";
    public const string NestedLoopLargeInput = "plan-nested-loop-large-input";
    public const string SortSpilledToDisk  = "plan-sort-spilled-to-disk";
    public const string ScanWithFilter     = "plan-scan-with-filter";

    public static readonly string[] All =
        [SeqScanLargeTable, EstimateMismatch, NestedLoopLargeInput, SortSpilledToDisk, ScanWithFilter];
}

public static class MigrationRuleIds
{
    public const string TableRewrite         = "migration-table-rewrite";
    public const string BlockingLock         = "migration-blocking-lock";
    public const string IndexNotConcurrent   = "migration-index-not-concurrent";
    public const string UnvalidatedConstraint = "migration-validated-constraint-scan";
    public const string Destructive          = "migration-destructive";
    public const string Summary              = "migration-summary";

    public static readonly string[] All =
        [TableRewrite, BlockingLock, IndexNotConcurrent, UnvalidatedConstraint, Destructive, Summary];
}

public static class CascadeRuleIds
{
    public const string CircularPath     = "cascade-circular-path";
    public const string DeepPath         = "cascade-deep-path";
    public const string BreaksWithSetNull  = "cascade-breaks-with-set-null";
    public const string BreaksWithRestrict = "cascade-breaks-with-restrict";
    public const string SelfReferencing  = "cascade-self-referencing";
    public const string Summary          = "cascade-summary";

    public static readonly string[] All =
        [CircularPath, DeepPath, BreaksWithSetNull, BreaksWithRestrict, SelfReferencing, Summary];
}

public static class RelationshipRuleIds
{
    public const string UndeclaredForeignKey = "relationship-undeclared-fk";

    public static readonly string[] All = [UndeclaredForeignKey];
}

public static class EfRuleIds
{
    public const string MigrationUnknown = "ef-migration-unknown";
    public const string TableMissing     = "ef-table-missing";
    public const string ColumnMissing    = "ef-column-missing";
    public const string IndexMissing     = "ef-index-missing";
    public const string MigrationPending = "ef-migration-pending";
    public const string IndexUnmodeled   = "ef-index-unmodeled";
    public const string ForeignKeyMissing = "ef-fk-missing";
    public const string UnmappedTable    = "ef-unmapped-table";
    public const string ColumnType       = "ef-column-type";
    public const string ColumnNullability = "ef-column-nullability";
    public const string IndexUniqueness  = "ef-index-uniqueness";
    public const string DriftSummary     = "ef-drift-summary";

    public static readonly string[] All =
    [
        MigrationUnknown, TableMissing, ColumnMissing, IndexMissing, MigrationPending,
        IndexUnmodeled, ForeignKeyMissing, UnmappedTable, ColumnType, ColumnNullability, IndexUniqueness, DriftSummary,
    ];
}

public static class HotQueryRuleIds
{
    public const string ReadsPerRow = "hot-query-reads-per-row";
    public const string DiskReads   = "hot-query-disk-reads";
    public const string TempSpill   = "hot-query-temp-spill";
    public const string JoinFanOut  = "join-fanout";
    public const string Summary     = "hot-query-summary";

    public static readonly string[] All = [ReadsPerRow, DiskReads, TempSpill, JoinFanOut, Summary];
}

public static class HygieneRuleIds
{
    public const string ColumnAlwaysNull   = "column-always-null";
    public const string ColumnSingleValue  = "column-single-value";
    public const string ColumnAbandoned    = "column-abandoned";
    public const string ColumnNotInModel   = "column-not-in-model";
    public const string ColumnLegacyName   = "column-legacy-name";
    public const string MissingCreated     = "audit-missing-created";
    public const string MissingUpdated     = "audit-missing-updated";
    public const string UpdatedNotMaintained = "audit-updated-not-maintained";
    public const string UpdateBeforeCreate = "audit-update-before-create";
    public const string CreatedNullable    = "audit-created-nullable";
    public const string WithoutTimeZone    = "audit-timestamp-without-timezone";
    public const string Summary            = "hygiene-summary";

    public static readonly string[] All =
    [
        ColumnAlwaysNull, ColumnSingleValue, ColumnAbandoned, ColumnNotInModel, ColumnLegacyName,
        MissingCreated, MissingUpdated, UpdatedNotMaintained, UpdateBeforeCreate, CreatedNullable,
        WithoutTimeZone, Summary,
    ];
}
