# Pal.DDD AI 提示模板使用说明

## 什么是 `.pal/prompts/`

这是一组结构化的 **AI 提示模板**，设计用于 AI 编码助手（GitHub Copilot / Cursor / Claude Code / Windsurf 等）。
每个模板封装了 Pal.DDD 框架的架构约束和代码约定，帮助 AI 生成符合 DDD/Clean Architecture/AOT 规范的代码。

## 如何使用

### 方式 1：作为 System Prompt（推荐）
将模板内容复制到 AI 编码助手的 System Prompt / Rules 中。AI 会将模板约束作为代码生成的硬规则。

### 方式 2：作为对话上下文
在开始编码前，将相关模板粘贴到对话中，然后描述你的需求。
例如：粘贴 `aggregate-root.prompt.md`，然后说「创建 Order 聚合根」。

### 方式 3：.cursorrules / .github/copilot-instructions.md
将模板的核心约束提取到项目的 AI 配置文件中，使 AI 在每次交互时自动遵守。

## 模板清单

| 模板 | 用途 | 何时使用 |
|------|------|---------|
| `aggregate-root.prompt.md` | 聚合根 + 实体 | 创建新的领域模型 |
| `domain-event.prompt.md` | 领域事件 | 定义事件类型 |
| `command-handler.prompt.md` | 命令 + CQRS 写端 | 实现写操作 |
| `query-handler.prompt.md` | 查询 + CQRS 读端 | 实现读操作 |
| `saga-orchestrator.prompt.md` | Saga 长事务 | 跨聚合业务流程 |
| `projection-handler.prompt.md` | 投影 + 事件回放 | 构建读模型 |
| `value-object.prompt.md` | 值对象 | 封装原始类型 |
| `bounded-context.prompt.md` | BC 脚手架 + DI | 项目初始化 |

## 模板结构

每个模板包含五个部分：
1. **角色** — AI 扮演的角色（Pal.DDD 框架专家）
2. **框架约束** — 编译期强制执行的规则（来自 StrategicDddAnalyzer）
3. **必须遵守** — 框架设计哲学的硬约束
4. **禁止** — 反模式警告
5. **输出格式** — 具体的代码骨架
6. **示例** — 来自 samples/ 的真实可编译代码

## 框架核心原则（适用于所有模板）

1. **零反射** — `MakeGenericType` / `Activator.CreateInstance` / `Assembly.GetTypes()` 禁止
2. **AOT 兼容** — `JsonSerializerIsReflectionEnabledByDefault=false`，源生成 JSON
3. **不做 IRepository<T>** — `DbContext` 就是 UoW+Repository
4. **显式注册** — Handler 通过 `AddPalCommandHandler<T>()` 注册，零 Assembly Scanning
5. **ConfigureAwait(false)** — 所有基础设施层 await 必须调用

## 反馈

模板是活文档，随框架演化持续更新。如有改进建议，请提交 Issue 或 PR。
