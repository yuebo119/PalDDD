#!/usr/bin/env bash
# 验证行动项中的文件路径和源码标识符是否存在。

set -euo pipefail

# 自审计 A3 修复：位置无关仓库根发现——此前 [ -e ]/git grep 均相对当前目录，
# 从子目录运行会把真实存在的路径误报 FAIL（与其他脚本的 _ai_root_find 同款）
_ai_root_find() { local d="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; while [ "$d" != "/" ] && [ ! -f "$d/PalDDD.slnx" ]; do d="$(dirname "$d")"; done; printf '%s' "$d"; }
ROOT="$(_ai_root_find)"
cd "$ROOT"

if [ $# -ne 1 ]; then
    printf '用法：bash scripts/verify-action-items.sh <action-items-file>\n' >&2
    exit 2
fi

ACTION_FILE="$1"
if [ ! -f "$ACTION_FILE" ]; then
    printf '错误：文件不存在：%s\n' "$ACTION_FILE" >&2
    exit 2
fi

printf '═══════ Action Items 验证 ═══════\n'
printf '文件：%s\n\n' "$ACTION_FILE"

MISSING=0
FOUND=0
SKIPPED=0

is_path() {
    [[ "$1" == */* ]] || [[ "$1" =~ \.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml)$ ]]
}

is_ignored_token() {
    [[ "$1" =~ ^(P[0-3]|AUD-[0-9]+|ITM(-[0-9]+)?|PASS|FAIL|WARN|SKIP|urgent|near|future|assess)$ ]]
}

# 2026-08-15 实践教训：反引号里的散文引用（SQL 片段/带引号值/表达式/commit hash）
# 被当标识符 grep 产生误报（9 处）。含标识符/路径中不可能出现的字符 → 跳过。
is_prose_token() {
    # 正则放变量规避 [[ =~ ]] 内引号转义问题（2026-08-15 实测：内联字符类漏判 =( ) 等字符）
    local prose_chars='[()<>=?*{}">[:space:]]'
    [[ "$1" =~ $prose_chars ]] && return 0
    # 纯十六进制 commit hash（7-40 位，无字母 o-z 混入）
    [[ "$1" =~ ^[0-9a-f]{7,40}$ ]] && return 0
    # 2026-08-16 二轮优化：`File.cs:NN` 行号引用与 `--flag` 命令行开关不是源码标识符
    [[ "$1" =~ :[0-9]+$ ]] && return 0
    [[ "$1" == --* ]] && return 0
    # 含 / 但既无文件扩展名也不以已知目录开头（如 "Xxx/Yyy" 方法对）→ 散文
    if [[ "$1" == */* ]] \
       && [[ ! "$1" =~ \.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml|sql)$ ]] \
       && [[ ! "$1" =~ ^(src|test|docs|scripts|bench|samples|\.ai|\.github|nupkgs)/ ]]; then
        return 0
    fi
    return 1
}

while IFS= read -r identifier; do
    [ -z "$identifier" ] && continue
    if is_ignored_token "$identifier"; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi
    if is_prose_token "$identifier"; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi

    if is_path "$identifier"; then
        if [ -e "$identifier" ]; then
            FOUND=$((FOUND + 1))
        elif [[ "$identifier" != */* ]] && git grep -F -q -- "$identifier" src test scripts docs .github .ai 2>/dev/null; then  # 元审计脚本#30：补 .ai——验证 AI 系统自身行动项（PD21/tech-debt 项）时找不到会误报
            # 无目录前缀的相对文件名（如模板名）——文件不存在但正文有引用则放行
            FOUND=$((FOUND + 1))
        else
            printf 'FAIL 文件不存在：%s\n' "$identifier"
            MISSING=$((MISSING + 1))
        fi
        continue
    fi

    if git grep -F -q -- "$identifier" src test scripts docs .github .ai 2>/dev/null; then  # 元审计脚本#30：补 .ai——验证 AI 系统自身行动项（PD21/tech-debt 项）时找不到会误报
        FOUND=$((FOUND + 1))
    else
        printf 'FAIL 标识符未找到：%s\n' "$identifier"
        MISSING=$((MISSING + 1))
    fi
done < <(grep -oE '`[^`]+`' "$ACTION_FILE" | tr -d '`' | sort -u || true)

printf '\n找到：%s  缺失：%s  跳过：%s\n' "$FOUND" "$MISSING" "$SKIPPED"
printf '═══════ 验证完成 ═══════\n'

[ "$MISSING" -eq 0 ]
