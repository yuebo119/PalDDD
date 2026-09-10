#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 🔐 secret-scan.sh — 受跟踪文件硬编码凭据扫描（ITM-616）
# ─────────────────────────────────────────────────────────────
# 定位：补齐 docs/pitfalls.md SE2 声称但此前缺失的"凭据入库"机械防线。
# 范围：仅扫描 git 跟踪文件（与 SE2 口径一致）。
# 退出：发现高置信凭据 exit 1；无发现 exit 0。
# 用法：bash scripts/secret-scan.sh
#
# 设计纪律（宁缺毋滥——低精度门禁比无门禁更坏，会被噪声淹没后失效）：
#   1. 只报**高置信度**形态（云密钥前缀格式、连接串密码、私钥块头），
#      不报 `token:`/`secret:` 等泛词赋值（实测在干净仓库产生 52 处误报）。
#   2. 只扫跟踪文件（git ls-files），与 SE2 口径一致。
#   3. 输出不含凭据明文——仅打印 文件:行号:模式名（P0 #1 防二次泄露）。
#   4. 不用 inline (?i)（GNU grep ERE 不支持 → 会静默恒绿）；大小写敏感用 -i 标志。
# ─────────────────────────────────────────────────────────────
set -uo pipefail

mapfile -t files < <(git ls-files | grep -iE '\.(json|cs|sh|py|yml|yaml|props|config|toml|env|xml|ps1|txt|md|csproj)$' || true)
[ "${#files[@]}" -eq 0 ] && { printf 'PASS 无可扫描的跟踪文件\n'; exit 0; }

found=0
report() { printf 'SUSPECT [%s] %s:%s\n' "$1" "$2" "$3"; found=$((found + 1)); }

# ── 模式 1：已知云密钥/令牌前缀格式（极高信度，零误报）──
#   AWS 访问键 AKIA/ASIA+16 位大写字母数字；GitHub ghp_/github_pat_；
#   Slack xox[baprs]-；OpenAI sk-；PEM 私钥块头。
while IFS=: read -r file lineno _; do
    [ -z "$file" ] && continue
    report "已知密钥格式" "$file" "$lineno"
done < <(grep -nIE 'AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----' "${files[@]}" 2>/dev/null || true)

# ── 模式 2：连接串内嵌真实密码（SE2 原始场景）──
#   同一行同时含 主机键(Host=/Server=/Data Source=) 与 密码键(Password=/Pwd=)，
#   且密码值不在占位符白名单。占位符（test/postgres/root/guest/...）放行。
while IFS=: read -r file lineno content; do
    [ -z "$file" ] && continue
    # 提取 Password/Pwd 的值（= 后到 ; 或 " 或行尾）
    value="$(printf '%s' "$content" | grep -ioE '(password|pwd)[[:space:]]*=[[:space:]]*"?[^;", ]+' | head -1 | sed -E 's/^[^=]*=[[:space:]]*"?//' || true)"
    [ -z "$value" ] && continue
    # 占位符/示例值放行：显式占位词，或含 example/sample/demo/placeholder 等示例语义的子串。
    if printf '%s' "$value" | grep -qiE '^(test|postgres|root|guest|password|pass|pwd|secret|example|changeme|yourpassword|probe|localhost|none|default)$|example|sample|demo|placeholder|dummy|fake|mock'; then continue; fi
    # 示例密码形态：<词>-pass / secret123 / <占位词>NNN——文档与测试构造输入的常见无害形态。
    if printf '%s' "$value" | grep -qiE '^([a-z0-9]+-)?pass$|^secret[0-9]+$|^[a-z]+-pass$'; then continue; fi
    # 跳过长于常见占位/短值；真实密码通常含数字或长度 ≥ 10
    if ! printf '%s' "$value" | grep -qE '[0-9]' && [ "${#value}" -lt 10 ]; then continue; fi
    report "连接串内嵌密码" "$file" "$lineno"
done < <(grep -inIE '(Host|Server|Data Source|数据源)[[:space:]]*=.*(Password|Pwd)[[:space:]]*=' "${files[@]}" 2>/dev/null || true)

if [ "$found" -gt 0 ]; then
    printf '\n发现 %d 处疑似硬编码凭据（上方仅列位置，未打印明文）。\n' "$found"
    printf '请改用环境变量或用户机密（User Secrets / CI Secrets），勿入库。\n'
    exit 1
fi

printf 'PASS 受跟踪文件无高置信硬编码凭据（%d 文件已扫描）\n' "${#files[@]}"
exit 0
