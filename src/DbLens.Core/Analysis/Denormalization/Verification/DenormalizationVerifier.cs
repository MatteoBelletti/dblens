using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.Denormalization.Verification;

/// <summary>
/// Applies measurements to signals and reclassifies them. Pure. A measurement promotes only what
/// it measured: the title states the count, and the diagnosis goes into a separately labelled
/// part of the description, where it does not inherit the count's confidence.
/// </summary>
public static class DenormalizationVerifier
{
    /// <summary>Denominator below which a refutation is annotated as resting on few rows.</summary>
    private const long SmallSampleThreshold = 100;

    public const string DisclaimerVerified =
        "The heuristics below were tested against the data with read-only aggregate queries. " +
        "Only counts were read: no row value was returned to this tool or examined by it, so a " +
        "result can say how many rows diverge but never which rows or what they contain. " +
        "Entries in Findings are measurements taken at one point in time. Entries still in " +
        "Signals were not measured — they remain naming and structure heuristics. " +
        "A measured divergence means the values differ; it does not by itself mean the column " +
        "is a defect.";

    public static AnalyzeDenormalizationResult Apply(
        AnalyzeDenormalizationResult unverified,
        IReadOnlyList<VerificationMeasurement> measurements,
        AnalyzeDenormalizationRequest request)
    {
        var byTarget = measurements.ToDictionary(m => m.Target.Key, StringComparer.Ordinal);

        var reclassified = new List<Finding>(unverified.Findings);
        var promoted = 0;

        foreach (var signal in unverified.Signals)
        {
            var key = new VerificationTarget(signal.RuleId, signal.Table, signal.Columns ?? []).Key;

            if (!byTarget.TryGetValue(key, out var measurement))
            {
                reclassified.Add(signal);
                continue;
            }

            var updated = measurement.Status switch
            {
                VerificationStatus.Confirmed => Confirm(signal, measurement),
                VerificationStatus.Refuted => Refute(signal, measurement),
                _ => LeaveAsSignal(signal, measurement),
            };

            if (updated.Evidence.IsFactual()) promoted++;
            reclassified.Add(updated);
        }

        var split = FindingSplit.Partition(reclassified);
        var caveats = BuildCaveats(unverified.Caveats, measurements, split, request.Budget);

        return unverified with
        {
            Findings = split.Findings,
            Signals = split.Signals,
            Truncated = split.Truncated,
            Caveats = caveats,
            Disclaimer = DisclaimerVerified,
            Verification = VerificationSummary.From(measurements, attempted: true),
            Summary = unverified.Summary with
            {
                TotalFindings = split.Findings.Count,
                TotalSignals = split.Signals.Count,
                PromotedFromSignals = promoted,
                BySeverity = split.BySeverity(),
                ByRule = split.ByRule(DenormalizationRuleIds.All),
            },
        };
    }

    // ── Reclassification ───────────────────────────────────────────────────────

    private static Finding Confirm(Finding signal, VerificationMeasurement m)
    {
        var compared = m.Denominator!.Value;
        var diverging = m.Numerator!.Value;
        var ratio = compared == 0 ? 0 : (double)diverging / compared;

        return signal with
        {
            Evidence = Evidence.Measured,
            // Confidence 1.0 is coherent only because the Title asserts the count and nothing more.
            Confidence = 1.0,
            Title =
                $"{signal.Table}: {Num.N0(diverging)} of {Num.N0(compared)} rows — " +
                m.MeasurementDescription,
            Description =
                $"MEASURED: {Num.N0(diverging)} of {Num.N0(compared)} rows compared " +
                $"({Num.P0(ratio)}) — {m.MeasurementDescription}. " +
                "This number was read from the data with a read-only aggregate query; no row " +
                "values were returned.\n\n" +
                "WHAT THIS MAY MEAN: " + signal.Description + "\n\n" +
                "A deliberate point-in-time snapshot and a stale cache produce exactly the same " +
                "measurement. This result shows the values differ; it does not establish that the " +
                "difference is a defect.",
            Metadata = VerificationOutcome.Attach(signal.Metadata, m, ratio),
        };
    }

    private static Finding Refute(Finding signal, VerificationMeasurement m)
    {
        var compared = m.Denominator!.Value;

        var smallSample = compared < SmallSampleThreshold
            ? $" This rests on {Num.N0(compared)} row(s), which is a small sample — the count is " +
              "exact, but it supports a weaker conclusion than a large one would."
            : "";

        return signal with
        {
            Evidence = Evidence.Measured,
            Confidence = 1.0,
            Severity = Severity.Info,
            Title = $"{signal.Table}: no divergence across {Num.N0(compared)} rows — {m.MeasurementDescription}",
            Description =
                $"MEASURED: 0 of {Num.N0(compared)} rows compared — {m.MeasurementDescription}. " +
                "The heuristic below was tested and not borne out." + smallSample + "\n\n" +
                "WHAT WAS SUSPECTED: " + signal.Description,
            Metadata = VerificationOutcome.Attach(signal.Metadata, m, 0),
        };
    }

    /// <summary>
    /// Keeps a signal a signal, recording why it was not measured. Nothing about the finding's
    /// claim changes: an unverified conjecture is exactly as strong as it was before.
    /// </summary>
    private static Finding LeaveAsSignal(Finding signal, VerificationMeasurement m) =>
        signal with { Metadata = VerificationOutcome.Attach(signal.Metadata, m, null) };


    // ── Caveats ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildCaveats(
        IReadOnlyList<string> existing,
        IReadOnlyList<VerificationMeasurement> measurements,
        FindingSplit split,
        VerificationBudget budget)
    {
        var caveats = FindingSplit.WithoutSignalCaveat(existing);

        var inconclusive = measurements.Count(m => m.Status == VerificationStatus.Inconclusive);
        if (inconclusive > 0)
            caveats.Add(
                $"{inconclusive} verification query/queries compared no rows and are reported as " +
                "not measured, NOT as refuted. An empty table cannot disprove anything, and " +
                "reporting it as 'no divergence found' would be a factual-looking claim derived " +
                "from nothing.");

        caveats.AddRange(budget.NotRunCaveats(measurements));

        if (split.SignalCaveat() is { } signalCaveat)
            caveats.Add(signalCaveat);

        return caveats;
    }

}
