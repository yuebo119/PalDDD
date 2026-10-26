# ADR 036：许可证由 AGPL-3.0-or-later 改为 Apache-2.0

> 状态：**已采纳并实施（2026-10-26）**，自下一发布版起生效（nuspec 随 `Directory.Build.props` 单点切换；已发布历史版本不可追溯）
> 日期：2026-10-26
> 关联：2026-09-17/19/21 审计轮「维持 AGPL 社区策略」裁决（`docs/review/audit-2026-09-17-independent.md` §D-2、`docs/review/audit-2026-09-19-chief-audit.md` §D-3、`docs/review/audit-2026-09-21-technical-audit.md`）、2026-10-26 全历史身份改写（单一版权人 SuperLee）、PalORM 6.8.0 上游转 Apache-2.0（`ac7777e` 升级带入）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-10-26 |
| 评审状态 | 已采纳并实施：9 处锚点同提交切换；count-audit / doc-consistency / verify-conventions --quick 门禁重跑通过 |
| 兼容性 | 对下游纯放宽：Apache-2.0 与 AGPL-3.0 均可入 GPLv3 系组合，均不可入 GPLv2-only；义务面从 copyleft（分发/网络服务触发源码公开）收敛为保留 LICENSE+NOTICE、声明修改、专利授权三件套。已发布历史版本（tag v1.x~v4.0.0 与已上 NuGet.org 的包）不可追溯，永久保持 AGPL-3.0-or-later |
| 触发原因 | 单一版权人改写完成（重许可无需任何第三方同意）+ PalORM 6.8.0 转 Apache-2.0（存储面 AGPL 分层效应消失）+ 维护者裁决（2026-10-26） |

## 背景

本仓 2026-07 起为 MIT，2026-09-19 改为 AGPL-3.0-or-later（社区策略导向，见 CHANGELOG §[2.0.0] 与 `docs/review/audit-2026-09-17-independent.md`：当时把「AGPL → 宽松」归类为产品/商业决策，2026-09-19/21 两轮审计裁决维持 AGPL）。

2026-10-24~26 两个前提发生了变化：

1. **git 全历史改写为单一版权人**。全部 948 条提交的作者与提交者归一为 `SuperLee <57142635+yuebo119@users.noreply.github.com>`（filter-repo，树哈希零差异），Dependabot 整体移除（`6cb5159`），远端已同步。版权面无任何其他实质贡献者，重许可不再需要第三方同意。
2. **PalORM 6.8.0 上游转为 Apache-2.0**（本机 nuspec 实证 `<license type="expression">Apache-2.0</license>`）。此前审计反复点名的「store 集成包传递拉入 AGPL-3.0-only 形成 fact 分层许可」问题（audit-2026-09-15 §D-2、audit-2026-09-20 §D1）随上游切换消失，依赖链全量宽松化。

同时，多轮审计将 AGPL 记为商业消费方的法务评估阻力项（chief-audit D-3「对被业务团队引用的框架是采用阻力项」、audit-2026-09-20 D1 [高]）。维护者于 2026-10-26 裁决转向最大采用面。

## 决策

仓库许可证由 AGPL-3.0-or-later 改为 **Apache-2.0**（SPDX：`Apache-2.0`），同一次提交内机械同步全部锚点：

| # | 锚点 | 变更 |
|---|------|------|
| 1 | `LICENSE` | 整体替换为 apache.org 官方全文（11,358 字节，curl 原文下载） |
| 2 | `Directory.Build.props` `<PackageLicenseExpression>` | `AGPL-3.0-or-later` → `Apache-2.0`（35 包单点） |
| 3 | `README.md` 徽章 + 许可说明段 | 徽章换 `Apache--2.0`；说明段改写（专利授权/NOTICE 保留/商标不授权/PalORM 6.8.0 同源宽松） |
| 4 | `README.en.md` 徽章 + 许可说明段 | 同上（英文） |
| 5 | `README.md` / `README.en.md` 版权行 | `Copyright (C) 2026 PalDDD` → `Copyright (C) 2026 SuperLee`（对齐单一版权人） |
| 6 | `docs/release.md` nuspec 校验行 + 发布检查项 | 表达式与 License 检查值同步 |
| 7 | `NOTICE`（新增） | Apache-2.0 §4(d) 分发保留义务锚点：产品名 + 版权人 SuperLee |
| 8 | `README.md` / `AGENTS.md` ADR 计数声明 | 35 → 36（本 ADR） |
| 9 | `CHANGELOG.md` `[Unreleased]` Changed | 本变更条目 |

