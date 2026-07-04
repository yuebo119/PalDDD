# ADR-015：Saga 实例 freeze 机制评估

> 状态：已采纳 · 日期：2026-07-01 · 关联：ITM-058

## 背景

`Saga.cs:108-109` 的 `GetFrozen()` 中使用 `_frozen ??= _stepsByKey.ToFrozenDictionary(...)` 非原子操作。文档已明确"步骤在构造函数中注册"，Saga 实例 Scoped 生命周期保证单请求内不并发。

## 决策

**维持现状，不增加 freeze 机制。**

## 理由

1. **Scoped 生命周期已提供并发保护**：Saga 实例由 DI 容器按请求创建，单请求内不存在多线程并发调用 `ProcessEventAsync` 的场景。
2. **`??=` 在单线程场景下足够安全**：首次调用 `GetFrozen()` 时，仅一个线程执行 `ToFrozenDictionary()`——Scoped 生命周期保证了这一点。
3. **ITM-042 已增加文档约束**：`When()` 方法的 XML doc 已明确"必须在启动期单线程调用，首次 `ProcessEventAsync` 调用后不得再添加步骤"。
4. **增加 freeze 机制的成本高于收益**：若在 `ProcessEventAsync` 首次调用后抛 `InvalidOperationException` 阻止后续 `When()` 注册，需要额外的 `bool _frozen` 字段和检查逻辑。当前约定已充分，额外的守护增加代码复杂度无实际保护价值。

## 不动

维持现状。ITM-042 的文档约束 + Scoped 生命周期已提供足够的保护。
