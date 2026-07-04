# ADR 002：非 JSON 序列化方案评估

> 状态：提案  
> 日期：2026-06-28  
> 决策者：Pal.DDD 架构组

## 背景

当前 Pal.DDD 仅提供 `System.Text.Json`（STJ）序列化实现（位于 `PalDDD.Serialization` 包的 `PalDDD.Serialization.Json` 命名空间，无独立项目）。JSON 具有人类可读、跨语言、生态成熟等优势，但在高性能、低延迟场景下存在两个瓶颈：

1. **文本格式开销**：JSON 是文本格式，payload 体积大，序列化/反序列化有 UTF-8 编解码开销
2. **无 schema 进化**：JSON 无内置 schema 版本管理，需应用层自行处理消息格式变更

## 候选方案

### 方案 A：MemoryPack

| 维度 | 评估 |
|------|------|
| **AOT 安全** | ✅ 内置 source generator，设计目标就是 NativeAOT，零反射 |
| **性能** | 🔥 二进制格式，热点路径比 STJ 快 3-5x，payload 小 2-4x |
| **Schema** | ❌ 纯 code-first（C# 类型即 schema），无跨语言互操作 |
| **生态** | 🔶 较新（2023+），由 Cysharp 维护，社区活跃 |
| **集成成本** | 🟡 需新建 `PalDDD.Serialization.MemoryPack` 包，实现 `IMessageSerializer` |
| **NuGet** | `MemoryPack` 1.21.x，MIT 许可 |

**优点**：
- 与现有 `GenerateMessage` source generator 理念一致 —— 代码优先，编译器验证
- `MemoryPackSerializer.Serialize<T>(T value)` 与 STJ 的 `JsonSerializer.Serialize<T>` API 形式接近
- 支持 `MemoryPackable` attribute 和 source-generator，无需 .proto 文件
- 比 Protobuf 更紧凑（无字段标签开销）

**缺点**：
- C# only，无跨语言互操作
- 较新项目，长期稳定性待验证
- 社区生态小于 Protobuf

### 方案 B：Google.Protobuf

| 维度 | 评估 |
|------|------|
| **AOT 安全** | ✅ protoc 生成的代码是纯 C#，无反射 |
| **性能** | 🔥 二进制格式，成熟高效 |
| **Schema** | ✅ `.proto` 文件是强 schema，支持前向/后向兼容 |
| **跨语言** | ✅ 所有主流语言支持 |
| **生态** | ✅ 业界标准，Google 维护 |
| **集成成本** | 🔴 需引入 protoc 工具链 + `.proto` 文件维护 + 代码生成步骤 |

**优点**：
- 跨语言互操作 —— 非 .NET 消费者可直接消费
- 强 schema —— `.proto` 文件即文档，前向/后向兼容保证
- 业界最成熟的二进制序列化方案

**缺点**：
- **工具链重**：需 protoc + gRPC 工具，增加 CI 复杂度
- **Schema 维护成本**：双写 C# 类型 + `.proto` 文件，漂移风险
- 与 Pal.DDD 的"代码即 schema"哲学冲突

## 决策

**采纳方案 A（MemoryPack），暂不实施。**

理由：

1. **AOT 兼容性为先**：MemoryPack 的 source-generator 架构与 Pal.DDD 的 AOT-first 原则一致，零反射零运行时 IL 生成
2. **代码优先匹配现有架构**：`MemoryPackable` attribute ≈ `GenerateMessage` attribute，都是编译时生成代码，不引入外部 schema 文件
3. **Protobuf 工具链成本过高**：引入 protoc 意味着每个消息类型需要 `.proto` 文件 + 代码生成步骤，与当前 `dotnet build` 即完成的流程违背
4. **跨语言互操作非当前需求**：Pal.DDD 是 .NET 框架库，消费者是 .NET 应用；跨语言消息交换可通过 HTTP/JSON API 网关实现
5. **STJ JSON 满足大多数场景**：JSON 的人可读性、可调试性在开发和运维阶段有不可替代的价值；二进制序列化是性能优化而非功能需求

### 不采纳 Protobuf 的具体原因

- Pal.DDD 的核心哲学是"源代码驱动"（source-driven）：类型定义 + attribute → 源码生成器 → 编译时完成
- Protobuf 的工作流是"schema 驱动"（schema-driven）：`.proto` → protoc → 生成代码，引入外部 DSL
- 两种范式冲突；采用 Protobuf 意味着每个消息类型需要维护两份定义（C# record + .proto message），漂移风险高

## 后续

- 当出现以下信号之一时，实施 MemoryPack 集成：
  1. 生产环境中 Outbox/EventLog 的 JSON 序列化成为可测量瓶颈
  2. 消息 payload 大小成为存储或网络成本瓶颈
  3. 社区反馈要求二进制序列化选项
- 实施方式：新建 `PalDDD.Serialization.MemoryPack` 包，注册 `MemoryPackMessageSerializer : IMessageSerializer`
- 保留 STJ JSON 为默认序列化器，MemoryPack 作为 opt-in 高性能选项

## 后果

- **正面**：保留简单架构，不引入 protoc 工具链；STJ JSON 满足当前需求
- **负面**：高性能场景缺少内置二进制序列化选项
- **风险**：MemoryPack 项目生命周期风险（可通过 fork 或迁移方案 B 缓解）
