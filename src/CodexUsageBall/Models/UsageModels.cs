namespace CodexUsageBall.Models;

public sealed record QuotaWindow(
    double UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

public sealed record QuotaBucket(
    string LimitId,
    string? LimitName,
    string? PlanType,
    QuotaWindow? Primary,
    QuotaWindow? Secondary);

public sealed record UsageSnapshot(
    string? PlanType,
    IReadOnlyList<QuotaBucket> Buckets,
    DateTimeOffset UpdatedAt)
{
    public IEnumerable<QuotaWindow> AllWindows => Buckets
        .SelectMany(bucket => new[] { bucket.Primary, bucket.Secondary })
        .OfType<QuotaWindow>();

    public double MostConstrainedRemaining => AllWindows
        .Select(window => window.RemainingPercent)
        .DefaultIfEmpty(0d)
        .Min();
}

public sealed class CodexConnectionException : Exception
{
    public CodexConnectionException(string message) : base(message) { }
    public CodexConnectionException(string message, Exception innerException) : base(message, innerException) { }
}
