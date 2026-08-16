#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔬 refine-scan.sh — Pal.DDD 精炼扫描 v7.1
# ═══════════════════════════════════════════════════════════════
# 输出格式：ID [信噪比] 名称: 命中数  — 提示
#   信噪比标注（基于 2026-07 实测校准）：
#     🟢 命中数 ≈ 可改数（扫描可信）
#     🟡 命中数 > 可改数（需人工核实返回类型/上下文）
#     🔴 命中数 ≫ 可改数（高假阳性，多数不可改，仅作线索）
#
# ⚠️ 命中数 ≠ 可改数。采纳任何精炼前，逐条 read_file 核实语义，
#    并遵循诊断三步骤（改前基线 → 单项验证 → build/test 反向验证）。
#
# 实测校准基线（commit 4459e23，供偏差参考）：
#   M1 报 15 → 实际可改 5（Array.Empty 命中 ReadOnlyMemory 返回类型不可改）
#   M6 报 19 → 实际可改 0（18 惯用 ?? throw + 1 在 netstandard2.0）
#   M3 报 87 → 实际可改 0（全为 ORM 映射类，required 不适用）
#   O1 报 5  → 实际可改 0（全为 ToFrozenDictionary 构建器/外部 API 传入）
# ═══════════════════════════════════════════════════════════════
set -euo pipefail
# 位置无关仓库根发现（元审计脚本#1-3/GAP31 修复）：向上查找含 PalDDD.slnx 的目录——
# 同一文件在根 scripts/ 与 .ai/scripts/ 均可直接运行，杜绝搬移后 cd 层数不匹配的整体 no-op
_ai_root_find() { local d="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; while [ "$d" != "/" ] && [ ! -f "$d/PalDDD.slnx" ]; do d="$(dirname "$d")"; done; printf '%s' "$d"; }
ROOT="$(_ai_root_find)"
SRC="$ROOT/src"

# 计数辅助：grep -c 无匹配返回 exit 1，set -e 下需兜底
count() { grep -rn "$@" "$SRC" --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep -v '/bin/' | grep -c . || true; }

echo "═══════ 一类:减法 ═══════"
echo "A1 🟢 AssemblyInfo: $(find "$SRC" -name AssemblyInfo.cs -not -path '*/obj/*' | wc -l | tr -d ' ')  — 删文件→csproj InternalsVisibleTo"
echo "A2 🟢 GlobalUsings: $(find "$SRC" -name GlobalUsings.cs -not -path '*/obj/*' | wc -l | tr -d ' ')  — 删文件→csproj Using 项"
echo "A3 🟡 标记接口/常量(≤10行): $(find "$SRC" -name '*.cs' -not -path '*/obj/*' -exec wc -l {} \; 2>/dev/null | awk '$1<=10 && $1>0' | wc -l | tr -d ' ')  — 需核实是否独立语义(enum+record 不合并)"
echo "A5 🟢 using 行密度: $(grep -rn '^using ' "$SRC" --include='*.cs' 2>/dev/null | grep -v '/obj/' | wc -l | tr -d ' ') 行  — dotnet format IDE0005 清冗余"

echo ""
echo "═══════ 二类:现代化 ═══════"
echo "M1a 🟡 Array.Empty<T>(): $(count 'Array\.Empty<')  — ⚠️ 返回类型 byte[]/T[] 可改[]；ReadOnlyMemory/Span/Memory 不可改(CS9174)"
echo "M1b 🟢 List/Dict 空构造: $(count 'new List<[^>]*>()\|new Dictionary<[^>]*>()')  — new T<>()→[](无容量/comparer 时)"
echo "M2 🔴 ?? throw 字段初始化: $(count 'private readonly.*?? throw')  — 高假阳性：主构造函数在继承链/ORM 类风险高，多不可下沉"
echo "M3 🔴 public {get;}: $(grep -rn '{ get; }' "$SRC" --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep 'public' | grep -cv 'set\|init\|static\|=>' || true)  — 高假阳性：ORM 映射类不能用 required(需无参构造)"
echo "M5 🟢 string.Format: $(count 'string\.Format\|String\.Format')  — →\$\"{x}\" 插值"
M6_RAW=$(count 'throw new ArgumentNullException')
M6_STANDALONE=$(grep -rn 'throw new ArgumentNullException' "$SRC" --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep -v '///\|Suppress' | grep -cv '?? throw\|??throw' || true)
echo "M6 🟡 独立 throw ArgumentNullException: ${M6_STANDALONE} (总 ${M6_RAW}, 排除 ?? throw 惯用法)  — →ThrowIfNull；⚠️ netstandard2.0 项目(Analyzers/SourceGen)不支持此 API"

echo ""
echo "═══════ 三类:优化 ═══════"
echo "O1 🔴 new Dictionary<>: $(count 'new Dictionary<')  — 高假阳性：ToFrozenDictionary 构建器/外部 API(comparer/headers)传入，多不可改"
echo "O2 🟢 List/Dict 无预分配: $(count 'new List<[^>]*>()\|new Dictionary<[^>]*>()')  — new(N) 预分配(已知容量时)"
echo "O3 🟡 ToArray/ToList: $(grep -rn '\.ToArray()\|\.ToList()' "$SRC" --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep -cv 'InMemory\|Test' || true)  — 热路径→Span<T> 零分配(非热路径不改)"
echo "O6 🟡 string +=: $(count '+= .*\"')  — 循环内→StringBuilder/插值(单次拼接不改)"

echo ""
echo "═══ 扫描完成 · 命中数≠可改数，逐条核实后按诊断三步骤采纳 ═══"
