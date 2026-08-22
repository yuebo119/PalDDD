# 行动项 · 2026-08-22 v2（第四十轮全仓地毯 + 验证轮，REVIEW-2026-08-22-v2）

> 来源：`docs/review/review-2026-08-22-full-carpet-v2.md`（commit b2bd64a 基线）。
> 上轮 ITM-242..261 已全部勾销（见 action-items-2026-08-22.md），本轮验证轮确认无假修无回退。

## 族 A · P0 安全（探针已实锤）

### [x] ITM-262 · F1：ReadWriteRouter 副本缺 Host 异常消息内嵌完整连接串（含 Password）
- **位点**：`src/PalDDD.Dapper.PostgreSql/PostgreSqlReadWriteRouter.cs` 约 147-149 行。
- **探针证据**：凭据一致 + 缺 Host 的副本串 → ArgumentException 的 Message 含密码原文（报告 F1 详情；v1 探针被前置凭据守卫拦截的证伪记录一并保留）。
- **修复**：消息改为报副本索引（不内嵌 `'{cs}'`），或仅脱敏 Host 段；对齐同文件 ThrowIfCredentialsMismatch 的"只含角色名"形态。
- **传感器**：单元测试断言异常消息不含 Password 段（构造含密码的 host-less 副本串）。修复后探针复跑转绿。

## 族 B · P2 收尾（三项同源一次修）

### [x] ITM-263 · F2：弱断言棘轮回归 186>173（b2bd64a 净增 15 冗余守卫行）
- **修法**：删除"IsNotNull() 后跟感叹号解引用属性断言"形态中的冗余 IsNotNull 行（行为覆盖不变——属性断言自身在 null 时失败）；逐文件处理至计数 ≤173（建议顺带压到更低，棘轮只降不升）。
- **验证**：`bash .ai/scripts/assertion-strength-check.sh` 转绿 + 全量测试零回归。

### [x] ITM-264 · F3：Broker 轴对称收尾（上轮 F20 残留）
- `BrokerIntegrationTests.cs`：①Rabbit RoundTrips/HandlerCancellation 两测试补 `[NotInParallel("broker-integration")]`（MultipleMessages 已有）；②HandlerCancellation entered 等待 120s → 对齐 Kafka 侧 30s（或注释理由）；③Fixture.InitializeAsync 的 `_triedTestcontainers` 并行竞态加锁（防双启容器覆盖引用泄漏）。

### [x] ITM-265 · F4：AotSample MarkProcessed 传原始引用 → 三方实现全部静默 no-op
- **位点**：`samples/PalDDD.AotSample/Program.cs` 约 55-57 行。
- **修法**：改传租约返回的 `pending[0]`（对齐 ECommerce/PalOrmSample 正确写法）；Check 补终验（MarkProcessed 后再 GetPending 应为 0）。修复后运行样本确认全流程 PASSED。

## 族 C · 三方一致残留（P3 优先两项，上轮修复的文档尾巴）

### [x] ITM-266 · palorm-adapter.md:207 仍写"（AsyncLocal）挂接"——CWT 改造后措辞未同步
- 改为"（Session 键控 ConditionalWeakTable）挂接"，与 src 注释及 conventions §禁止新增 AsyncLocal 的既定规范对齐。

### [x] ITM-267 · OutboxSqliteConcurrencyTests 类头注释描述已修复的缺陷（"Skip 缺陷固化等 src 修复"）
- ITM-261 已修复且 Skip 已移除——注释块更新为现状；4 处行内"见 Skip 说明"引用同步勘正。

## P3 池

~45 项已登记 `.ai/review/action-items-p3-backlog.md` 四十轮追加段（30 天老化→升 P2），本轮不展开。

## 完成定义

1. ITM-262 探针红转绿；ITM-263 棘轮 ≤173；ITM-265 样本运行 PASSED。
2. 全部修复自带回归测试；修复合入后 gate 22/22 + tech-debt 全绿 + dialect-probe 40/40 + 测试与基线 1041（996+45）一致。
3. 修复轮后跟验证轮（引擎协议）。

## 修复轮验证记录（2026-08-22 同日）

- **ITM-262（P0）**：消息改报副本索引不内嵌连接串原文 + 顺带 writer 失败路径释放（P3-SRC-202）；
  传感器 ×2（hostless 不含密码 + 姊妹凭据失配形态锁定）。红证据 = 探针 v2（修复前消息含
  Password=Sup3rS3cret!，报告 F1 在案）；绿证据 = 传感器 + Integration 188/188。
- **ITM-263**：脚本化删除"后跟感叹号解引用断言"的冗余 IsNotNull 守卫行 34 行——棘轮
  186 → **152 ≤ 173**（回到并低于原基线），六受影响项目全绿零行为变化。
- **ITM-264**：Rabbit 两测试补 NotInParallel（6/6 全覆盖）+ entered 等待 120s→30s 对齐 Kafka +
  InitializeAsync SemaphoreSlim 异步锁（夹具级兜底并行双启容器）。broker 本机可达 6/6 实测。
- **ITM-265**：MarkProcessed 改传 pending[0] + 新增终验（标记后 GetPending==0）；样本运行
  all checks passed——修复前该终验必红（片 8 证据链：三方实现均静默 no-op）。
- **ITM-266/267**：adapter.md AsyncLocal→CWT 措辞同步；测试类头注释勘正为"ITM-261 已修复"现状。
- **终基线**：build 0/0；测试 1043 = 998 通过 + 45 fail-closed + 0 代码失败（+2 传感器）；
  dialect-probe 40/40；机械六件套全绿（棘轮 152；gate 21/22 之 G22=待提交，提交后消除）。
