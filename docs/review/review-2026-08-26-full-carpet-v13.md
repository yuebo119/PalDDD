# 全量评审报告 v13（875e4b8 基线 · 收束后新循环首轮）

> 类型：全量档（五片并行地毯）· 链路第 8 轮

## 1. 覆盖度

- 机械轴全绿：gate 24/24、test-gate、doc 10/10、弱断言 153<173、verify-ai 21/21、Core 246。
- 逐行覆盖 176 文件：A 33（含新 LeaseOwnerFactoryTests）/ B 38 / C 44 / D 67+8csproj / E 50，五片零跳读声明。
- 方言轴 SKIP（双库握手超时持续，环境性——第四轮同结论，待 CI）。
- 跨片不变式：近两提交改动点（Saga 隔离三命中/FanOutStep 双守卫/LeaseOwner 测试在位）全部核实在位。

## 2. 发现汇总（主线程终审）：**P0=0 · P1=0 · P2=0** · P3×14

**六轮以来首次全量档静态零 P2**——v8-3→v9-2→v10-1→v11-2→v12-0（能力实证）→ **v13-0（全量）**。

### P3 清单（按族，主线程亲验关键项）

| 族 | 项 |
|----|----|
| **上轮修复残留**（2） | StrategicDddAnalyzer 主文件残留 2 个 unused using（CSharp/CSharp.Syntax——拆分时继承，v11 只删了 Globalization 漏此二）【亲验属实】；CodeFixHelpers 7 个 unused using |
| **注释-文档失实**（1） | 三方言包 csproj 声称"MySql/PG AOT publish 缺口 docs/aot.md 已声明"——aot.md 零该声明（缺口真实存在但未落文档）【亲验属实】 |
| **姊妹对称**（4） | Rabbit AsyncSubscription 句柄级无幂等门（Broker 级有）；InMemoryOutbox AddMessagesAsync 单条 null 不对称；OutboxDbContext.AddMessage 单条无守卫；Inbox 截断存储层第二层不对称（TEXT 列兜底风险低） |
| **行为边界声明**（3） | OnStatusChanged try-catch 仅同步半面（异步 Sink 故障落入丢弃 ValueTask 无 Activity——CAP-2 的残余半面）；ProcessEventAsync 入口 pragma 吞 IL2026/IL3050（项目级 false 兜底）；MySQL 租约跨栈分叉（PalORM JOIN last-writer-wins vs EFCore SKIP LOCKED——未声明取舍 ❓） |
| **覆盖/措辞**（2） | CodeFix 的 ProjectionName 查找不沿基类链（诊断报但 fix 不注册）；PipelineStateMachine remarks"对象池"与每请求 new 张力 |
| **观察项**（2） | Dapper SqlServer 死分支口径；Native 压缩超分配数组不裁剪 |

## 3. 七流结论

五片各自附零发现流证据（架构/安全/资源/并发/错误/AOT/生成语义）。历史修复全部核实在位：幽灵 Added 四形态、幂等三栈契约、SafeObserve 族（含新补偿隔离）、readonly 三方闭环、LeaseOwner 互异防线、400 形态统一。

## 4. 修复清单（P3×14，按建议优先级）

1. **unused using×9**（2 文件，10 分钟）——若未来启用 IDE0005 门禁将升 P2
2. **csproj→aot.md 声明补齐**（把"MySql/PG 方言 publish 未验证"真实写入 aot.md，或修注释）
3. **姊妹守卫×3**（AddMessage 单条 null×2 + Rabbit 句柄幂等门）
4. **OnStatusChanged 异步半面**（ContinueWith 记 Activity 或 doc 声明半面性）
5. **MySQL 租约分叉声明**（csproj/remarks 声明 PalORM MySQL 取舍，或 dialect-probe 扩 steal-vs-skip 断言——CI）
6. CodeFix 基类链 / PipelineStateMachine 措辞 / 观察项×2（斟酌处置）

## 5. 趋势与协议状态

静态连续零 P2（v12 焦点轮 + v13 全量轮）。能力实证已于 v12 完成（1/1 全抓+盲区焊死）——不重复触发。**协议状态：静态收敛 + 能力验证 + 盲区焊死三重达成，评审-修复循环处于稳定收束态**；剩余 P3 全为卫生/对称/声明级。
