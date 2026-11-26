namespace DbLens.Core.Analysis.Indexes;

/// <summary>Index scan counts collected from DB statistics views, and the window they cover.</summary>
public sealed record IndexUsageStats(
    /// <summary>Scan count keyed by (schema, table, index_name). Absent key = index not tracked.</summary>
    IReadOnlyDictionary<(string Schema, string Table, string Index), long> ScanCounts,
    StatsWindow Window
);

/// <summary>The event the scan counters started counting from: whichever happened last.</summary>
public enum StatsWindowStart
{
    ServerStart,
    DatabaseCreated,
    DatabaseRestored,
    StatisticsReset,
}

/// <summary>
/// How long the scan counters have been counting. A dump restored yesterday on a server up for a
/// month has one day of counts, not thirty: every event that empties the counters bounds it.
/// </summary>
public sealed record StatsWindow(
    double Days,
    StatsWindowStart Since,
    /// <summary>
    /// False when the engine would not say when the database was created or restored (PostgreSQL
    /// without file access): a restore after the server started would then go unseen.
    /// </summary>
    bool RestoreVisible,
    /// <summary>
    /// Whether any index of the database has recorded a scan. None has when the workload never ran
    /// here — a restore the dates cannot show (pg_restore --clean into an existing database) included.
    /// </summary>
    bool AnyIndexScanned = true)
{
    /// <summary>
    /// When the database was last created or restored, apart from the other events that bound the window: a
    /// server restarted or statistics reset after a restore hide it in <see cref="Days"/>, not here.
    /// </summary>
    public (double Days, StatsWindowStart Since)? DatabaseEvent { get; init; }

    /// <summary>Days since each event, null when it never happened or cannot be read; the most recent wins.</summary>
    public static StatsWindow From(
        double serverStartDays, double? createdDays, double? restoredDays, double? statsResetDays, bool restoreVisible,
        bool anyIndexScanned = true)
    {
        var window = new StatsWindow(Math.Max(serverStartDays, 0), StatsWindowStart.ServerStart, restoreVisible, anyIndexScanned);
        window = window.Later(createdDays, StatsWindowStart.DatabaseCreated);
        window = window.Later(restoredDays, StatsWindowStart.DatabaseRestored);
        window = window.Later(statsResetDays, StatsWindowStart.StatisticsReset);

        var events = new[] { (Days: createdDays, Since: StatsWindowStart.DatabaseCreated), (Days: restoredDays, Since: StatsWindowStart.DatabaseRestored) }
            .Where(e => e.Days.HasValue).Select(e => (Days: Math.Max(e.Days!.Value, 0), e.Since)).ToList();
        return events.Count == 0 ? window : window with { DatabaseEvent = events.MinBy(e => e.Days) };
    }

    private StatsWindow Later(double? days, StatsWindowStart since) =>
        days is { } d && d < Days ? this with { Days = Math.Max(d, 0), Since = since } : this;

    /// <summary>"since the database was restored", for finding text.</summary>
    public string SinceText => Since switch
    {
        StatsWindowStart.DatabaseCreated => "since the database was created",
        StatsWindowStart.DatabaseRestored => "since the database was restored",
        StatsWindowStart.StatisticsReset => "since the statistics were last reset",
        _ => "since the server started",
    };
}