历史记录不改写：`docs/review/**`、`docs/decisions/001~035`、CHANGELOG 已发布段中的 AGPL 表述属当时事实，保留原样。

## 被否方案

- **维持 AGPL-3.0-or-later**：2026-09-19 曾裁决维持（社区策略导向，强制 SaaS 反馈源码）；被否原因：两个前提已变（单一版权 + PalORM 宽松化），且采用阻力项在每轮审计中反复出现，维护者裁决转向最大采用面。复活触发器：维护者推翻本 ADR。
- **MIT**：与 Apache-2.0 同为宽松许可，但无显式专利授权与专利报复终止条款；Apache-2.0 在 .NET 生态企业接受度等同且法律面更完整。不选。
- **双许可（AGPL + 商业授权）**：需要建立商业授权通道、报价与法务流程，单一维护者成本不成比例。复活触发器：出现付费商业授权需求。
- **Apache-2.0 + CLA/DCO 机制**：当前全历史唯一实质提交者为 SuperLee，贡献者协议无适用对象；若未来接受外部贡献（复活触发器：首个非 SuperLee 实质 PR），再评估接入 CLA 以保持重许可自由。

## 影响与迁移

- **下游义务面**：网络服务开源义务（AGPL §13）不再适用；分发（含闭源商用集成）须保留 LICENSE 与 NOTICE 副本、对修改作声明；获得专利授权，商标不授权。
- **存储栈**：PalORM 6.8.0 起同为 Apache-2.0，PalDDD 全依赖链无 copyleft 组件；旧审计项「or-later 对 only 组件承诺过宽」（D-2 族）就此关闭。
- **不可追溯项**：tag v1.0.0-preview.1 ~ v4.0.0 与任何已发布 GitHub Release / NuGet.org 历史包保持 AGPL-3.0-or-later；消费方锁定旧版本时按旧许可评估。
- **门禁**：`license-policy` 只判定**依赖**许可（Apache-2.0 本就在白名单第 86 行），零改动；本仓自身许可的机械锚点由 count-audit 扫描面覆盖（README/AGENTS 声明一致性）。
- **升级指引**：无代码面变更；仅当消费方以 nuspec license 元数据做合规扫描时，新版本标识将从 AGPL-3.0-or-later 变为 Apache-2.0。

## 证据锚点表

| 断言 | 锚点 |
|------|------|
| 前许可为 AGPL-3.0-or-later | 改写前 `Directory.Build.props:81`（git 历史 `6cb5159^`）；LICENSE 改写前全文（AGPLv3） |
| 官方全文逐字节一致 | `LICENSE`（11,358 字节，源 https://www.apache.org/licenses/LICENSE-2.0.txt） |
| PalORM 6.8.0 = Apache-2.0 | `~/.nuget/packages/palorm.core/6.8.0/palorm.core.nuspec` `<license type="expression">Apache-2.0</license>` |
| 单一版权人 | `git log --branches --tags`（948/948 唯一身份 SuperLee，2026-10-26 审计） |
| 维持 AGPL 的历史裁决 | `docs/review/audit-2026-09-17-independent.md:401`、`docs/review/audit-2026-09-19-findings-confirmation.md` §D-3 开放问题 |

## 开放决策点

- 无。若未来接受外部贡献（首个非 SuperLee 实质 PR），重开 CLA/DCO 评估以保持重许可自由。
