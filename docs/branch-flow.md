# 分支与发布流程

## 分支职责

| 分支 | 用途 | 规则 |
|------|------|------|
| `main` | GitHub 对外发布分支 | 只从 `dev` 合并，禁止从其他分支合并；推送仅限维护者账号 |
| `dev` | 日常开发主分支 | PR 合并到这里，功能开发在这里 |
| `feature/*` | 功能/修复分支 | 从 `dev` 创建，合并回 `dev` |

## 日常开发

```bash
# 从 dev 创建功能分支
git checkout dev
git checkout -b feature/my-feature

# 开发完成后合并回 dev
git checkout dev
git merge feature/my-feature   # 或 Squash and merge
```

## 发布到 GitHub

```bash
git checkout main
git merge dev                   # Fast-forward，dev 的提交追加到 main
git push origin main            # 推送 GitHub（仅维护者账号放行）
git checkout dev                # 切回 dev
```

> **发布一律经 PR/人工合并，仓库无强推脚本**（`scripts/publish-main.sh` 已删除）。日常按上面的维护者本机合并后直接推送执行，`protect-main-push` 只放行维护者账号。

## GitHub 侧强制规则

仓库侧由 ruleset 机械强制（2026-10-28 起，状态经 API 核对，并用临时分支做双向实推验证）：

| 规则集 | 匹配 | 规则 | bypass |
|--------|------|------|--------|
| `protect-main-push` | 默认分支 `main` | `update`：仅 bypass 列表可更新 | 仅 `yuebo119` |
| `protect-main-history` | 默认分支 `main` | `deletion` + `non_fast_forward` | 无，维护者本人也拦 |
| `protect-release-tags` | `refs/tags/v*` | `deletion` + `non_fast_forward` | 仅 `yuebo119` |

- 仓库仅一名协作者（维护者，admin 角色），无 deployment key，无待接受邀请。`dev` 无 ruleset，安全性来自「无人有写权限」，引入协作者前须先补规则。
- 管理员身份不自动豁免：无 bypass 的 ruleset 下维护者推送会被拒（`GH013 Cannot update this protected ref`），把维护者加入 bypass 后同一推送成功（`Bypassed rule violations`）。
- Actions 默认 `GITHUB_TOKEN` 只读；`release.yml` 内的 `contents: write` 令牌不在任何 bypass 列表，改写 `main` 与已发布 tag 同样被拒。
- Actions 已启用 `sha_pinning_required`：`uses:` 必须为 40 位 commit SHA（与 `config-policy` 门禁 C 项同口径）。
- 例外通道：需要从 `main` 历史移除泄漏提交时，先临时停用 `protect-main-history`（Settings → Rules），处理完立即恢复。

## 强制规则

1. ❌ 禁止 `git push origin main --force`（会覆盖远程历史）：`protect-main-history` 强制，无 bypass
2. ❌ 非维护者向 `main` 推送提交：`protect-main-push` 强制，仅维护者在 bypass 列表
3. ✅ 所有推送到 GitHub 的代码必须先合并到 `dev`，再合并到 `main`
4. ✅ PR 合并使用 Squash and merge（保持 main 历史干净）
