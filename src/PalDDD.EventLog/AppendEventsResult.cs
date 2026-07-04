// ─────────────────────────────────────────────────────────────
// 📊 AppendEventsResult — 追加操作的结果值对象
// ─────────────────────────────────────────────────────────────
namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 追加事件的结果
// ─────────────────────────────────────────────────────────────

/// <summary>向流追加事件的结果。</summary>
public readonly record struct AppendEventsResult(
    string StreamName,
    long FirstStreamVersion,
    long LastStreamVersion,
    long FirstGlobalPosition,
    long LastGlobalPosition);
