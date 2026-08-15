// 🔄 EventLogReplaySource — 从 EventLog 回放事件源
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Diagnostics;
using PalDDD.EventLog;
using PalDDD.Serialization;
using System.Diagnostics;
using System.Globalization;

namespace PalDDD.Projections.EventLog;

/// <summary>从事件日志流回放事件以生成投影重建事件。</summary>
/// <typeparam name="TMessage">回放源生成的消息类型。</typeparam>
public sealed class EventLogReplaySource<TMessage> : IEventReplaySource<TMessage>
{
    private readonly IEventLog _eventLog;
    private readonly IMessageSerializer _serializer;
    private readonly MessageDescriptor _descriptor;

    /// <summary>创建回放源，从指定名称的事件流中读取事件。</summary>
    public EventLogReplaySource(
        IEventLog eventLog,
        IMessageSerializer serializer,
        MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.ClrType != typeof(TMessage))
        {
            throw new ArgumentException(
                $"Descriptor CLR type '{GetTypeName(descriptor.ClrType)}' does not match replay message type '{GetTypeName(typeof(TMessage))}'.",
                nameof(descriptor));
        }

        _eventLog = eventLog;
        _serializer = serializer;
        _descriptor = descriptor;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReplayEvent<TMessage>> ReadAsync(
        string sourceName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = PalActivitySource.StartEventReplayRead(sourceName, _descriptor.Name, GetTypeName(typeof(TMessage)));
        var read = 0;

        await foreach (var recorded in _eventLog.ReadStreamAsync(sourceName, cancellationToken: ct).ConfigureAwait(false))
        {
            ReplayEvent<TMessage> replayEvent;
            try
            {
                replayEvent = CreateReplayEvent(recorded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordReplayFailure(activity, read, ex);
                throw;
            }

            yield return replayEvent;
            checked { read++; }
        }

        activity?.SetTag("pal.replay.read_count", read);
        PalMetrics.ReplayRead.Add(read);
    }

    private ReplayEvent<TMessage> CreateReplayEvent(RecordedEvent recorded)
    {
        EnsureContractMatches(recorded);

        object? message;
        try
        {
            message = _serializer.Deserialize(recorded.Payload.Span, _descriptor);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EventReplayException(
                $"Event '{recorded.EventName}' payload at '{recorded.StreamName}' version {recorded.StreamVersion} could not be deserialized as '{GetTypeName(typeof(TMessage))}'.",
                ex);
        }

        if (message is not TMessage typedMessage)
        {
            throw new EventReplayException(
                $"Event '{recorded.EventName}' payload at '{recorded.StreamName}' version {recorded.StreamVersion} deserialized as '{GetTypeName(message?.GetType() ?? typeof(object))}', expected '{GetTypeName(typeof(TMessage))}'.");
        }

        return new ReplayEvent<TMessage>(
            recorded.StreamName,
            recorded.StreamVersion.ToString(CultureInfo.InvariantCulture),
            recorded.RecordedAt,
            typedMessage,
            ToReplayAudit(recorded.Audit));
    }

    private static void RecordReplayFailure(Activity? activity, int read, Exception exception)
    {
        activity?.SetTag("pal.replay.read_count", read);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        PalMetrics.ReplayFailed.Add(1);
    }

    private static ReplayAuditMetadata ToReplayAudit(EventAuditMetadata audit)
        => new(
            audit.ActorId,
            audit.Reason,
            audit.CorrelationId,
            audit.CausationId,
            audit.TraceParent,
            audit.TraceState);

    /// <summary>
    /// 校验流内事件与本回放源的 descriptor 契约一致。
    /// <para>📐 设计约束（P2 定案，刻意行为）：本回放源要求<b>每流单事件类型</b>——
    /// 投影重建需要固定的 EventName+SchemaVersion+ContentType 契约来反序列化。
    /// 异构事件流（一个聚合流内多事件类型）会在第一个不匹配事件处抛
    /// <see cref="EventReplayException"/>。需要回放异构流时，按 EventName
    /// 预过滤后为每种事件类型创建独立的回放源。</para>
    /// </summary>
    private void EnsureContractMatches(RecordedEvent recorded)
    {
        if (!StringComparer.Ordinal.Equals(recorded.EventName, _descriptor.Name))
        {
            throw new EventReplayException(
                $"Recorded event name '{recorded.EventName}' does not match descriptor name '{_descriptor.Name}' at '{recorded.StreamName}' version {recorded.StreamVersion}.");
        }

        if (recorded.SchemaVersion != _descriptor.SchemaVersion)
        {
            throw new EventReplayException(
                $"Recorded event schema version '{recorded.SchemaVersion}' does not match descriptor schema version '{_descriptor.SchemaVersion}' at '{recorded.StreamName}' version {recorded.StreamVersion}.");
        }

        if (!StringComparer.Ordinal.Equals(recorded.ContentType, _descriptor.ContentType))
        {
            throw new EventReplayException(
                $"Recorded event content type '{recorded.ContentType}' does not match descriptor content type '{_descriptor.ContentType}' at '{recorded.StreamName}' version {recorded.StreamVersion}.");
        }
    }

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;
}
