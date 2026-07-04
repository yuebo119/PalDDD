#!/bin/bash
# 发布到 origin/main — 单次干净提交
set -e
name="${1:-publish}"
msg="${2:-Pal.DDD release}"
branch="publish-$(date +%Y%m%d-%H%M%S)"
git checkout --orphan "$branch"
git add .
git commit --no-verify -m "$msg"
git push origin "$branch":main --force --no-verify
git checkout master
git branch -D "$branch"
echo "✅ Published to origin/main"
