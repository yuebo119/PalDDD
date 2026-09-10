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

- **全局行覆盖率不低于 65%**（基线 67.9% - 3% 缓冲）——已自动化：`ci-coverage.sh` 第 5 步解析合并 Cobertura 的 line-rate，低于阈值退出 1（fail-closed，评审 P1-2 修复——此前文档声称门禁但从未实现）。阈值可经 `COVERAGE_THRESHOLD` 环境变量覆盖。CI 流水线集成（ci.yml 调用）为后续项：全量覆盖率收集显著拉长流水线，需维护者裁决。
- **单模块不允许从当前值下降超过 5%**——未自动化（需逐模块基线快照），靠评审轮人工核对上表；如需自动化需先建立模块级基线文件。

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
