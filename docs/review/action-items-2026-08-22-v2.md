# 行动项 · 2026-08-22 v2（第四十轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v2）

> 来源：`docs/review/review-2026-08-22-full-carpet-v2.md`（commit b2bd64a 基线）。
> 上轮 ITM-242..261 已全部勾销（见 action-items-2026-08-22.md），本轮验证轮确认无假修无回退。

## 族 A · P0 安全（探针已实锤）

### [ ] ITM-262 · F1：ReadWriteRouter 副本缺 Host 异常消息内嵌完整连接串（含 Password）
- **位点**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs` 约 147-149 行。
- **探针证据**：凭据一致 + 缺 Host 的副本串 → ArgumentException 的 Message 含密码原文（报告 F1 详情；v1 探针被前置凭据守卫拦截的证伪记录一并保留）。
- **修复**：消息改为报副本索引（不内嵌 `'{cs}'`），或仅脱敏 Host 段；对齐同文件 ThrowIfCredentialsMismatch 的"只含角色名"形态。
- **传感器**：单元测试断言异常消息不含 Password 段（构造含密码的 host-less 副本串）。修复后探针复跑转绿。

## 族 B · P2 收尾（三项同源一次修）

### [ ] ITM-263 · F2：弱断言棘轮回归 186>173（b2bd64a 净增 15 冗余守卫行）
- **修法**：删除"IsNotNull() 后跟感叹号解引用属性断言"形态中的冗余 IsNotNull 行（行为覆盖不变——属性断言自身在 null 时失败）；逐文件处理至计数 ≤173（建议顺带压到更低，棘轮只降不升）。
- **验证**：`bash .ai/scripts/assertion-strength-check.sh` 转绿 + 全量测试零回归。

### [ ] ITM-264 · F3：Broker 轴对称收尾（上轮 F20 残留）
- `BrokerIntegrationTests.cs`：①Rabbit RoundTrips/HandlerCancellation 两测试补 `[NotInParallel("broker-integration")]`（MultipleMessages 已有）；②HandlerCancellation entered 等待 120s → 对齐 Kafka 侧 30s（或注释理由）；③Fixture.InitializeAsync 的 `_triedTestcontainers` 并行竞态加锁（防双启容器覆盖引用泄漏）。

### [ ] ITM-265 · F4：AotSample MarkProcessed 传原始引用 → 三方实现全部静默 no-op
- **位点**：`samples/PalDDD.AotSample/Program.cs` 约 55-57 行。
- **修法**：改传租约返回的 `pending[0]`（对齐 ECommerce/PalOrmSample 正确写法）；Check 补终验（MarkProcessed 后再 GetPending 应为 0）。修复后运行样本确认全流程 PASSED。

## 族 C · 三方一致残留（P3 优先两项，上轮修复的文档尾巴）

### [ ] ITM-266 · palorm-adapter.md:207 仍写"（AsyncLocal）挂接"——CWT 改造后措辞未同步
- 改为"（Session 键控 ConditionalWeakTable）挂接"，与 src 注释及 conventions §禁止新增 AsyncLocal 的既定规范对齐。

### [ ] ITM-267 · OutboxSqliteConcurrencyTests 类头注释描述已修复的缺陷（"Skip 缺陷固化等 src 修复"）
- ITM-261 已修复且 Skip 已移除——注释块更新为现状；4 处行内"见 Skip 说明"引用同步勘正。

## P3 池

~45 项已登记 `.ai/review/action-items-p3-backlog.md` 四十轮追加段（30 天老化→升 P2），本轮不展开。

## 完成定义

1. ITM-262 探针红转绿；ITM-263 棘轮 ≤173；ITM-265 样本运行 PASSED。
2. 全部修复自带回归测试；修复合入后 gate 22/22 + tech-debt 全绿 + dialect-probe 40/40 + 测试与基线 1041（996+45）一致。
3. 修复轮后跟验证轮（引擎协议）。
