namespace PalDDD.Projections;

public readonly record struct ProjectionContext
{
    public ProjectionContext(string sourceName, string position, DateTimeOffset occurredAt)
        : this(sourceName, position, occurredAt, ReplayAuditMetadata.Empty)
    {
    }

    public ProjectionContext(
        string sourceName,
        string position,
        DateTimeOffset occurredAt,
        ReplayAuditMetadata audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);

        SourceName = sourceName;
        Position = position;
        OccurredAt = occurredAt;
        Audit = audit;
    }

    public string SourceName { get; }

    public string Position { get; }

    public DateTimeOffset OccurredAt { get; }

    public ReplayAuditMetadata Audit { get; }
}
