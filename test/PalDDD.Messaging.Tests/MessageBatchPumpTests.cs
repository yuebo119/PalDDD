using PalDDD.Messaging;
using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Tests;

public sealed class MessageBatchPumpTests
{
    [Test]
    public async Task SubscribeBatchAsync_GroupsBySizeAndPreservesOrderAndMessageId(CancellationToken cancellationToken)
    {
        var probe = new ProbeBroker<string>();
        IMessageBroker broker = probe;
        var batches = new List<ConsumedMessage<string>[]>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await broker.SubscribeBatchAsync<string>(
            (batch, _) =>
            {
                batches.Add([.. batch]);
                if (batches.Sum(static b => b.Length) >= 5)
                    allReceived.TrySetResult();
                return ValueTask.CompletedTask;
            },
            maxBatchSize: 2,
            maxDelay: TimeSpan.FromMilliseconds(10),
            cancellationToken);

        for (var i = 0; i < 5; i++)
            await probe.PushAsync($"message-{i}", $"id-{i}", cancellationToken).ConfigureAwait(false);

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        var flattened = batches.SelectMany(static b => b).ToArray();
        await Assert.That(flattened.Length).IsEqualTo(5);
        await Assert.That(batches.All(static b => b.Length <= 2)).IsTrue();
        await Assert.That(string.Join(",", flattened.Select(static b => b.Message)))
            .IsEqualTo("message-0,message-1,message-2,message-3,message-4");
        await Assert.That(string.Join(",", flattened.Select(static b => b.Context!.MessageId)))
            .IsEqualTo("id-0,id-1,id-2,id-3,id-4");
    }

    [Test]
    public async Task FromHeaders_MessageIdOnly_ReturnsContextWithId(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var context = MessageConsumeContext.FromHeaders(null, null, "broker-msg-1")
            ?? throw new InvalidOperationException("MessageId-only context should not be null.");

        await Assert.That(context.MessageId).IsEqualTo("broker-msg-1");
        await Assert.That(context.Headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeBatchAsync_FlushesByTimeBoundWhenBatchIsNotFull(CancellationToken cancellationToken)
    {
        var probe = new ProbeBroker<string>();
        IMessageBroker broker = probe;
        var received = new TaskCompletionSource<ConsumedMessage<string>[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await broker.SubscribeBatchAsync<string>(
            (batch, _) =>
            {
                received.TrySetResult([.. batch]);
                return ValueTask.CompletedTask;
            },
            maxBatchSize: 64,
            maxDelay: TimeSpan.FromMilliseconds(20),
            cancellationToken);

        await probe.PushAsync("only", "id-only", cancellationToken).ConfigureAwait(false);

        var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        await Assert.That(batch.Length).IsEqualTo(1);
        await Assert.That(batch[0].Message).IsEqualTo("only");
        await Assert.That(batch[0].Context!.MessageId).IsEqualTo("id-only");
    }

    private sealed class ProbeBroker<TMessage> : IMessageBroker
    {
        private Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask>? _handler;

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, MessageConsumeContext?, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
        {
            if (typeof(T) != typeof(TMessage))
                throw new InvalidOperationException($"ProbeBroker<{typeof(TMessage).Name}> cannot subscribe {typeof(T).Name}.");
            _handler = (Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask>)(object)handler;
            return new ValueTask<IAsyncDisposable>(NoopDisposable.Instance);
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => SubscribeAsync<T>((message, _, token) => handler(message, token), ct);

        public async ValueTask PushAsync(TMessage message, string messageId, CancellationToken ct)
        {
            var handler = _handler ?? throw new InvalidOperationException("No subscription registered.");
            await handler(
                message,
                MessageConsumeContext.FromHeaders(null, null, messageId),
                ct).ConfigureAwait(false);
        }

        public ValueTask PublishAsync<T>(T message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
