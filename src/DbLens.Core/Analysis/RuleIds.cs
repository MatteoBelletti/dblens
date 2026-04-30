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
