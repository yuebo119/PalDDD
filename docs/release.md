# Pal.DDD 发布规范（SOP）

> 本规范定义 Pal.DDD 项目从代码变更到 NuGet 发布的标准流程。
> 所有版本发布（含补丁版/小版本/大版本/Preview）必须遵守。
>
> **当前状态**：`VersionPrefix=1.0.0` / `VersionSuffix=preview.1`（见 `Directory.Build.props`）。
> **首次发布待办**：本规范第 5/6/9 章在首次实际发布后需补实测教训（参考 ORM 项目 `docs/发布规范.md` §9）。

---

## 目录

1. [版本号管理](#一版本号管理)
2. [公开发布包范围](#二公开发布包范围)
3. [分支与合并流程](#三分支与合并流程)
4. [发布前验证清单](#四发布前验证清单)
5. [触发发布](#五触发发布)
6. [release.yml 工作流规范](#六releaseyml-工作流规范)
7. [发布后验证](#七发布后验证)
8. [回滚与补救](#八回滚与补救)
9. [发布实践教训](#九发布实践教训)
10. [版本发布模板](#十版本发布模板)

---

## 一、版本号管理

### 1.1 唯一真源：Directory.Build.props

**所有 src/ 项目的版本由 `Directory.Build.props` 集中管理**，单个 csproj **不得**硬编码 `<Version>`。

```xml
<!-- Directory.Build.props（唯一版本源） -->
<PropertyGroup>
    <VersionPrefix>1.0.0</VersionPrefix>
    <VersionSuffix>preview.1</VersionSuffix>
    <!-- 最终 Version = 1.0.0-preview.1 -->
</PropertyGroup>
```

### 1.2 版本号语义（SemVer）

| 版本段 | 何时升级 | 示例 |
|--------|---------|------|
| **Major**（1.x.x） | 破坏性 API 变更、不兼容升级 | v1.0.0 → v2.0.0 |
| **Minor**（x.1.x） | 新增功能、向后兼容 | v1.0.0 → v1.1.0 |
| **Patch**（x.x.1） | bug 修复、向后兼容 | v1.0.0 → v1.0.1 |
| **Preview**（VersionSuffix） | 预发布 | preview.1 → preview.2 |

### 1.3 三方一致验证

升版本时，**同一次提交**内必须同步更新所有引用版本号的位置：

| 位置 | 文件 | 验证命令 |
|------|------|---------|
| 版本真源 | `Directory.Build.props` | `grep "VersionPrefix\|VersionSuffix" Directory.Build.props` |
| README badge | `README.md` | `grep "nuget-v" README.md` |
| tag 名 | git tag | `git tag --list "v*"` |
| CHANGELOG | `CHANGELOG.md` | `grep "^## " CHANGELOG.md` |

---

## 二、公开发布包范围

### 2.1 发布清单（按层级）

DDD 项目分层（对照 conventions §4.2 解决方案分层）：

#### Domain 层（领域纯净层）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Core` | `src/PalDDD.Core/` | ✅ |

#### App-Abstractions 层

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Serialization` | `src/PalDDD.Serialization/` | ✅ |
| `PalDDD.Serialization.Evolution` | `src/PalDDD.Serialization.Evolution/` | ✅ |

#### App-Core 层

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.CQRS` | `src/PalDDD.CQRS/` | ✅ |
| `PalDDD.EventLog` | `src/PalDDD.EventLog/` | ✅ |
| `PalDDD.Transactions` | `src/PalDDD.Transactions/` | ✅ |
| `PalDDD.Idempotency` | `src/PalDDD.Idempotency/` | ✅ |
| `PalDDD.Projections` | `src/PalDDD.Projections/` | ✅ |
| `PalDDD.Messaging` | `src/PalDDD.Messaging/` | ✅ |
| `PalDDD.Compression` | `src/PalDDD.Compression/` | ✅ |

#### Infra-EFCore 层（非 AOT 适配器）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.EntityFrameworkCore` | ~~`src/PalDDD.EntityFrameworkCore/`~~（空目录已删除，见 OBS-068） | ⚠️ 仅旧 preview.1 nupkg |
| `PalDDD.Repository.EFCore` | `src/PalDDD.Repository.EFCore/` | ✅ |
| `PalDDD.Transactions.EFCore` | `src/PalDDD.Transactions.EFCore/` | ✅ |
| `PalDDD.EventLog.EFCore` | `src/PalDDD.EventLog.EFCore/` | ✅ |
| `PalDDD.Idempotency.EFCore` | `src/PalDDD.Idempotency.EFCore/` | ✅ |
| `PalDDD.Projections.EFCore` | `src/PalDDD.Projections.EFCore/` | ✅ |

#### Infra-Dapper 层（AOT 兼容）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Dapper` | `src/PalDDD.Dapper/` | ✅ |
| `PalDDD.Dapper.PostgreSql` | `src/PalDDD.Dapper.PostgreSql/` | ✅ |
| `PalDDD.Dapper.Sqlite` | `src/PalDDD.Dapper.Sqlite/` | ✅ |
| `PalDDD.Dapper.MySql` | `src/PalDDD.Dapper.MySql/` | ✅ |

#### Infra-Messaging 层（非 AOT 适配器）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Messaging.Kafka` | `src/PalDDD.Messaging.Kafka/` | ✅ |
| `PalDDD.Messaging.RabbitMQ` | `src/PalDDD.Messaging.RabbitMQ/` | ✅ |

#### Infra-Other 层

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Compression.Native` | `src/PalDDD.Compression.Native/` | ✅ |
| `PalDDD.Serialization.MemoryPack` | `src/PalDDD.Serialization.MemoryPack/` | ✅ |
| `PalDDD.Projections.EventLog` | `src/PalDDD.Projections.EventLog/` | ✅ |

#### Hosting 层

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Hosting.AspNetCore` | `src/PalDDD.Hosting.AspNetCore/` | ✅ |
| `PalDDD.DependencyInjection` | `src/PalDDD.DependencyInjection/` | ✅ |

#### 分析器（编译期工具）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Analyzers` | `src/PalDDD.Analyzers/` | ✅ |
| `PalDDD.Analyzers.CodeFixes` | `src/PalDDD.Analyzers.CodeFixes/` | ✅ |
| `PalDDD.Core.SourceGen` | `src/PalDDD.Core.SourceGen/` | ✅（analyzer 包，通过 Base 元包传递） |

#### Metapackages（元包，只含 PackageReference）

| 包 ID | 项目路径 | 公开发布 |
|-------|---------|:------:|
| `PalDDD.Base` | `src/PalDDD.Base/` | ✅（领域+序列化+压缩+分析器 组合） |
| `PalDDD.Extension` | `src/PalDDD.Extension/` | ✅（CQRS+EventLog+Outbox+Saga+Projection+DI 组合） |
| `PalDDD.Prompts` | `src/PalDDD.Prompts/` | ❌ AI 模板，非运行时库（见 §2.2） |

### 2.2 禁止打包的项目（❌ 永不发布到 NuGet）

以下项目**严禁打包发布**。它们不是运行时库，用户不应 `PackageReference` 引用。
必须在 csproj 中设 `<IsPackable>false</IsPackable>`。

| 项目 | 当前 NuGet 残留 | 禁止原因 | 处置 |
|------|:--:|------|------|
| `PalDDD.EntityFrameworkCore` | 1.0.0-preview.1（已 Unlist） | 源码未入库（OBS-068），被 PalORM 替代 | 永不打包，NuGet 旧包保持 Unlist |
| `PalDDD.Prompts` | 1.1.0 | AI 代码生成模板，非运行时库 | `<IsPackable>false>`，NuGet 包 Unlist |
| `PalDDD.Testing` | 1.1.0（已 Unlist） | 测试基础设施，仅项目内部用 | `<IsPackable=false>`，NuGet 包保持 Unlist |
| `PalORM.Testing` | 5.0.0（已 Unlist） | PalORM 测试基础设施 | NuGet 包保持 Unlist |
| `PalDDD.AotSample` | 未发布 | CI AOT 验证示例 | `<IsPackable=false>` |
| `PalDDD.ECommerce` | 未发布 | 电商场景示例代码 | `<IsPackable=false>` |

**判定规则**：以下三类项目永不打包——
1. **示例项目**（samples/）：AotSample / ECommerce / PalOrmSample / MinimalApi
2. **测试基础设施**（test/ 共享层）：Testing
3. **非运行时工具**：Prompts（AI 模板）、EntityFrameworkCore（废弃空壳）

### 2.3 包公开发布的判定标准

新增 src/ 项目是否公开发布，按以下顺序判断：

1. **目标消费者**：是否面向 Pal.DDD 项目外的开发者？仅内部测试用 → 不发布。
2. **API 稳定性**：是否提供 semver 承诺？随项目内部需求变更无承诺 → 不发布。
3. **消费场景**：独立消费者是否有合理使用场景？无 → 不发布。
4. **公共 API 面**：发布是否会让消费者困惑？会 → 不发布。

**判定为不发布**：在 csproj 加 `<IsPackable>false</IsPackable>`，并在发布清单中排除。

---

## 三、分支与合并流程

### 3.1 分支保护规则（详见 docs/branch-flow.md）

```
feature/xxx ──PR──▶ dev ──PR──▶ main
    │                 │            │
    └── 开发分支      └── 集成测试   └── 生产发布（含 tag）
```

- **main**：仅接受来自 dev 的 PR 合并，禁止直接 push
- **dev**：接受来自 `feature/*` 的 PR 合并
- **feature/\*\***：从 dev 创建，PR 到 dev

### 3.2 合并权限规则（强制）

| 合并方向 | 谁触发 | 频率 |
|---------|--------|------|
| `feature/*` → `dev` | 贡献者自主（CI 通过后合并） | 每个 feature 完成时 |
| `dev` → `main` | **仅人工确认后**（用户/维护者明确指示"合并到 main"） | **按需**——不是每次 dev 更新都合并 |
| `main` → `dev`（反向同步） | 维护者 | 按需 |

**关键规则**：
- ❌ **禁止 AI/自动化工具自主合并到 main**——dev → main 是发布动作，必须用户明确确认（全局 AGENTS.md 安全红线）
- ❌ **禁止"每次 dev 更新就同步到 main"**——main 应保持稳定，只在准备发版时合并
- ✅ dev 可以自由积累多个 feature，等到准备发布版本时再一次性合并到 main

### 3.3 PR 类型与 `--delete-branch` 使用

| PR 类型 | base | `--delete-branch` | 说明 |
|---------|------|-------------------|------|
| `feature/*` → `dev` | dev | ✅ 删 feature | 标准 feature 流程 |
| `dev` → `main` 同步 | main | ❌ **不删 dev** | dev 是常驻分支 |
| `fix/*` → `main`（紧急修复） | main | ✅ 删 fix | 跳过 dev 的紧急通道 |

**关键**：`dev` 和 `main` 是常驻分支，合并 PR 时**绝不**用 `--delete-branch` 删除它们。

---

## 四、发布前验证清单

### 4.1 本地必跑

```bash
# 1. 工作树清洁
git status --short

# 2. 全量构建（含所有 src + test 项目，0 警告 0 错误）
dotnet build PalDDD.slnx -c Release --no-incremental -warnaserror

# 3. 单元 + 集成测试全绿
dotnet test PalDDD.slnx --no-restore --no-build

# 4. 规范验证
bash scripts/verify-conventions.sh

# 5. AI 系统门禁（如使用 .ai/）
bash .ai/scripts/gate-check.sh --allow-dirty

# 6. 本地 pack 验证
rm -rf /tmp/release-preview && mkdir -p /tmp/release-preview
for proj in $(ls src/); do
    csproj="src/$proj/$proj.csproj"
    [ -f "$csproj" ] && dotnet pack "$csproj" -c Release --no-build -o /tmp/release-preview
done
ls /tmp/release-preview/*.nupkg | wc -l   # 应等于公开发布包数
```

### 4.2 nuspec 元数据检查

每个 nupkg 解压后的 `.nuspec` 必须包含：

```bash
unzip -p /tmp/release-preview/PalDDD.Core.*.nupkg '*.nuspec' | grep -E "<(id|version|projectUrl|repository|releaseNotes|license)"
```

**必须字段**：
- `<id>` 正确（如 `PalDDD.Core`）
- `<version>` 与 `Directory.Build.props` 一致（如 `1.0.0-preview.1`）
- `<projectUrl>` 指向 `https://github.com/yuebo119/PalDDD`
- `<repository url=... commit=.../>` 含 commit hash（证明 SourceLink 生效）
- `<releaseNotes>` 指向 CHANGELOG.md
- `<license type="expression">AGPL-3.0-or-later`

---

## 五、触发发布

> ⚠️ **首次发布前**：DDD 项目当前没有 `.github/workflows/release.yml`。首次发布需先创建该 workflow（见第六章）。

### 5.1 tag 触发（标准发布）

```bash
# 1. 确认本地 main 与远程一致
git checkout main
git pull origin main
git log -1 --format="%h %s"

# 2. 确认版本号已更新到目标版本
grep -E "VersionPrefix|VersionSuffix" Directory.Build.props

# 3. 打 tag（tag 名格式：v + 版本号，如 v1.0.0-preview.1）
git tag v1.0.0-preview.1
git push origin v1.0.0-preview.1

# 4. 观察 Actions 运行
# https://github.com/yuebo119/PalDDD/actions/workflows/release.yml
```

### 5.2 workflow_dispatch（手动补发）

适用场景：tag 触发失败需重发，或预览版发布。

1. 访问 https://github.com/yuebo119/PalDDD/actions/workflows/release.yml
2. 点 **Run workflow**
3. 输入版本号（必须与 Directory.Build.props 完全一致）
4. 点 **Run workflow**

### 5.3 不应触发发布的场景

- ❌ PR 合并到 dev（dev 不发布，仅集成测试）
- ❌ PR 合并到 main 但未打 tag（main 上的代码 ≠ 发布版本）
- ❌ 直接 push 到 main（违反分支保护）

---

## 六、release.yml 工作流规范

### 6.1 触发条件

```yaml
on:
    push:
        tags: ['v*']              # 标准触发：tag 推送
    workflow_dispatch:             # 手动触发：补发/预览
        inputs:
            version: { required: true }
```

### 6.2 必须的 step 顺序

| 顺序 | step | 作用 | 失败处理 |
|------|------|------|---------|
| 1 | Resolve version | 从 tag/input 解析版本号 | 解析失败 → exit |
| 2 | Verify version matches props | tag 版本 vs Directory.Build.props | 不一致 → exit（防误发） |
| 3 | Restore + Build | 全量构建，warnings as errors | 编译失败 → exit |
| 4 | Unit tests | 全部 .Tests 项目 | 测试失败 → exit |
| 5 | Integration tests | Testcontainers（PG/MySQL/RabbitMQ/Kafka）+ ubuntu-latest | 测试失败 → exit |
| 6 | AOT publish 验证 | AOT 核心层 7 项目 `dotnet publish -p:PublishAot=true` | AOT 失败 → exit |
| 7 | Pack | 全部公开发布项目 | pack 失败 → exit |
| 8 | Verify package count | 断言 nupkg 数 = 发布清单总数 | 数量不对 → exit（防漏发） |
| 9 | Push to NuGet.org | `--skip-duplicate` | push 失败 → exit |
| 10 | Create GitHub Release | 仅 tag 触发时 | 创建失败 → exit |

### 6.3 关键配置

```yaml
concurrency:
    group: release-${{ github.ref }}
    cancel-in-progress: false    # 发布不可中断，防半成品

permissions:
    contents: write              # 创建 GitHub Release 必需

# Testcontainers 必须用 ubuntu-latest（Windows runner 不支持）
runs-on: ubuntu-latest
```

### 6.4 secrets 配置（一次性）

| Secret 名 | 用途 | 获取方式 |
|-----------|------|---------|
| `NUGET_API_KEY` | 推送到 nuget.org | https://www.nuget.org/account/apikeys（Push 权限，Glob Pattern=`PalDDD.*`，365 天有效期） |

---

## 七、发布后验证

### 7.1 workflow 运行状态

```bash
gh run list --workflow=release.yml --limit 1
# 应显示 completed/success
```

### 7.2 nuget.org 包页面

逐一访问公开发布包验证（如 https://www.nuget.org/packages/PalDDD.Core）：

- ✅ 新版本号（首次索引可能需 5-15 分钟）
- ✅ License: AGPL-3.0-or-later
- ✅ Project URL: https://github.com/yuebo119/PalDDD
- ✅ Source Link 标识（Repository 区显示 commit hash）
- ✅ README 渲染正确
- ✅ Dependencies 树正确（如 PalDDD.CQRS 依赖 PalDDD.Core）

### 7.3 GitHub Release

访问 https://github.com/yuebo119/PalDDD/releases：

- ✅ Release 标题 = tag 名（如 `v1.0.0-preview.1`）
- ✅ Body 来自 CHANGELOG.md
- ✅ Assets 含全部 nupkg 文件

### 7.4 实际消费测试

```bash
mkdir /tmp/palddd-consumer-test && cd /tmp/palddd-consumer-test
dotnet new console
dotnet add package PalDDD.Base --version 1.0.0-preview.1
dotnet add package PalDDD.Extension --version 1.0.0-preview.1
dotnet restore
dotnet build   # 应成功，无警告
```

---

## 八、回滚与补救

### 8.1 发布失败（workflow 报错）

| 失败 step | 补救 |
|----------|------|
| Build/Test 失败 | 修复代码 → 重新合并到 main → 重打 tag（见 8.3） |
| AOT publish 失败 | 检查 AOT 核心层是否引入反射（PDDD-G8 + gate-check.sh） |
| Pack 失败 | 检查 csproj 改动是否破坏 pack → 修复 → 重打 tag |
| Push 部分失败 | `--skip-duplicate` 会自动跳过已 push 的包；workflow_dispatch 重发即可 |
| GitHub Release 创建失败 | 手动在 https://github.com/yuebo119/PalDDD/releases/new 创建 |

### 8.2 误发布（不该发的版本发出去）

**NuGet 不支持删除已发布版本**，只能 listed=false 隐藏：

1. 访问 https://www.nuget.org/packages/<包名>
2. 点 **Manage package**
3. **Listing** 区域取消 **List in search results**
4. **Save**

效果：
- ✅ nuget.org 搜索结果不显示
- ✅ 直接 URL 仍可访问（已下载项目仍能恢复）
- ✅ `dotnet add package <包名> --version <版本>` 仍工作

### 8.3 重打 tag

```bash
# 删本地+远程旧 tag
git tag -d v1.0.0-preview.1
git push origin :refs/tags/v1.0.0-preview.1

# 在最新 main 上重打
git checkout main && git pull origin main
git tag v1.0.0-preview.1
git push origin v1.0.0-preview.1
```

---

## 九、发布实践教训

> ⚠️ **首次发布待补**：DDD 项目当前未实际发布过（v1.0.0-preview.1 待发）。
> 首次实际发布后，在此章节补充实测教训（参考 ORM 项目 `docs/发布规范.md` §9 的 9 条 v5.0.0 教训）。

预期可能踩的坑（基于 ORM 项目经验预判）：

| # | 预期教训 | 预防措施 |
|---|---------|---------|
| 1 | Testcontainers service 容器配置缺失（PG/MySQL/RabbitMQ/Kafka） | §6.2 step 5 必须配 4 个 service |
| 2 | `ContinuousIntegrationBuild=true` 触发路径归一化（dotnet/roslyn#55860） | ArchitectureBoundaryTests 的 `FindRepositoryRoot()` 已处理；新增 src 目录扫描需复核 |
| 3 | git diff --check 对 Windows CRLF 行为不一致 | ci.yml gate 的 `git diff --check` 失败降级为 warning |
| 4 | `gh pr merge --delete-branch` 会删主分支 | §3.3 表格明确：dev/main 是常驻分支 |
| 5 | 内部测试库不应公开发布 | `test/PalDDD.Testing` 显式 `<IsPackable>false</IsPackable>`（待声明） |
| 6 | 首次 tag 应基于已过 CI 的 commit | §4.1 本地必跑清单 + §5.1 tag 前确认 main 已过 CI |
| 7 | dev → main 合并必须人工确认 | §3.2 合并权限规则（禁止 AI 自主合并） |
| 8 | 多包同步发布易漏发 | §6.2 step 8 断言 nupkg 数量 |

---

## 十、版本发布模板

### 10.1 Preview 版（preview.1 → preview.2）

```bash
# 1. 创建 feature 分支
git checkout dev && git pull origin dev
git checkout -b feature/xxx

# 2. 修改代码 + 测试
# ... 编辑代码 ...
dotnet build PalDDD.slnx --no-incremental
dotnet test PalDDD.slnx --no-restore --no-build

# 3. 升版本（同一次提交）
# 编辑 Directory.Build.props: <VersionSuffix>preview.1</VersionSuffix> → preview.2
# 编辑 README.md badge
# 编辑 CHANGELOG.md
git add Directory.Build.props README.md CHANGELOG.md
git commit -m "功能：xxx + 升版本 preview.2"

# 4. PR 流程：feature → dev → main
# 5. 重打 tag：git tag v1.0.0-preview.2 && git push origin v1.0.0-preview.2
```

### 10.2 补丁版（1.0.0 → 1.0.1）

```bash
# 移除 VersionSuffix，升 VersionPatch
# <VersionPrefix>1.0.0</VersionPrefix> → 1.0.1
# 删除 <VersionSuffix>preview.N</VersionSuffix>
```

### 10.3 小版本（1.0.1 → 1.1.0）

新增功能时，除 §10.2 步骤外：

```bash
# 升版本前更新 CHANGELOG.md（新增 Features 段落）
# 检查新增 API 是否需要补充 README 示例
# 检查新增 src/ 项目是否需要加入发布清单（§2.3 判定）
```

### 10.4 大版本（1.x → 2.0.0）

破坏性 API 变更时，除 §10.3 步骤外：

```bash
# 在 docs/decisions/ 新增 ADR 记录破坏性变更决策
# README 加迁移指南
# 考虑是否保留 v1.x 的 LTS 分支
```

---

## 附录 A：相关文件清单

| 文件 | 用途 |
|------|------|
| `Directory.Build.props` | 版本号真源 + 包元数据 + SourceLink 配置 |
| `.github/workflows/release.yml`（待创建） | 发布 workflow |
| `.github/workflows/ci.yml`（待创建） | CI 主流程 |
| `CHANGELOG.md` | 变更日志（GitHub Release body 来源） |
| `README.md` | 包页面展示用 README |
| `docs/branch-flow.md` | 分支与发布流程（精简版） |

## 附录 B：常用命令速查

```bash
# 查看当前版本
grep -E "VersionPrefix|VersionSuffix" Directory.Build.props

# 查看远程 tag
git tag --list "v*" --sort=-version:refname | head -5

# 查看 release workflow 历史（首次发布后）
gh run list --workflow=release.yml --limit 5

# 查看已发布包
gh api repos/yuebo119/PalDDD/releases --jq '.[].tag_name'

# 验证本地 pack 产物
unzip -p /tmp/release-preview/PalDDD.Core.*.nupkg '*.nuspec' | head -20

# 统计公开发布包数
find src -name "*.csproj" | xargs grep -l '<IsPackable>false' | wc -l   # 不发布数
find src -name "*.csproj" | wc -l                                        # 总数 - 不发布数 = 发布数
```
