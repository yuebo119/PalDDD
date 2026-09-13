# EF Core 最新版新特性与 NativeAOT 支持调查

> 调查编号：EFCORE-AOT-2026-09-13
> 数据来源：Microsoft Learn 官方文档（What's New in EF Core 11 / NativeAOT Support and Precompiled Queries / EF 11 Breaking Changes）+ dotnet/efcore issues（#34446/#29754/#35945）
> 时点：2026-09-13，EF Core 11 处于 RC 阶段（本项目钉 11.0.0-rc.1.26425.128），GA 预计 2026-11；EF Core 10 已 GA（2025-11）

---

## 核心结论（先说答案）

1. **EF Core 11 的 NativeAOT 支持仍未转正**——官方警告原话（2026-09 拉取）："NativeAOT and query precompilation are highly experimental feature, and are not yet suited for production use"。与本项目 AOT 分析报告（aot-analysis-2026-09-13.md）结论一致，无新进展可改变该判断。
2. **跳票实证**：EF Core 9 文档曾承诺"we expect to stabilize it and make it more suitable for production usage in **EF 10**"——EF 10 未兑现，EF 11 依然 experimental。该特性已连续两个版本跳过稳定化承诺。
3. **但集成度在实质提升**：EF 11 起 `PublishAot=true` 时发布流水线**自动**启用编译模型 + 查询预编译生成（`EFOptimizeContext` 属性被移除，改由 `EFScaffoldModelStage`/`EFPrecompileQueriesStage` 控制）——从"手动开关"进化为"发布即生成"。
4. **对本项目**：EFCore 栈 `IsAotCompatible=false` 诚实声明继续正确；三栈 AOT 能力排序因 Dapper 栈实测超越而改变（PalORM 库级 > Dapper 调用点级 > EFCore 实验性基础设施）。

## EF Core 11 新特性全景（非 AOT 线，按对本项目相关度排序）

| 领域 | 特性 | 对本项目 EFCore 栈的相关性 |
|------|------|------------------------|
| **LINQ/SQL 翻译** | to-one join 优化（split query 去冗余 join **-29%** 查询耗时、ORDER BY 去冗余键 **-22%**）；no-op CAST 剥离 | **高**——本仓 EFCore 栈读路径（投影/租约查询）直接受益，零代码变更自动生效 |
| LINQ | GroupBy 增强（导航组合/聚合走 join 而非相关子查询） | 中 |
| LINQ | .NET 11 `FullJoin` 一等算子翻译 | 低（本仓无 full join 场景） |
| LINQ | `MaxBy`/`MinByAsync` 翻译 | 低 |
| Complex types | TPT/TPC 继承下可用 + JSON 列；复杂类型属性上建键/索引；lambda 链式配置；大批稳定化修复 | 中（本仓 EFCore 栈未用 owned/complex） |
| SQL Server | `VECTOR_SEARCH()` + 向量索引（实验）；向量属性默认不进 SELECT（**9x-22x** 提升）；全文目录/索引迁移化；`JSON_CONTAINS`/JSON 索引；temporal period CLR 属性映射；DateTimeOffset 组件翻译 | 低（本仓 EFCore 栈面向 PG/MySQL/SQLite） |
| SQLite | `group_concat` 排序（需 3.44+）；UInt128 绑定 | 低 |
| Cosmos | 复杂类型/事务批/批量执行/STJ 现代化（破坏性：去 Newtonsoft、`__jObject` 移除） | 无（本仓无 Cosmos） |
| 迁移 | FK 约束排除（`ExcludeForeignKeyFromMigrations`）；快照记录最新迁移 ID（团队分支分叉合并冲突预警）；`database update --add` 一步迁移（Roslyn 运行时编译）；`--connection`/`--offline`；`.config/dotnet-ef.json` 配置文件；通配 `--context *` | **高**——本仓 docs/sql 手写 DDL 体系外的 EFCore 用户直接受益；`--add` 对 Aspire/容器场景实用 |

## NativeAOT 支持深度剖析

### 机制（与 Dapper.AOT 惊人地同构）

EF 的 AOT 路径同样是**拦截器 + 发布期生成**：`Microsoft.EntityFrameworkCore.Tasks` MSBuild 集成在发布时静态扫描 LINQ 查询，为每条查询生成含**最终 SQL + 物化代码**（`UnsafeAccessor` 写私有字段）的 C# 拦截器。区别在覆盖面：EF 还需编译模型（compiled model）替代运行时模型构建，生成物体积大、生成慢（官方自认"may currently be quite large... take a long while"）。

### 官方限制清单（发布时仍报大量 trim/AOT 警告——"isn't fully guaranteed to run properly"）

1. **动态查询不支持**——跨语句组合/条件拼接的 LINQ 无法静态分析（官方给了重写为多条静态查询的指引）；且即便未来支持，动态查询在 AOT 下仍会慢（物化代码生成在 AOT 下不可用）。
2. LINQ 查询表达式语法（`from...select`）不支持——只有方法链。
3. Provider 需自建预编译支持（Npgsql/Pomelo 的支持状态需各自核实——本项目未验证）。
4. 捕获状态的值转换器不支持。
5. Cosmos NativeAOT（#33909）仍在 future work 清单未完成。
6. 零警告目标（#33478 "NativeAOT: get to zero warnings"）未达成。

### 稳定化时间线（跳票证据链）

| 版本 | 官方口径 |
|------|---------|
| EF Core 9（2024-11） | 引入实验性 AOT；文档承诺"expect to stabilize it... in **EF 10**" |
| EF Core 10（2025-11 GA） | **未转正**——仍 experimental，同样警告 |
| EF Core 11（2026-09 RC） | **仍 experimental**；但发布集成自动化（PublishAot 即自动生成）+ Cosmos 破坏性变更为 compiled model 让路——基础设施在收敛 |

[推断] 按"基础设施收敛度"判断，EF 12（2027-11）转正是合理预期——但已跳票两次，不做承诺性预判。

## 对本项目三栈格局的影响

| 栈 | AOT 等级 | 状态 |
|----|---------|------|
| PalORM | **库级**（源生成零反射） | 生产就绪，CI 实测 |
| Dapper | **调用点级**（拦截器 34 调用点） | 本周真库三方言实测（合并 bffd2c2）——**超越 EFCore 栈** |
| EFCore | 实验性基础设施 | 微软自述不适合生产；本仓 `IsAotCompatible=false` 诚实声明继续正确 |

**行动建议**：

1. 本仓 EFCore 栈定位（生态兼容、JIT）不变，`IsAotCompatible=false` 不动。
2. 不为 EFCore 栈引入 `Microsoft.EntityFrameworkCore.Tasks` 预编译——非 AOT 发布下预编译只省启动时间，本仓 EFCore 栈无冷启动瓶颈报告，收益不抵工具链复杂度（Karpathy 懒惰阶梯）。
3. 升级跟踪点：EF Core 11 GA（2026-11）后重查一次官方警告是否移除；EF 12 若转正，评估 EFCore 栈 AOT 化的前置条件清单（provider 支持 + 本仓查询形状静态性审计 + 动态查询扫描）。
4. 三栈选型文档口径更新：AOT 需求 → PalORM（库级）/ Dapper（调用点级）双选择；EFCore 明确标注"AOT 依赖上游实验特性，暂不可用"。
