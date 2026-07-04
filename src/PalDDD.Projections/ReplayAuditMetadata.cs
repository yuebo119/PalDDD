namespace PalDDD.Projections;

public readonly record struct ReplayAuditMetadata(
    string? ActorId,
    string? Reason,
    Guid? CorrelationId,
    Guid? CausationId,
    string? TraceParent,
    string? TraceState)
{
    public static ReplayAuditMetadata Empty { get; } = new(null, null, null, null, null, null);
}
