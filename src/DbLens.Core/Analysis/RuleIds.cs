namespace DbLens.Core.Analysis;

public static class IndexRuleIds
{
    public const string FkWithoutIndex = "fk-without-index";
    public const string DuplicateIndex = "duplicate-index";
    public const string RedundantIndex = "redundant-index";
    public const string UnusedIndex    = "unused-index";

    public static readonly string[] All = [FkWithoutIndex, DuplicateIndex, RedundantIndex, UnusedIndex];
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
