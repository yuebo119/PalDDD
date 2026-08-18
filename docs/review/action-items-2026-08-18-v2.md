# 行动项清单 2026-08-18 v2（第三十一轮全量评审）

> 来源：`docs/review/audit-2026-08-18-v2.md` · 基线 79eadee
> 优先级 = 危害 × 复杂度（conventions §13）

---

### [ ] ITM-200 · SagaProcessor 补偿失败 Error 无截断 · ✅
- **维度**：错误流 · **优先级**：[P2] · 危害: 高（无限重试循环 + 状态丢失） · 复杂度: 易
- **问题**：`sagaState.Error = ex.Message`（SagaProcessor.cs:154）无截断，Error 列 HasMaxLength(2048)。补偿异常（AggregateException/FanOut 聚合消息）超 2048 → SaveChangesAsync 抛 DbUpdateException → 被 :196 外层 catch 吞 → Saga 停留 Processing、租约过期重租重补偿 → 无限循环且 CompensationFailed 永不落库。InboxDbContext 截断 2000 / OutboxDbContext 2040，Saga 路径缺席（PD24 截断族第三处漏网）。
- **建议**：赋值前截断（对齐 2040 兜底），Dapper/PalORM SagaStateStore 三方同步枚举（PD17 姊妹）。
- **验证**：附测试——超长补偿异常消息后 Saga 落库 CompensationFailed（不抛 DbUpdateException）。

### [ ] ITM-201 · CSV 公式注入无防护 · ✅
- **维度**：安全流 · **优先级**：[P2] · 危害: 中（报表导出场景公式执行） · 复杂度: 易
- **问题**：`EscapeCsvSpan`（PostgreSqlReportHelper.cs:270-275）仅处理逗号/引号/换行，`= + - @ \t` 前缀不设防（OWASP CSV Injection）。events 的 reason/actor_id 等操作者输入经报表导出后在 Excel 打开触发公式。
- **建议**：对 `= + - @ \t` 开头单元格前置 `'` 或强制引号包裹，附测试。

### [ ] ITM-202 · 时间语义 3 项 · ⚠
- **优先级**：[P3] · PalOrmIdempotency/Checkpoint 读侧 GetDateTime Kind 漂移（[推断] 修复前先补探针）；PalOrmSaga ReadSagaRow 同型；DomainEvent/SagaState Ulid 默认路径不受 TimeProvider（文档声明或接 IPalIdGenerator）

### [ ] ITM-203 · 截断/边界 2 项 · ⚠
- **优先级**：[P3] · retriedBy audit 无长度上限（三实现均匀，需 PD17 枚举）；PostgreSqlJsonb 空数组 `array[]` 类型推断失败（入口守卫）

### [ ] ITM-204 · 并发/生命周期 4 项 · ⚠
- **优先级**：[P3] · KafkaBroker Dispose 无订阅拦截（_disposed 标志）；InMemoryOutbox GetPending / InMemoryInbox Mark 无 ct（姊妹对称）；ServiceRegistration 双实例（先 PD32 容器探针）

### [ ] ITM-205 · 声明失真 5 项 · ⚠
- **优先级**：[P3] · DapperEventLog "流式"注释（改 buffered:false 或修注释）；ProjectionProcessor Zero 替换（改 TimeSpan? 参数）；HandlerNotFound 头注释 400→404；PalOrm sync-over-async 隐式契约文档化；NameAndVersionComparer 自反性修复

### [ ] ITM-206 · 监控/生成/配置 4 项 · ⚠
- **优先级**：[P3] · Inbox/Idempotency handler OCE 传播补计数；EnumGenerator 非 TSelf 字段诊断；mode=memory 参数精确解析；CodeFix 首个字面量用 diagnostic.Location

### [ ] ITM-207 · 其他 4 项 · ⚠
- **优先级**：[P3] · EventLogReplaySource 计数语义注释；SagaCompensation ReferenceEquals 锁定；EndpointExtensions 裸 400 统一 ProblemDetails；BulkCopy extractor 首行双调用（probe 结果缓存复用）

---

## 下沉审查（P0/P1 收口）

| ITM | 可下沉为 |
|-----|---------|
| ITM-200 | 截断族机械枚举——tech-debt 建议新增：Error 列写入点（MarkFailed/Error=ex.Message/audit）全仓 grep 截断兜底 |
| ITM-201 | CSV 注入前缀检测可下沉 assertion/tech-debt grep（EscapeCsv 系列方法） |

## 完成回填（修复后逐项勾选）
