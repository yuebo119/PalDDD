# ADR 010：P3 评估类评审结论批量

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`docs/review/audit-2026-06-29-v2.md` 对 6 个 P3 评估类评审项给出"评估结论"要求，不要求必做，只要求给出 ADR 结论。

本 ADR 集中给出 6 项评估结论。

## 评估项与结论

### ITM-014 · `Directory.Build.props` AOT 默认值语义
- **议题**：全局 `IsAotCompatible=true` 默认，但 ~23/31 项目重写为 `false`——"默认 true 但多数项目关闭"使默认值语义偏离实际。
- **结论**：**维持现默认 `true`**。理由：默认严格代表治理纪律——若设为 `false`，AOT 兼容项目需开发者主动显式声明，违反"框架默认严苛"原则；当前重写为 `false` 的项目均为基础设施适配层（EFCore / Kafka / RabbitMQ），其豁免已在 `AGENTS.md` AOT 硬约束明文列出。资源配置与架构纪律的取舍明显倾向"默认严格 + 显式豁免"。
- **不动**。

### ITM-015 · SQL Server Dapper 适配器评估
- **议题**：EFCore Outbox 有 `SqlServerOutboxDbContext`，但 Dapper 适配器缺 SQL Server 方言。文档"三数据库支持"应明确 SQL Server 的覆盖范围。
- **结论**：**短期仅做文档化**。在 `docs/architecture.md` 已说明 SQL Server 当前仅 EFCore Outbox 覆盖。补充 Dapper 适配器需 SQL Server 测试环境与本项评估前未存在需求——延后至明确需求出现再补 `PalDDD.Dapper.SqlServer`。
- **落地**：文档明确（README + docs/architecture.md 三数据库支持说明已隐含 SQL Server 仅 EFCore 覆盖，无需追加改动）。

### ITM-017 · `OutboxStatus` 实体 + 状态枚举拆分
- **议题**：`OutboxMessage.cs` 同时含数据实体和 `OutboxStatus` 枚举——可拆分 `OutboxStatus` 为独立文件，与 `SagaStatus.cs` 先例对齐。
- **结论**：**不拆**。理由：`OutboxStatus` 仅有 3 个枚举值（Pending / Processed / Dead）逻辑强相关于 `OutboxMessage`，与 `SagaStatus` 7 个枚举值 + 多扩展方法的体量不同。`SagaStatus` 拆分因体积显著，`OutboxStatus` 尚未达到拆分阈值（YAGNI）。维持现状以保持实体 / 状态并置高内聚。
- **不动**。

### ITM-018 · `IMessageBroker.SubscribeAsync` 细粒度订阅
- **议题**：`SubscribeAsync<TMessage>` 只按类型订阅，不支持按 topic / routing key 细粒度订阅（架构测试断言 `EventFilter.cs` 不存在，有意简化）。
- **结论**：**不暴露细粒度订阅重载**。理由：架构边界测试断言 `EventFilter.cs` 不存在是有意简化——按类型订阅是跨 Kafka / RabbitMQ / Null 三实现的最小公共子集，topic/routing key 等高级路由场景由应用方在 handler 内自行分发，由应用层决定而非框架侵入。新增细粒度订阅需跨三实现对齐，违反"约定 > 配置"原则。
- **不动**。

### ITM-020 · `ValueObject<T>` 非数值场景
- **议题**：`ValueObject<T>` 约束 `INumber<T>, IMinMaxValue<T>`，排除 string / GUID 等非数值载体。非数值值对象需自行实现 `IValueObject`，失去基类 `TryFormat` / 隐式转换支持。建议提供 `StringValueObject` 基类或文档化非数值实现范式。
- **结论**：**仅文档化，不新增基类**。理由：非数值值对象的语义多变（GUID 强类型 ID、string SKU 码、Enum-as-string）难以用单一基类覆盖；新增 `StringValueObject` 需承诺 API 稳定性。当前 `IValueObject` 接口已提供通用入口，`ValueObject<T>` 的数值约束是有意边界（参考 ADR-003 `IValueObject` 保留论证）。建议在 `ValueObject.cs` XML doc 增强非数值示例引导而非提供基类。
- **落地**：`docs/conventions.md` 已叙述非数值值对象通过 `IValueObject` 自行实现的范式（见"值对象"章节），无需追加改动。

### ITM-021 · Saga 独立包拆分
- **议题**：`PalDDD.Transactions` 依赖 `Core` + `Messaging` + `Serialization`——使用者即使只用 Saga 也要引入 Messaging。
- **结论**：**不拆**。理由：当前 31 项目已较多，再拆 `PalDDD.Saga` 增加项目数与 NuGet 打包复杂度；`Saga` 与 Outbox / Inbox 共用 `OutboxMessage` 等基础类型，拆分需引入额外共享抽象包，得不偿失。使用者通过 `PalDDD.Prompts` 元包可按需引用子集，已缓解"全量引入"痛点。
- **不动**。

### ITM-023 · Testcontainers 集成测试 CI 配置
- **议题**：6 个 Broker 集成测试需 Docker 环境，CI 配置文档未说明。
- **结论**：**采纳 CI 配置模板**。在 `docs/development.md` 补充 GitHub Actions service container 模板，降低接入门槛。本 ADR 视为采纳——实际配置文档落地为 P3 文档化任务（与 ITM-007 Stryker CI 配置同章节）。
- **后续行动**：在 `docs/development.md` 已有 Stryker 章节，同章补 Testcontainers service container 模板。

## 关联

- 评审来源：`docs/review/audit-2026-06-29-v2.md` ITM-014 / 015 / 017 / 018 / 020 / 021 / 023
- 相关 ADR：ADR-003（IValueObject 保留）、ADR-005（net11 单目标）