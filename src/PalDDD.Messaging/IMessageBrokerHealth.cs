namespace PalDDD.Messaging;

/// <summary>
/// 可选能力：broker 的自述消费链路健康状态（供健康检查/运维探针读取）。
/// </summary>
/// <remarks>
/// broker 实现本接口后，<c>AddPalHealthChecks</c> 的 <c>message_broker</c> 检查会读取
/// <see cref="IsConsumingHealthy"/>：消费链路不可恢复地终止时返回 false，健康端点随之转为
/// Unhealthy（原实现只断言「已注册」，消费停摆时仍恒绿）。
/// 未实现本接口的 broker（含默认 <see cref="NullMessageBroker"/>）保持「注册即健康」语义。
/// <para>
/// <b>两栈的故障面不同源</b>（v4 批 C，2026-11-07）：Kafka 以「消费线程不可恢复终止」置位；
/// RabbitMQ 是 per-delivery <c>ReceivedAsync</c> 回调模型，没有消费线程概念，只能以
/// 「channel 关闭」置位。两栈的共同契约是「<b>不可恢复</b>才置位」——普通 handler 失败
/// （毒消息按 at-most-once 弃置）<b>不置位</b>，因此健康端点不会因单条消息失败而变红。
/// </para>
/// </remarks>
public interface IMessageBrokerHealth
{
    /// <summary>消费链路当前是否健康；false 表示消费已停止或处于不可恢复故障态。</summary>
    bool IsConsumingHealthy { get; }

    /// <summary>最近一次终止/故障原因（健康时为 null；不含凭据）。</summary>
    string? LastFaultReason { get; }
}
