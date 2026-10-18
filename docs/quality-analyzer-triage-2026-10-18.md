# 第三方分析器逐条裁决（2026-10-18）

> 目标：把评估轮 642 条第三方分析器警告收敛到可执行基线，逐类做业务判断，而不是长期靠 `baseline` 掩盖。
> 结果：**642 → 0**（去重后），`scripts/quality-analyzer-baseline.txt` 归零；默认构建仍关闭分析器，CI `quality-analyzers` job 以 0 为棘轮。
> 评估背景见 `docs/quality-tooling-eval-2026-10-18.md`。

## 一、收敛路径

| 阶段 | 警告数 | 动作 |
|---|---:|---|
| 初始（三分析器全开） | 642 | 评估基线 |
| 规则子集（`.editorconfig`） | 280 | 关闭 9 条风格噪声，保留安全/正确性规则 |
| 自动修复（`dotnet format analyzers`） | 188 | MA0011/MA0021/MA0008 等机械修复 |
| 逐条修复与精确抑制 | **0** | 真实缺陷修复 + 站点级 `#pragma`/`SuppressMessage` + 规则级裁决 |

## 二、规则级与站点级裁决（业务判断）

| 规则 | 条数（子集后） | 裁决 | 理由 |
|---|---:|---|---|
| MA0015 参数名 | 73 | 关闭 | 配置解析异常的消息已含具体配置键/值，paramName 不增加定位信息；公共 API 参数名由 S927/S3928 守护 |
| S2077 SQL 注入模式 | 8 | 站点级抑制 | 保留规则（不全局关闭，避免 Semgrep 覆盖不到的 `FromSqlRaw` 形态失去纵深）；8 个已验证站点逐处 `#pragma` 并注明白名单/常量/参数化依据；Semgrep 为独立防线 |
| S1133 弃用代码 | 6 | 关闭 | 弃用窗口由 ADR-026 与 CHANGELOG 管理，分析器无法知道移除日期 |
| S1199 嵌套块 | 5 | 关闭 | SQL 租约/管线的嵌套块是可读性结构，不是缺陷 |
| S3267 LINQ 简化 | 4 | 关闭 | 与零分配/可读性纪律冲突，本仓优先显式循环 |
| MA0016 集合抽象 | 2 | 关闭 | 领域状态刻意使用具体集合类型（序列化/性能契约） |
| S2326/S3246 类型参数 | 6 | 关闭 | 类型参数是公共 API 契约，删除或加 `out` 会破坏兼容性 |

## 三、真实修复（代表性）

| 类别 | 处理 |
|---|---|
| MA0011 格式化提供程序 | 60 处加 `CultureInfo.InvariantCulture`（`ToString`/`TryParse`/`Convert`） |
| MA0021 字符串比较 | 18 处改 `StringComparer`/`StringComparison` |
| MA0008 结构布局 | record struct 补 `[StructLayout(LayoutKind.Auto)]`（默认布局，零行为变化） |
| MA0132 隐式 DateTimeOffset | 3 处改显式 `new DateTimeOffset(..., TimeSpan.Zero)`（修正 Unspecified 本地偏移风险） |
| MA0054 内层异常 | 2 处 `InvalidOperationException` 补 `innerException`；1 处无在捕异常的分支加精确 `#pragma` 说明 |
| MA0196 inheritdoc | 2 处改为实际 `<summary>` |
| S8949/S6966 取消传播 | 传递 `ct`/`RequestAborted` 到 `WriteAsJsonAsync`/`WriteLineAsync`/`QueueDeleteAsync`；`Cancel`→`CancelAsync`、`Dispose`→`DisposeAsync`；刻意 fire-and-forget 的 4 处加 `#pragma` 说明 |
| S8969 空 forgiven | 11 处移除 `!`（编译器已证明非空） |
| S927 参数名 | `msg`→`message` 4 处，对齐接口声明 |
| S1144/S4487 未用成员 | 删除 `EnsureOpen`/`_connection`/`_dialect`/`_timeProvider` 等未读成员；反射物化的 init 访问器/诊断属性加精确 `#pragma` |
| S1905/S2325/S2696/S3011/S2743/S2094/S108/S2953/S3427/S6672/S3928/S4136 | 逐站点修复或加 `#pragma`/`SuppressMessage`，理由随代码注释 |

## 四、站点级抑制原则

1. 只抑制**已证明非缺陷**的形态：反射物化、ThreadStatic 池、AOT 非目标反射、配置值异常、刻意 fire-and-forget。
2. 每条抑制必须写一行理由；禁止无理由 `#pragma`。
3. `#pragma` 只包住最小作用域（单条语句/单个方法），不整文件关闭（JsonMessageSerializer 的 ThreadStatic 池是唯一文件级例外，类头已有重入声明）。
4. 能用真实测试锁定的，优先补测试而不是抑制：本轮 CompressionProvider 的 6 个变异体由新增守卫测试杀掉，Stryker 分数 40% → **100%**。
5. 同时使用 Sonar `#pragma` 与 Semgrep `nosemgrep` 时，`nosemgrep` 必须紧邻被检代码行；把 `#pragma` 放在它前面（本轮曾因插在中间导致 Semgrep 4 条回归，重排后清零）。


## 五、验证

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -p:EnableQualityAnalyzers=true -p:TreatWarningsAsErrors=false` | 0 警告 |
| `scripts/quality-baseline.cs` | 基线 0，棘轮 0 == 0 |
| 默认 `dotnet build -c Release` | 0 警告 0 错误 |
| 16 个测试项目 | 全部通过（Compression 46/46，Stryker 100%） |
| CI | `quality-analyzers` job 以 0 为基线；`semgrep` job 0 findings；`mutation` job 手动触发 |
