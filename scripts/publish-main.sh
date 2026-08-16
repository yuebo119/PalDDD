#!/bin/bash
# 发布到 origin/main — 单次干净提交
set -euo pipefail
name="${1:-publish}"
msg="${2:-Pal.DDD release}"
branch="publish-$(date +%Y%m%d-%H%M%S)"
# 记录原分支用于结束后切回（仓库无本地 master 分支，旧脚本 checkout master 必失败）
orig="$(git branch --show-current)"
orig="${orig:-main}"
git checkout --orphan "$branch"
git add .
git commit --no-verify -m "$msg"
git push origin "$branch":main --force --no-verify
git checkout "$orig"
git branch -D "$branch"
echo "✅ Published to origin/main"
