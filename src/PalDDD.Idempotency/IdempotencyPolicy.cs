namespace PalDDD.Idempotency;

public sealed record IdempotencyPolicy
{
    public static IdempotencyPolicy Default { get; } = new();

    public TimeSpan ProcessingTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
}
