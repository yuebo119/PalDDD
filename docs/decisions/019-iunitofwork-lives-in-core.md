# ADR 019：IUnitOfWork 归属 PalDDD.Core 项目（保留 PalDDD.Core.Repository 命名空间）

> 状态：已采纳
> 日期：2026-08-26
> 关联：架构文档"App-Abstractions"节、二轮架构评审"架构瑕疵"节（本 ADR 补档）

## 背景

原独立项目 `PalDDD.Repository` 已移除，其 `IUnitOfWork` 接口合并入 `PalDDD.Core` 项目，但保留 `PalDDD.Core.Repository` 命名空间（`src/PalDDD.Core/IUnitOfWork.cs`）。严格 Clean Architecture 视角：持久化关注点的接口（事务边界 + SaveChanges）位于领域核心项目内，是边界纯度的一个让步——此前仅代码注释说明，无 ADR 存档。

## 决策

**IUnitOfWork（及 UnitOfWorkExtensions）留在 PalDDD.Core 项目、保留 PalDDD.Core.Repository 命名空间，不重建独立抽象项目。**

## 理由

1. **接口纯度高于项目纯度**：`IUnitOfWork` 是 4 方法纯抽象（Begin/Commit/Rollback/SaveChanges），无任何基础设施类型引用（仅 BCL 的 ValueTask/CancellationToken/IAsyncDisposable）。领域核心依赖它表达"事务边界"概念，不引入 EF/Dapper/连接等具体设施——依赖方向仍指向稳定抽象。
2. **命名空间承载语义区分**：`PalDDD.Core.Repository` 命名空间明示"持久化关注点"与同项目的 `Entity`/`AggregateRoot`（领域原语）区分；架构边界测试（ArchitectureBoundaryTests）锁定 Core 零项目引用、仅 ByteAether.Ulid 一个包——污染无入口。
3. **独立项目的成本不成比例**：三栈（EFCore/Dapper/PalORM）各自实现 IUnitOfWork，若抽独立 `PalDDD.Abstractions` 项目，40 包的依赖图增加一个节点，元包分层（Base/Extension）与边界测试白名单全部要动——收益仅是"项目名更纯"。
4. **模板方法走扩展方法**：`ExecuteInTransactionAsync` 以扩展方法提供（ISP——实现者只写 4 个原语），接口面保持最小，抑制该抽象的生长压力。

## 触发重评条件

- Core 出现第二个持久化关注点接口（如 ITransactionFactory），需要成组隔离时。
- 某实现栈需要 IUnitOfWork 携带基础设施语义（如 DbTransaction 透明暴露）导致接口被污染时——那是接口设计警报，优先修接口而非挪项目。

## 后果

- 架构文档的"App-Abstractions"节描述与此 ADR 对齐：IUnitOfWork 是 Core 内的持久化命名空间，非独立层。
- 新增持久化抽象时默认进 `PalDDD.Core.Repository` 命名空间并在边界测试可见范围内评审。
