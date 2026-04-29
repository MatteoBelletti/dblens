using System.Text.RegularExpressions;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Gdpr;

public static class GapAnalyzer
{
    private static readonly Regex SoftDeleteRx   = new(@"^(deleted_?at|is_?deleted|removed_?at|deleted|soft_?delete)$",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreatedAtRx    = new(@"^(created_?at|creation_?date|inserted_?at|created_on)$",        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UpdatedAtRx    = new(@"^(updated_?at|modified_?at|last_?modified|last_?updated|changed_?at)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EncryptionRx   = new(@"(encrypt|hash|hashed|token|masked|obfuscat|redact)",            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TableGap Analyze(TableDetail table)
    {
        var names = table.Columns.Select(c => c.Name).ToList();

        return new TableGap(
            HasSoftDelete:       names.Any(n => SoftDeleteRx.IsMatch(n)),
            HasCreatedAt:        names.Any(n => CreatedAtRx.IsMatch(n)),
            HasUpdatedAt:        names.Any(n => UpdatedAtRx.IsMatch(n)),
            HasEncryptionMarker: names.Any(n => EncryptionRx.IsMatch(n)));
    }
}
