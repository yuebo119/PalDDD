# 全量评审报告 v16（63bceee 基线 · 第二循环首轮）

> 类型：全量档（五片并行地毯）· 链路第 11 轮

## 1. 覆盖度

机械轴全绿（gate 24/24、test-gate、doc 10/10、弱断言 153<173、verify-ai 21/21、Core 246/DI 94）；五片逐行 176 文件（A 34 含 SagaTests / B 38 / C 35 / D 62 / E 61）；方言轴第五轮环境性 SKIP；近三轮修正点横向核实（OnlyOnFaulted×4/await 化/裁剪×3 全在位）。

## 2. 发现汇总（主线程终审）：P0=0 · P1=0 · **P2×5** · P3×19

### P2 定稿（全部主线程亲验或反证充分）

| # | 位置 | 缺陷 | 修复方向 |
|---|------|------|---------|
| P2-1 | SagaTests.cs:175（v15 遗留） | **v15 异步测试未走声称路径**——EventSpecificCompensationSaga 只注册普通步骤走 SafeObserve 族，不触 OnStatusChanged（唯一调用点 :669 在 InterruptStep）；ContinueWith 路径零测试覆盖（若再丢 OnlyOnFaulted 无测试会红）+ 注释失实（PD29）【亲验】 | 补 InterruptStep+AsyncThrowingSink 测试（断言 AwaitingHumanDecision 正常返回无逃逸）；勘正 v15 测试注释（其覆盖 SafeObserve 异步半面，价值保留） |
| P2-2 | PostgreSqlMultiHost 三入口 | **PG MultiHost 族缺 DbDataSource 双注册**——MySQL 姊妹三入口均有（ITM-167 声明该抽象注册是 WithStores 连接工厂前提）；failover 入口+自定义 DbConnection 工厂 → 解析 DbDataSource 抛【反证充分】 | 三入口补 `TryAddSingleton<DbDataSource>`；集成测试断言 |
| P2-3 | MySqlOutboxDbContext.cs:53-58 | 注释过时——声称"Mark* 是内存突变+SaveChangesAsync（仅 ReleaseForRetry 是 ExecuteUpdate）"，ITM-210 token 化后基类 Mark* 已是 FencedTarget+ExecuteUpdate 直写【亲验】 | 注释勘正（"回读保持跟踪"理由随之失效，现无害） |
| P2-4 | AddProjectionContextPrefixCodeFix:35-58 | **PDDD013 fix 的 BC 轴未对齐 analyzer（v13 修一半）**——字面量轴补了基类链，但 BC 从诊断节点祖先取（字面量在基类 B 时取到 B 的声明找 BC）；analyzer 已传 Properties["BoundedContext"]（Handlers.cs:87-88）**未被 fix 消费**——PDDD008 fix 消费同款属性（姊妹对称修复方向明确） | 改读 diagnostic.Properties（对齐 PDDD008）；补跨类型场景测试 |
| P2-5 | DependencyInjection csproj | **AOT 声明态无归属**——NoWarn IL3058+继承 true+依赖 ZLogger，不在 PD3 三态表任何列；PalOrmSample AOT CI 实际经过 AddPalDDD（实证可行） | 补三态表核心列（true，CI 实证）+ csproj 声明注释 |

### P3×19（按族摘要）

- **姊妹/守卫不对称**（7）：InboxDbContext Mark* 无 status 守卫（Dapper/PalORM 有）/ Outbox+PalORM GetPending 无 batchSize 守卫（Saga 族有）/ DI 扩展 7/8 缺 ThrowIfNull / Legacy Router Reader "any" 声明缺口 / SqliteFts 私有拼接种子 / OutboxSelectByLease 判据对称（已声明）/ EF 抢占边界分叉
- **测试/注释**（6）：PALMSG003 文案 / GetSemanticModelAsync 双取 / "NativeATO" 错拼 + "AOT-safe" 措辞 / ZStd 注释复制 LZ4 文案×2 / decoded null key / SafeObserve OCE 边界
- **行为观察**（6）：ContinueWith 不流动 ExecutionContext（Activity≈no-op）/ 批次 now 漂移 / OCE 滞留超时 / indexMap 重复引用 / Sqlite 翻页全扫 / PalOrmInbox 捕 Exception

## 3. 七流与近轮修正核实

五片零发现流各自附证；v13-v15 全部修正点核实在位且形态正确（亲核+横向）。v16 的 P2 特征：**三项是"上轮修复修了一半"的姊妹轴残留**（P2-1 测试走错路径 / P2-4 fix 只对齐一轴 / P2-3 注释没跟 token 化）——"修一半"模式成为新循环的主要缺陷源。

## 4. 修复清单

1. **P2-1**：InterruptStep+AsyncSink 测试 + v15 注释勘正
2. **P2-2**：PG MultiHost 三入口补 DbDataSource + 测试
3. **P2-4**：fix 改读 Properties["BoundedContext"]（对齐 PDDD008）+ 跨类型测试
4. **P2-3/P2-5**：注释勘正 + 三态表补列
5. P3×19 按族处置（守卫补齐优先）
