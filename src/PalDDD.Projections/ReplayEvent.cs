namespace PalDDD.Projections;

public readonly record struct ReplayEvent<TMessage>
{
    public ReplayEvent(string sourceName, string position, DateTimeOffset occurredAt, TMessage message)
        : this(sourceName, position, occurredAt, message, ReplayAuditMetadata.Empty)
    {
    }

    public ReplayEvent(
        string sourceName,
        string position,
        DateTimeOffset occurredAt,
        TMessage message,
        ReplayAuditMetadata audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
        ArgumentNullException.ThrowIfNull(message);

        SourceName = sourceName;
        Position = position;
        OccurredAt = occurredAt;
        Message = message;
        Audit = audit;
    }

    public string SourceName { get; }

    public string Position { get; }

    public DateTimeOffset OccurredAt { get; }

    public TMessage Message { get; }

    public ReplayAuditMetadata Audit { get; }
}
