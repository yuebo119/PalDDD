namespace PalDDD.Transactions;

/// <summary>收件箱消息状态</summary>
public enum InboxStatus
{
    Pending,
    Processing,
    Processed,
    Failed
}
