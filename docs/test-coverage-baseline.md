# 测试覆盖率基线

> **基线日期**：2026-07-30
> **基线 commit**：`db47e22`（feature/palorm-adapter）
> **测试总数**：850（全绿）——**基线时点值**
> **当前规模**（2026-09-04 v2.1.0）：16 项目 1202 用例（本机 1153 通过 + 49 环境依赖项由 CI Testcontainers 执行）；本文余下覆盖率数字均为 2026-07-30 基线，未随测试增长重测
> **覆盖率工具**：dotnet-coverage 18.9.0 + ReportGenerator 5.5.11

## 总览

| 指标 | 值 |
|------|:--:|
| 行覆盖率 | **67.9%**（5895 / 8672） |
| 方法覆盖率 | 68%（749 / 1101） |
| 完全覆盖方法 | 58.2%（641 / 1101） |

## 按模块覆盖率

| 覆盖率 | 模块 |
|:--:|------|
| 90%+ | Projections 98.6% · MemoryPack 100% · EventLog 92.3% · Idempotency 93.7% · Serialization 94.6% · PalORM.Core 89.6% |
| 80-90% | Analyzers 87% · CQRS 87.3% · Core 86% · Messaging 86.5% · RabbitMQ 89.6% · Kafka 81.2% · Idempotency.EFCore 91.1% |
| 60-80% | Dapper 75.5% · DI 70% · Repository.EFCore 77.4% · EventLog.EFCore 78.8% · Projections.EFCore 88% |
| 40-60% | Transactions 59.1% · Transactions.EFCore 51.5% · Projections.EventLog 41.8% · Core.SourceGen 42.4% |
| < 30% | Hosting.AspNetCore 30% · Dapper.PostgreSql 11.2% · PalORM.MySql 4% · PalORM.PostgreSql 4% |

## 门禁阈值

- **全局行覆盖率不低于 65%**（阈值口径：2026-07-30 基线 67.9% - 3% 缓冲）。
  实现脚本为 **`scripts/ci-coverage.cs`**（2026-09-11 由 `ci-coverage.sh` 迁移为
  C# file-based app）：build → 逐项目 `test --coverage` → ReportGenerator 合并 →
  解析合并 Cobertura 的 `line-rate` → 低于阈值退出 1（fail-closed）。阈值可经
  `COVERAGE_THRESHOLD` 环境变量覆盖；脚本自带 `--selftest`。

  **状态（2026-09-13 实测修订）**：脚本**就绪但未接入 CI**——`.github/workflows/`
  中 grep `coverage` 零命中。此前本行表述为「已自动化」，与实际不符（脚本可运行
  ≠ 门禁在运行），已按实测改写。接线未完成的**两个具体阻塞**（非仅"耗时长"）：

  1. **本机无法产出全局数字**：`PalDDD.PalORM.Tests` 的 46 项多方言测试要求
     Testcontainers（`MultiDialectFixture.EnsureTestcontainersRequired`，
     `test/PalDDD.PalORM.Tests/MultiDialectFixture.cs:92`），本机 Docker 不可用 →
     脚本在该项目处中断，实测仅 **12/16** 测试项目产出 cobertura。全局 line-rate
     **本机不可测**。
  2. **阈值口径未重测**：0.65 锚定 2026-07-30 的 67.9%（见本文首部说明），而项目
     此后已增长到 37 个 src 项目 / 16 个测试项目。阈值现在可能**恒真**（实际远高于
     0.65，门禁永不触发）或**恒假**（实际低于 0.65，接上即全红），两种情况都不可
     直接上线。

  **接线前置**（按序）：① 在具备 Docker 的环境（CI 或本地启用 Testcontainers）
  跑一次 `dotnet run scripts/ci-coverage.cs` 取真实 line-rate；② 依据实测值重新
  校准阈值（并记录取值依据）；③ 在 `ci.yml` 的 `build-and-test` job 追加覆盖率
  步骤；④ 同提交更新本行状态与 `AGENTS.md` §2 门禁表。
  在①完成前**不得接线**——否则是为过门禁而调门禁。

- **单模块不允许从当前值下降超过 5%**——未自动化（需逐模块基线快照），靠评审轮人工
  核对上表。自动化的可行路径：`scripts/ci-coverage.cs` 已逐项目产出
  `TestResults/coverage.<项目名>.cobertura.xml`，可解析各文件 `line-rate` 与本表
  基线比对，缺基线项（新增模块）按"建立基线"处理而非判失败。

## 覆盖率低的已知原因（非缺陷）

| 模块 | 低覆盖率原因 | 是否需补测试 |
|------|------------|:--:|
| Core.SourceGen (42.4%) | 源生成器编译时执行，运行时覆盖率工具测不到 | ✅ P1 Verify 快照 |
| 各模块 ServiceCollectionExtensions (0%) | DI 胶水代码，架构边界测试已覆盖注册正确性 | ❌ 不需要 |
| PalORM 方言包 (4-16%) | 跨方言测试走泛型基类，不命中具体方言固化类 | ⚠️ P3 评估 |
| Dapper.PostgreSql (11.2%) | 分片/路由/JSON 类为高级特性，尚未投入使用 | ⚠️ 待定 |
| Testing (53.4%) | 测试基础设施自己测自己意义有限 | ❌ 不需要 |

## 真正需要补测试的运行时热点

| 优先级 | 模块 | 类 | 当前覆盖率 |
|:--:|------|------|:--:|
| P1 | Core.SourceGen | EnumGenerator / IdentityGenerator | 0% |
| P2 | Transactions | DefaultSagaManager / ChildSagaStep / DynamicStep / InterruptStep | 0% |
| P2 | Projections.EventLog | EventStreamJsonLines | 0% |
| P3 | Hosting.AspNetCore | EndpointExtensions | 3.2% |
