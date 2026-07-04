# 分支与发布流程

## 分支职责

| 分支 | 用途 | 规则 |
|------|------|------|
| `main` | GitHub 对外发布分支 | 只从 `dev` 合并，禁止直接修改、禁止从其他分支合并 |
| `dev` | 日常开发主分支 | PR 合并到这里，功能开发在这里 |
| `feature/*` | 功能/修复分支 | 从 `dev` 创建，合并回 `dev` |
| `master-archive` | 历史归档 | 只读，禁止任何操作 |

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
git push origin main            # 推送 GitHub
git checkout dev                # 切回 dev
```

## 强制规则

1. ❌ 禁止 `git push origin main --force`（会覆盖远程历史）
2. ❌ 禁止在 `main` 分支上直接提交
3. ❌ 禁止从 `master-archive` 合并到任何分支
4. ✅ 所有推送到 GitHub 的代码必须先合并到 `dev`，再合并到 `main`
5. ✅ PR 合并使用 Squash and merge（保持 main 历史干净）
