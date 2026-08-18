# 行动项清单 2026-08-18（第三十轮全量评审）

> 来源：`docs/review/audit-2026-08-18-full-review.md` · 基线 5d2acde
> 优先级 = 危害 × 复杂度（conventions §13）

---

### [ ] ITM-191 · IdempotencyProcessor 未对齐 ITM-180（副作用重放） · ✅
- **维度**：错误流 · **优先级**：[P1] · 危害: 高（副作用二次执行） · 复杂度: 易（<1h）
- **问题**：`MarkCompletedAsync`（IdempotencyProcessor.cs:71）在 handler 成功后调用，若抛非 OCE（DB 瞬断）落入通用 catch（:76）→ `MarkFailedAsync` 把**已成功执行**的记录降级为 Failed → Failed 可重入（`CanStartNewExecution`）→ handler 重放 → 副作用二次执行。InboxProcessor 同位置已有 ITM-180 修复（单独 catch + 记区分性日志 + 按成功返回）。Idempotency 是三兄弟（Inbox/Outbox）未对齐者。
- **建议**：镜像 ITM-180——`MarkCompletedAsync` 包内层 try/catch（`when not OCE`），失败记 Error 日志 + activity 标记 "completed-pending-confirmation" 后返回 Executed，不得进 MarkFailedAsync 分支；MarkFailedAsync 自身异常挂 `ex.Data`（镜像 InboxProcessor ITM-092）。
- **验证**：附测试——MarkCompleted 抛异常时 ExecuteAsync 返回 Executed 且记录不被标 Failed。

### [ ] ITM-192 · DapperSagaStateStore SQLite 分支缺类型限定 · ✅
- **维度**：错误分类 · **优先级**：[P2] · 危害: 中（误判掩盖真实数据错误） · 复杂度: 易
- **问题**：`IsUniqueConstraintViolation`（:222-227）SQLite 分支裸消息匹配 `Contains("UNIQUE constraint")`，无 `typeName.Equals("SqliteException")` 限定——DapperEventLog 已在 ITM-188 修复，本处是 PD17 姊妹漏网。任意 DbException 消息含该词被误判 → 转 `InvalidOperationException("被并发实例同时创建")`。
- **建议**：补 `typeName.Equals("SqliteException", StringComparison.Ordinal)` 前置限定，对齐 DapperEventLog/其余姊妹。

### [ ] ITM-193 · ProjectionCheckpointDbContext SQLite 分支缺类型限定 · ✅
- **维度**：错误分类 · **优先级**：[P2] · 危害: 中（误判 → 租约静默让出） · 复杂度: 易
- **问题**：`IsUniqueConstraintViolation`（:236-246）同型裸匹配 + 过时 "P3-3 已知局限" 声明——全仓姊妹统一修复后此声明已过时，且缺限定使误判时 `TryCreateCheckpointAsync` 返回 null、租约被静默让出。
- **建议**：补 SqliteException 限定 + 删除过时 P3-3 声明。

### [ ] ITM-194 · 注释/声明三方不一致（5 处） · ⚠
- **优先级**：[P3] · PostgreSqlMultiHost "覆盖 MaxAutoPrepare" 旧注释与 W2 条件化矛盾 ×2；PostgreSqlJsonbExtensions EscapeSqlLiteral doc 滞后；PipelineBehavior "零闭包" 声明过宽；MessageCatalog 单类型单描述符契约未文档化

### [ ] ITM-195 · public 入口守卫缺失（5 处） · ⚠
- **优先级**：[P3] · SqliteJsonExtensions.OutboxByType、SqliteFtsExtensions 全文件、SqlitePerformanceOptimizer.GetDiagnosticsAsync、DapperBulkCopy extractor 长度、四家 leaseDuration 非负

### [ ] ITM-196 · 资源释放边界（3 处） · ⚠
- **优先级**：[P3] · Sqlite Scoped 工厂抛异常连接未 Dispose（DbConnection finalizer 兜底）；KafkaBroker Dispose 前无 Flush（[推断]，P3 修复前先补探针）；SystemCompressor GZip 输入整块拷贝

### [ ] ITM-197 · 错误流/指标盲区（4 处） · ⚠
- **优先级**：[P3] · ExceptionMiddleware HasStarted 分支无日志；InboxProcessor pending-confirmation 无指标；DapperBulkCopy MySQL 非原子+RowsInserted 未检；MessageConsumeContext 判空语义边界

### [ ] ITM-198 · 生成语义/CodeFix 边界（5 处） · ⚠
- **优先级**：[P3] · IdentityGenerator CS0282；嵌套 containing 非 partial → CS0260；CodeFix 基类链不对称；MatchEventNameCodeFix 首个字面量；AddVersionSuffix v2.v3 残留

### [ ] ITM-199 · 其他（5 处） · ⚠
- **优先级**：[P3] · OutboxInterceptor Detach 扩面；DapperEventLog 读头"流式"注释不符；LoggingBehavior 查询打 Command 日志；DomainEventDispatcher 中英混排；Saga Dynamic 观察者口径

---

## 下沉审查（P0/P1 收口）

| ITM | 可下沉为 |
|-----|---------|
| ITM-191 | 管线孪生对称——tech-debt-scan #18 已覆盖截断族，另建议 Command 行为失败语义抽查入修复清单 |
| ITM-192/193 | SQLite 类型限定——可下沉 tech-debt 机械 grep（9 处 IsUniqueConstraintViolation 的 SQLite 分支必须有 SqliteException）：新增 #23 守卫 |

## 完成回填（修复后逐项勾选）
