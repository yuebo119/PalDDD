# Pal.DDD 评审报告（第五十三轮 · 补覆盖验证轮）

> 报告编号：REVIEW-2026-09-19-FULL-V2
> 评审基准：commit `99f464f` → 清偿提交 `a616d38` · 补覆盖档（第五十二轮明确未读面 + 修复回归验证）
> 方法：快照锚定 → 机械轴（build/15 项目全绿+PalORM D1）→ 四片子代理逐文件 Read 全文（片A Messaging 独立栈+Shared+Hosting 遗漏 11 文件 / 片B 测试域第一批 46 文件 / 片C 测试域第二批+Testing 共享库 73 文件 / 片D 修复回归 8 项+23 份 ADR 全读）→ 主线程逐项复核 → 修复

---

## 执行摘要

**评审结论**：补覆盖轮聚焦第五十二轮声明的未读面（Messaging.Kafka/RabbitMQ/Shared/Hosting 遗漏、测试域 119 文件、23 份 ADR），全部真实读取。**0 P1 / 5 P2 / 24 P3（新）**；上轮 8 项修复回归 **7✅ 1❌**（❌ 为 SqliteRowFactory 类级 remarks 漏网，本轮补修）。发现主体集中在**测试基建自身**（FakeTimeProvider 竞态窗口、隔离声明误导）与**翻案传导缺口**（palorm-adapter 选型文档停留在 DapperAot 禁用旧口径）。

| 指标 | 本轮 |
|------|:--:|
| P0 / P1 / P2 | 0 / 0 / 5 |
| P3 新增 | 24（本轮修 8 项关联，余登记） |
| 上轮修复回归 | 7✅ 1❌（补修 ✅） |
| 源/测试/ADR 文件全文读取 | 11 + 119 + 23 + 回归验证面 |

## P2 发现与处置（5 项全部闭环）

| # | 来源 | 发现 | 处置 |
|---|------|------|------|
| 片C-1 | FakeTimeProvider `_now`/`_timestamp` 无同步（声明并发面内撕裂读/DueTime 旧时钟窗口） | 共享库最高杠杆 | ✅ 同步收口（读写全进锁，15/16 项目零回归） |
| 片C-2 | RecordingActivityListener「跨项目隔离」声明过强（同进程类级并行仍混入，误导省略 NotInParallel） | API 误用面 | ✅ remarks 勘正 + 强制范式注明 |
| 片D-1 | palorm-adapter.md:15 三轨表「DapperAot 实际禁用/维护遗留/假象」旧口径（翻案传导缺口，误导选型） | 文档 | ✅ 修正为已启用拦截器、能力平等栈 |
| 片B-1 | DialectProbe #16 断言「超时接管」实测为首插分支（新 id 不走超时参数——非区分性） | 测试语义 | ✅ 改对已 Processing 记录 timeout=0 重入 |
| 片D-延伸 | SqliteRowFactory:24 类级 remarks 仍「未启用」（上轮修复只改文件头） | 修复漏网 | ✅ 补修 |

## 本轮修复清单（9 项全部完成，a616d38）

上表 5 项 + AGENTS.md ADR 计数 22→23 + ADR-014/008 状态注记（演化超越/事实失实，ADR 惯例不改正文）+ Hosting csproj AOT 覆盖出处注记 + 同 tick 用例恒真断言行删除与守卫专测格式修正（片B 指出的本会话新增测试瑕疵）。

验证：build 0W/0E · 15/16 测试项目全绿（PalORM 为 D1 环境性在案）· verify-conventions/encoding-gate 全过。

## 正面结论（有分量的通过面）

- **第五十二轮 8 项修复**：7 项回归 ✅（逐项对照当前文本与实际代码顺序/接线）
- **第五十二轮新增测试自查**（新鲜眼光"反着写会红吗"）：12 车道表征用例 + 同 tick/批中取消/守卫专测——全部有真实区分性（片B 证词）
- **测试域整体质量高位**（片B/C 各自结论）：断言强度棘轮有负向自证、无未声明的假绿形态、Skip 全部实时环境探测无过时理由、契约漂移零发现
- **Messaging 独立栈**：生命周期/竞态路径的已知问题全部有声明；无凭据进日志；两栈继承 MessageBrokerBase 不受 DIM 丢 context 影响
- **ADR 抽核**：23 份全读，主要声明与代码现状一致（抽核通过清单见片D 产出）

## 行动项登记（ITM-799 起）

| ITM | 来源 | 项 | 窗口 |
|-----|------|-----|------|
| ITM-799 | 片A-P3-2 | EndpointExtensions 两个 MapCommand 重载约 40 行反序列化段收口（Wrong Abstraction 预警级） | 卫生批 |
| ITM-800 | 片A-P3-3 | HealthCheckExtensions ContentType 死代码验证与清理（WriteAsJsonAsync 覆盖行为需实测确认） | 卫生批 |
| ITM-801 | 片B-P3 簇 | 测试增强批：SagaProcessor ResumeAsync 消息子串/SagaTests IsTimedOut 步骤名断言/RepositoryEfCore 零断言方法三处/PG MultiHost 首调用结果丢弃/fencing 断言宽度统一 | 测试批 |
| ITM-802 | 片C-P3 簇 | EventLogReplaySource 并行注记/AotContract 相对路径范式/AssertionStrength 正则前瞻（含 `{` 的参数化属性）/FakeTimeProvider 负 delta 单调性 | 基建批 |
| ITM-803 | 片D-P3 | ADR 历史快照低价值勘误（ADR-005 版本号/ADR-006/009 引用位置） | 归档整理时 |

## 覆盖度终局（两轮合并）

第五十二轮（184 源文件）+ 第五十三轮（11 遗漏源 + 119 测试 + 23 ADR + 回归面）——src/ 214 文件、test/ 118 文件、docs/decisions 23 份已全部有真实读取覆盖；未覆盖残余仅 docs 散文（conventions/usage/tutorial 等非代码权威段）与 README，无代码面盲区。
