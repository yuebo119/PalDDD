# Pal.DDD 项目长期记忆

## 项目定位
- **Pal.DDD**：面向 .NET 11 的 DDD/CQRS/Event Sourcing 基础设施框架
- 版本：v1.0.0-preview.1 · 30 个独立 NuGet 包 · **AGPL-3.0-or-later** 许可（2026-07-05 由 MIT 改为 AGPL v3）
- 仓库：https://github.com/yuebo119/PalDDD
- 单目标 `net11.0`（ADR-005 决策，OrderedDictionary 硬阻塞）

## 技术栈
- .NET 11 (SDK 11.0.100-preview.5) · C# latest · EF Core 11 Preview 5 · Dapper 2.1.79 + Dapper.AOT
- 测试：TUnit 1.58.0 + Microsoft.Testing.Platform · coverlet · FsCheck · Verify.TUnit · Testcontainers
- AOT 兼容：核心层 + Dapper 适配层 `IsAotCompatible=true`；EF Core/Kafka/RabbitMQ/AspNetCore 显式 `false`

## 项目结构
- `src/` 30 项目（Clean Architecture 分层：Domain → App → Infra → Hosting）
- `test/` 15 项目（1:1 映射 src + PalDDD.Testing 共享基础设施）
- `docs/` 65 md 文件（含 16 份 ADR、architecture/conventions/tutorial/usage/aot/performance）
- `docs/review/` 评审产出目录（遵循 NAMING.md 命名规范，最新全量审计 audit-2026-07-05-v2.md 8.4/10，最新架构评审 review-2026-07-05-serena.md 8.6/10）

## 已知文档债务（待修复）
- **F-061/F-062（P2 未修复）**：conventions.md:302 测试数 14→15、NAMING.md 文件清单未含 7 月产出
- **F-003（P2）**：README:217 Metapackages 列三个 vs conventions:321 只 Prompts（视角差异未说明）
- **OBS-068（P3）**：三个元包（PalDDD.Base/EntityFrameworkCore/Extension）.csproj 未入库，.nupkg 存在于 nupkgs/。用户安装正常，但 git clone 后无法从源码重建元包。

## 审计方法论教训
- **NuGet 包验证**：判断包是否存在必须查 `nupkgs/` 目录和 NuGet.org，不能仅凭仓库 `.csproj` 判断。`nupkgs/` 目录有 23 个 .nupkg（含 3 个元包），被 `.gitignore:8 *.nupkg` 忽略。
- **元包（metapackage）**：只有 .csproj（含 PackageReference）无 .cs 源码。PalDDD.Base(2.32KB)/Extension(2.38KB) 是元包，EntityFrameworkCore(82.13KB) 有实际内容。

## 核心规范
- 零反射红线（ArchitectureBoundaryTests 源码扫描强制）
- 编译期治理：PDDD001-015 共 15 条 Roslyn 分析器规则
- 性能契约：ValueTask + IsCompletedSuccessfully 快速路径、FrozenDictionary、ref struct 枚举器、ThreadStatic 池化
- 文件命名：约定大于配置（conventions.md §4.9 逐类型文件创建决策矩阵）
- 评审纪律：R0-R8 + 危害×复杂度双维度优先级（conventions.md §13）

## 用户规则关键约束
- 全局强制规则：中文优先、最新版本（含预览版）、最优方案、顶级专家身份、代码修改后自动 Git 提交（含大模型信息）
- 当前大模型：GLM-5
- 版本策略：允许使用预览版（2026-07-05 修改，v1.4/v2.0）；稳定版优先，预览版可用于新项目和实验性功能
- 规则文件位置：`C:\Users\Andy\.codebuddy\rules\`（全局强制规则.mdc / dotnet10-csharp.mdc / zcode-agents集成.mdc）

## 工作记忆位置
- 日志：`c:\ai\claude\Pal.DDD\.codebuddy\memory\YYYY-MM-DD.md`
- 长期：`c:\ai\claude\Pal.DDD\.codebuddy\memory\MEMORY.md`（本文件）
