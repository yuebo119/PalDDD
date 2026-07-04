namespace PalDDD.Idempotency;

public enum IdempotencyExecutionStatus
{
    Executed = 0,
    Cached = 1,
    Skipped = 2
}

public sealed record IdempotencyExecution<TResult>(IdempotencyExecutionStatus Status, TResult? Result);
