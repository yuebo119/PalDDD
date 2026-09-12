# Pal.DDD 地毯式逐行评审 — 共享简报（第四轮 · MIG-011/012 脚本 C# 化后验证轮）

> 基线: 7eca6be（dev）· 本轮重点：**全仓脚本体系 C# 化后的首次全量审计**——27 个 file-based app（~5,500 行新 C#）+ 三轮修复与迁移（d8746a6..HEAD 全部变更面）的敌对复查。修复/迁移自带缺陷率历史 6-7.8%（v3 轮降至 0 语义错误但 7 同步遗漏）。
> 机械防线已全绿（CI 三 job + 本地全套）——全绿不豁免逐行。

## 本轮专项检查轴（七流之上）
1. **27 个 .cs 工具正确性**（PD29——迁移工具自身假绿/过严/路径坑）：等价性声称的双跑基准是否真实存在；Process 调用的退出码/stdout 读取正确性；仓库根定位的边界（仓外/子目录）；资源泄漏（未 Dispose 的 Process/reader）。
2. **三方一致性终态**：ci.yml ↔ scripts/*.cs ↔ .ai prompt ↔ docs/conventions 的命令引用闭环；已删 .sh/py 的零活引用。
3. **三轮变更面敌对复查**（git diff d8746a6..HEAD）：注释声称 vs 代码；姊妹同步完整性（fix-orchestrator ④段是否被真正消费）。

## 七流问题卡（速查）
架构（分层/命名空间/循环）· 安全（SQL 注入/PII/标识符白名单）· 资源（异常路径释放/CTS using/每分支清理/Process Dispose）· 并发（Freeze 后 Add/租约全路径释放/共享状态 Lock）· 错误（OCE 三合规形态/清理异常挂 Data/超时 vs 取消）· AOT（反射三态/IL3058/JsonTypeInfo/注释与实际一致）· 生成语义（AddSource 去重/converter 契约/多段 sequence）。

## 误判库速版（命中即降级）
通用1-8；PD1-PD39（含新 PD38 CI skip 假象/PD39 override 架空守卫）。关键：PD10-11 无 SyncContext sync-over-async；PD12-14 框架边界/设计限制/对齐非新 bug；PD17-18 姊妹枚举/PalORM 参数化；PD29 验证器自欺；PD34-37 自激振荡/嵌套盲区/驱动假设/轮次分级。

## 产出格式
| # | 文件:行 | 流 | 问题 | 触发路径 | 可信度 | P 初判 | 误判库 |
末附覆盖度自报。P0/P1 ✅+探针思路；P2 可 ❓ 标"修复前先补探针"。
