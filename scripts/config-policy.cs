// ============================================================================
// config-policy.cs——配置文件策略门禁（A：dependabot 配置词表/结构；B：Directory.Build.* 构建期副作用；C：GitHub Actions 引用钉 SHA）
//
// 为什么三个检查合体为一个脚本：三者同属「配置文件策略」类——只读仓库根的
// 配置文件、纯静态判定、零判定交叉（A 的输入是 dependabot YAML、B 的输入是
// MSBuild 元素、C 的输入是 workflow YAML，互不读取对方文件），合体共享一次
// 接线成本（pre-commit 触发分支、ci.yml 步骤、gate-audit 探针与 gateForms
// 声明各只需一处），失败输出统一为同一份违规清单。
//
// 实践出处（三条本轮真实事故/风险，各锁一道检查）：
//   1) .github/dependabot.yml 的 groups.update-types 误用 ignore 规则词表
//      version-update:semver-*——两处 schema 词表不同是已知陷阱；该错误只在
//      配置落到默认分支时由 Dependabot 配置检查报出，dev 上长期不报，直至
//      main 上失败。→ 检查 A。
//   2) 构建期 git 副作用打断流水线：根 Directory.Build.targets 的
//      ConfigureGitHooks 并发写 .git/config，Exec 失败被 ContinueOnError 降级
//      为 MSB3073 警告，release 步骤 -warnaserror 再升级为错误——v3.1.0 首发
//      发布链当场中断（已修：Target 加 '$(GITHUB_ACTIONS)' != 'true' 跳过）。
//      → 检查 B 守住该形态不再回潮。
//   3) GitHub Actions 的 uses 若写可变 major 标签（@v4），上游标签可被移动或
//      被入侵后指向任意提交，CI 会以仓库 token 执行非预期代码（供应链风险）。
//      → 检查 C：所有 uses 引用钉 40 位 commit SHA，并带 `# vX.Y.Z` 版本注释
//      （注释供人读；Dependabot 的 github-actions 更新会同步改写 SHA+注释）。
//
// ── 检查 A 的边界（必须说清）：这是针对已知陷阱点的**行式校验**，不是完整
//    YAML 解析器——按缩进维护上下文栈，只认规则清单内的键与单行 flow 序列；
//    多行 flow（`key: [` 换行列表 `]`）、锚点/别名、多文档结构不在覆盖面内。
//    GitHub 的 Dependabot 配置检查仍是最终权威，本检查是本地前移防线。
//    规则清单：
//    A1 groups.*.update-types 取值 ∈ {patch, minor, major}（块/内联两形态）
//    A2 ignore[].update-types 取值 ∈ {version-update:semver-major/minor/patch}
//       （A1/A2 两处词表不同——本仓 2026-09-23 实证过的坑）
//    A3 ignore[] 条目须有 versions（≥1 项且无空串）；仅有合法 update-types
//       的条目是 Dependabot 支持的变体，放行
//    A4 每条 updates entry 的 labels 非空（缺失 / 零项 / 空串项均违规）
//    A5 commit-message.prefix 行存在时必须非空（commit-message 整体可选，
//       缺 prefix 键不报——Dependabot 有默认前缀）
//    A6 每条 updates entry 必须有非空 schedule.interval
//    A7 解析不出任何 updates entry → fail-closed
//    未知 update-types 取值（或 update-types 出现在 groups/ignore 之外）一律
//    违规并打印行号；# 整行注释与行尾注释不参与判定。
//    检查 B：根 Directory.Build.props 与 Directory.Build.targets 中每处
//    <Exec> 必须满足二者之一——
//    B1 自带 IgnoreExitCode="true"（失败不阻断），或
//    B2 所属 <Target> 的 Condition 含 '$(GITHUB_ACTIONS)' != 'true'（CI 跳过）
//    否则违规并打印位置与缺失项。XML 注释内的 <Exec> 不计（等长遮蔽，行号
//    不移位）。两个文件缺失同样违规（fail-closed：策略文件删除须与本门禁
//    同步显式裁决，不允许静默消失）。
//    检查 C：.github/workflows/*.yml|*.yaml 中每条 `uses:` 引用——
//    C1 非本地动作（./）与非 docker:// 镜像，ref 必须是 40 位小写十六进制
//       commit SHA（可变标签/分支/短 SHA 一律违规）；
//    C2 必须有 `# vX.Y.Z` 形态的版本注释（便于评审与 Dependabot 更新）。
//    工作流目录不存在或为空时跳过 C（无引用可钉，不阻断）；文件读取失败
//    fail-closed。
//
// 用法（在仓库根执行）：
//   dotnet run scripts/config-policy.cs               全量校验（A + B + C）
//   dotnet run scripts/config-policy.cs -- --selftest 自测（判定逻辑单元验证）
//
// 退出码：0 = 全通过；1 = 有违规；2 = 自测失败（仓库根定位失败同取 2——
//         属"无法执行判定"，与"有违规"区分）。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是门禁固定
// 协议行（PASS/FAIL 与违规明细被人工与 grep 消费），固定中文非用户可配文案
// ——沿 secret-scan.cs / xml-guard.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

// ─── 参数路由 ───
if (args.Contains("--selftest"))
{
    return SelfTest();
}

var root = FindRepoRoot();
var staged = GitStagedSet(root);
var violations = new List<string>();
violations.AddRange(CheckDependabot(root, staged));
violations.AddRange(CheckDirectoryBuild(root, staged));
violations.AddRange(CheckWorkflowPins(root, staged));
violations.AddRange(CheckCiRequirements(root, staged));

if (violations.Count > 0)
{
    Console.Error.WriteLine($"FAIL config-policy 违规 {violations.Count} 项（A=dependabot 词表/结构；B=Directory.Build Exec 构建期副作用；C=Actions 引用钉 SHA；D=CI requirements 平台标记）：");
    foreach (var v in violations) Console.Error.WriteLine($"  {v}");
    return 1;
}

Console.WriteLine("PASS config-policy（A dependabot 词表/结构 + B Directory.Build Exec 副作用 + C Actions 引用钉 SHA + D CI requirements 平台标记）");
return 0;

// ══════════════ 检查 A：dependabot 配置词表/结构 ══════════════

static List<string> CheckDependabot(string root, IReadOnlySet<string> staged)
{
    const string rel = ".github/dependabot.yml";
    var content = ReadPolicyFile(root, rel, staged);
    if (content is null)
        return [$"{rel} —— 文件缺失或暂存内容不可读（fail-closed：策略文件删除须与本门禁同步显式裁决）"];
    return ValidateDependabot(content, rel);
}

// 行式校验（纯函数，供 selftest 覆盖）：非完整 YAML 解析器，边界见头注释。
static List<string> ValidateDependabot(string text, string fileLabel)
{
    var violations = new List<string>();
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var stack = new List<(int Indent, string Key)>();   // 缩进上下文栈

    int updatesLine = 0;        // updates: 所在行（0 = 未见）
    int entryCount = 0;
    int entryLine = 0;          // 当前 updates entry 首行（0 = 无）
    bool entryHasInterval;
    bool entryLabelsSeen;
    int entryLabelsLine;
    int entryLabelsCount;
    int ignoreLine = 0;         // 当前 ignore 条目首行（0 = 无）
    bool igVersionsSeen;
    int igVersionsLine;
    int igVersionsCount;
    int igUpdateTypesCount;
    int utOwner = 0;            // 最近 update-types 键的归属：1=ignore 2=groups 0=未知

    entryHasInterval = false;
    entryLabelsSeen = false;
    entryLabelsLine = 0;
    entryLabelsCount = 0;
    igVersionsSeen = false;
    igVersionsLine = 0;
    igVersionsCount = 0;
    igUpdateTypesCount = 0;

    bool HasAncestor(string key) => stack.Exists(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    // 关闭当前 ignore 条目：versions 缺失且无 update-types → A3 违规
    void CloseIgnore()
    {
        if (ignoreLine == 0) return;
        if (igVersionsSeen)
        {
            if (igVersionsCount == 0)
                violations.Add($"{fileLabel}:{igVersionsLine} A3: ignore[].versions 至少一项（空列表不被接受）");
        }
        else if (igUpdateTypesCount == 0)
        {
            violations.Add($"{fileLabel}:{ignoreLine} A3: ignore 条目缺 versions（至少一项且非空串；仅 update-types 时须含合法取值）");
        }
        ignoreLine = 0;
        igVersionsSeen = false;
        igVersionsLine = 0;
        igVersionsCount = 0;
        igUpdateTypesCount = 0;
    }

    // 关闭当前 entry：A6 interval 与 A4 labels 的存在性在此裁决
    void CloseEntry()
    {
        CloseIgnore();
        if (entryLine == 0) return;
        if (!entryHasInterval)
            violations.Add($"{fileLabel}:{entryLine} A6: entry 缺 schedule.interval（须存在且非空）");
        if (!entryLabelsSeen)
            violations.Add($"{fileLabel}:{entryLine} A4: entry 缺 labels（每条 entry 的 labels 须非空）");
        else if (entryLabelsCount == 0)
            violations.Add($"{fileLabel}:{entryLabelsLine} A4: labels 为空（须至少一个非空项）");
        entryLine = 0;
        entryHasInterval = false;
        entryLabelsSeen = false;
        entryLabelsLine = 0;
        entryLabelsCount = 0;
    }

    // update-types 取值按归属词表裁决（块形态序列项与内联 flow 共用）
    void CheckUpdateTypeValue(string value, int lineNo)
    {
        if (utOwner == 2)
        {
            if (!IsValidGroupUpdateType(value))
                violations.Add($"{fileLabel}:{lineNo} A1: groups update-types 取值「{value}」不在词表 {{patch, minor, major}}（不是 ignore 的 version-update:semver-* 词表）");
        }
        else if (utOwner == 1)
        {
            if (!IsValidIgnoreUpdateType(value))
                violations.Add($"{fileLabel}:{lineNo} A2: ignore update-types 取值「{value}」不在词表 {{version-update:semver-major, version-update:semver-minor, version-update:semver-patch}}（不是 groups 的 patch/minor/major 词表）");
            igUpdateTypesCount++;
        }
        else
        {
            violations.Add($"{fileLabel}:{lineNo} update-types 出现在未知上下文（须在 groups 或 ignore 之下）");
        }
    }

    // labels / versions 的内联 flow 项记账（空串项行级报 A4/A3）
    void ConsumeFlowItems(string value, int lineNo, bool isLabels, bool isVersions)
    {
        foreach (var item in ParseFlowItems(value))
        {
            if (isLabels)
            {
                if (item.Length == 0) violations.Add($"{fileLabel}:{lineNo} A4: labels 含空项");
                else entryLabelsCount++;
            }
            else if (isVersions)
            {
                if (item.Length == 0) violations.Add($"{fileLabel}:{lineNo} A3: ignore[].versions 含空串项");
                else igVersionsCount++;
            }
        }
    }

    for (int i = 0; i < lines.Length; i++)
    {
        var raw = lines[i];
        int lineNo = i + 1;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') continue;   // 整行注释/空行不参与判定

        int indent = raw.Length - raw.TrimStart(' ').Length;
        while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
        var parentKey = stack.Count > 0 ? stack[^1].Key : "";

        var content = trimmed;
        bool isItem = false;
        if (content.StartsWith("- ", StringComparison.Ordinal)) { isItem = true; content = content[2..].TrimStart(); }
        else if (content == "-") { isItem = true; content = ""; }

        // 冒号后须有空白才是键分隔（`version-update:semver-patch` 是标量不是键）
        int sep = FindKeySeparator(content);
        if (sep < 0)
        {
            if (!isItem || stack.Count == 0) continue;   // 无法识别的续行——行式边界，跳过
            var parent = stack[^1].Key;
            var value = NormalizeValue(content);
            if (parent == "update-types")
            {
                CheckUpdateTypeValue(value, lineNo);
            }
            else if (parent == "labels" && entryLine != 0)
            {
                if (value.Length == 0) violations.Add($"{fileLabel}:{lineNo} A4: labels 含空项");
                else entryLabelsCount++;
            }
            else if (parent == "versions" && ignoreLine != 0)
            {
                if (value.Length == 0) violations.Add($"{fileLabel}:{lineNo} A3: ignore[].versions 含空串项");
                else igVersionsCount++;
            }
            continue;
        }

        var key = content[..sep].Trim();
        var val = NormalizeValue(content[(sep + 1)..]);

        // 条目边界：直接父是 updates 的序列项 → 开新 entry
        if (isItem && parentKey == "updates")
        {
            CloseEntry();
            entryLine = lineNo;
            entryCount++;
            stack.Add((indent, key));
            continue;
        }

        switch (key)
        {
            case "updates":
                if (updatesLine == 0) updatesLine = lineNo;
                break;
            case "dependency-name" when parentKey == "ignore":
                CloseIgnore();
                ignoreLine = lineNo;
                break;
            case "versions" when ignoreLine != 0 && HasAncestor("ignore"):
                igVersionsSeen = true;
                igVersionsLine = lineNo;
                if (val.Length > 0) ConsumeFlowItems(val, lineNo, isLabels: false, isVersions: true);
                break;
            case "update-types":
                utOwner = HasAncestor("ignore") ? 1 : HasAncestor("groups") ? 2 : 0;
                if (val.Length > 0)
                    foreach (var item in ParseFlowItems(val)) CheckUpdateTypeValue(item, lineNo);
                break;
            case "labels" when entryLine != 0:
                entryLabelsSeen = true;
                entryLabelsLine = lineNo;
                if (val.Length > 0) ConsumeFlowItems(val, lineNo, isLabels: true, isVersions: false);
                break;
            case "interval" when entryLine != 0 && HasAncestor("schedule"):
                // 键存在即记账（避免与 entry 级"缺 interval"重复报）；空值由行级单独报
                entryHasInterval = true;
                if (val.Length == 0) violations.Add($"{fileLabel}:{lineNo} A6: schedule.interval 值为空");
                break;
            case "prefix" when HasAncestor("commit-message"):
                if (val.Length == 0) violations.Add($"{fileLabel}:{lineNo} A5: commit-message.prefix 为空（行存在则须非空）");
                break;
        }

        stack.Add((indent, key));
    }

    CloseEntry();
    if (entryCount == 0)
        violations.Add($"{fileLabel}:{(updatesLine > 0 ? updatesLine : 1)} A7: 未解析出任何 updates entry（fail-closed：文件可能已被重写为本行式校验无法识别的形态）");

    return violations;
}

static bool IsValidGroupUpdateType(string value) => value is "patch" or "minor" or "major";

static bool IsValidIgnoreUpdateType(string value) =>
    value is "version-update:semver-major" or "version-update:semver-minor" or "version-update:semver-patch";

// 冒号后有空白（或行尾）才是键分隔；i>0 防空键。`version-update:semver-patch`
// 冒号后非空白 → 整串是标量（与 YAML 语义一致）。
static int FindKeySeparator(string s)
{
    for (int i = 1; i < s.Length; i++)
        if (s[i] == ':' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]))) return i;
    return -1;
}

// 取键值：去空白 → 已引号则取引号内（忽略引号后的行尾注释）→ 未引号则切 ` #` 注释尾
static string NormalizeValue(string rawValue)
{
    var v = rawValue.Trim();
    if (v.Length >= 2 && (v[0] == '"' || v[0] == '\''))
    {
        char q = v[0];
        int end = v.IndexOf(q, 1);
        return end > 0 ? v[1..end] : v[1..];
    }
    int hash = v.IndexOf(" #", StringComparison.Ordinal);
    if (hash >= 0) v = v[..hash];
    return v.Trim();
}

// 单行 flow 序列 `[a, b]` 拆项（无括号的单标量按一项处理；引号项去引号；
// 多行 flow 不在行式覆盖面内——见头注释边界）
static List<string> ParseFlowItems(string value)
{
    var v = value.Trim();
    if (v.StartsWith('['))
    {
        int end = v.EndsWith(']') ? v.Length - 1 : v.Length;
        v = v[1..end];
    }
    if (v.Trim().Length == 0) return [];
    var items = new List<string>();
    foreach (var part in v.Split(','))
    {
        var p = part.Trim();
        if (p.Length >= 2 && (p[0] == '"' || p[0] == '\''))
        {
            char q = p[0];
            int end = p.IndexOf(q, 1);
            p = end > 0 ? p[1..end] : p[1..];
        }
        else
        {
            int hash = p.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) p = p[..hash].TrimEnd();
        }
        items.Add(p);
    }
    return items;
}

// ══════════════ 检查 B：Directory.Build.* 构建期副作用 ══════════════

static List<string> CheckDirectoryBuild(string root, IReadOnlySet<string> staged)
{
    string[] files = ["Directory.Build.props", "Directory.Build.targets"];
    var violations = new List<string>();
    foreach (var rel in files)
    {
        var content = ReadPolicyFile(root, rel, staged);
        if (content is null)
            violations.Add($"{rel} —— 文件缺失或暂存内容不可读（fail-closed：策略文件删除须与本门禁同步显式裁决）");
        else
            violations.AddRange(ValidateDirectoryBuildExec(content, rel));
    }
    return violations;
}

// 每处 <Exec> 必须满足 B1 IgnoreExitCode="true" 或 B2 所属 Target 的 Condition
// 含 'GITHUB_ACTIONS' != 'true'（纯函数，供 selftest 覆盖）。
static List<string> ValidateDirectoryBuildExec(string text, string fileLabel)
{
    var violations = new List<string>();
    var masked = MaskXmlComments(text);   // 等长遮蔽：注释内 <Exec> 不计且行号不移位

    // 预扫描 <Target>：开标签结束位、闭标签位、Condition 属性
    var targets = new List<(int OpenEnd, int Close, string Cond)>();
    foreach (Match m in Regex.Matches(masked, "<Target\\b", RegexOptions.IgnoreCase))
    {
        int openEnd = FindTagEnd(masked, m.Index);
        if (openEnd < 0) continue;   // 未闭合标签——无从判定归属，跳过 Target 扫描
        int close = masked.IndexOf("</Target>", openEnd, StringComparison.OrdinalIgnoreCase);
        string cond = ExtractAttr(masked, m.Index, openEnd, "Condition") ?? "";
        targets.Add((openEnd, close < 0 ? int.MaxValue : close, cond));
    }

    foreach (Match m in Regex.Matches(masked, "<Exec\\b", RegexOptions.IgnoreCase))
    {
        int tagEnd = FindTagEnd(masked, m.Index);
        if (tagEnd < 0) continue;
        if (Regex.IsMatch(masked[m.Index..tagEnd], "IgnoreExitCode\\s*=\\s*[\"']true[\"']", RegexOptions.IgnoreCase))
            continue;   // B1 满足

        int owner = -1;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].OpenEnd <= m.Index && m.Index < targets[i].Close) owner = i;
        if (owner >= 0 && HasCiSkipCondition(targets[owner].Cond))
            continue;   // B2 满足

        int line = LineOf(masked, m.Index);
        string where = owner >= 0
            ? "所属 <Target> 的 Condition 无 'GITHUB_ACTIONS' != 'true'（CI 跳过）"
            : "不在任何 <Target> 内（无所属 Condition 可提供 CI 跳过）";
        violations.Add($"{fileLabel}:{line} <Exec> 缺 IgnoreExitCode=\"true\"，且{where}——构建期副作用失败会被 -warnaserror 升级为流水线错误（v3.1.0 发布链事故形态）");
    }
    return violations;
}

// ══════════════ 检查 C：GitHub Actions 引用钉 SHA ══════════════

static List<string> CheckWorkflowPins(string root, IReadOnlySet<string> staged)
{
    var dir = Path.Combine(root, ".github", "workflows");
    if (!Directory.Exists(dir)) return [];   // 无工作流 = 无引用可钉，不阻断
    var files = Directory.GetFiles(dir, "*.yml", SearchOption.TopDirectoryOnly)
        .Concat(Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) return [];

    var violations = new List<string>();
    foreach (var abs in files)
    {
        var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
        var content = ReadPolicyFile(root, rel, staged);
        if (content is null)
        {
            violations.Add($"{rel} —— 工作流文件不可读（fail-closed）");
            continue;
        }
        violations.AddRange(ValidateWorkflowPins(content, rel));
    }
    return violations;
}

// 行式校验（纯函数，供 selftest 覆盖）：只认 `uses:` 键；本地动作与 docker
// 镜像跳过。ref 必须是 40 位小写十六进制 commit SHA（统一小写便于比对），
// 且行尾必须有 `# vX[.Y[.Z]]` 版本注释。
static List<string> ValidateWorkflowPins(string text, string fileLabel)
{
    var violations = new List<string>();
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var trimmed = lines[i].Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') continue;

        var m = Regex.Match(trimmed, @"^(?:-\s*)?uses:\s*(\S+)(?:\s+#\s*(\S.*))?$");
        if (!m.Success) continue;

        var spec = m.Groups[1].Value;
        var comment = m.Groups[2].Value.Trim();
        if (spec.StartsWith("./", StringComparison.Ordinal)
            || spec.StartsWith("docker://", StringComparison.Ordinal))
            continue;

        int at = spec.LastIndexOf('@');
        if (at <= 0 || at == spec.Length - 1)
        {
            violations.Add($"{fileLabel}:{i + 1} C1: uses「{spec}」缺 @<40 位 commit SHA>");
            continue;
        }

        var action = spec[..at];
        var reference = spec[(at + 1)..];
        if (!Regex.IsMatch(reference, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            violations.Add($"{fileLabel}:{i + 1} C1: actions「{action}」引用「{reference}」不是 40 位小写 commit SHA（可变标签/分支可被上游移动）");
        if (!Regex.IsMatch(comment, @"^v[0-9]+(?:\.[0-9]+)*$", RegexOptions.CultureInvariant))
            violations.Add($"{fileLabel}:{i + 1} C2: actions「{action}」缺版本注释（行尾须为 `# vX.Y.Z`，供评审与 Dependabot 更新）");
    }
    return violations;
}

// Condition 归一化（去全部空白）后查 CI 跳过——容忍 `'$(GITHUB_ACTIONS)' != 'true'`
// 与无空格变体两种写法。
static bool HasCiSkipCondition(string condition) =>
    Regex.Replace(condition, "\\s+", "", RegexOptions.CultureInvariant)
        .Contains("'$(GITHUB_ACTIONS)'!='true'", StringComparison.Ordinal);

// XML 注释等长遮蔽（空格替换、保留换行）——行号与索引不移位
static string MaskXmlComments(string text)
{
    var chars = text.ToCharArray();
    int i = 0;
    while (i < chars.Length)
    {
        if (i + 3 < chars.Length && chars[i] == '<' && chars[i + 1] == '!' && chars[i + 2] == '-' && chars[i + 3] == '-')
        {
            int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
            int stop = end < 0 ? chars.Length - 1 : end + 2;   // 未闭合则遮到文件尾
            for (int j = i; j <= stop; j++) if (chars[j] != '\n') chars[j] = ' ';
            i = stop + 1;
        }
        else i++;
    }
    return new string(chars);
}

// 标签结束 `>`（引号感知：属性值内的 > 不截断；MSBuild Condition 里的单引号
// 字面量被包在双引号属性内，不误触单引号态）
static int FindTagEnd(string s, int start)
{
    bool inDouble = false, inSingle = false;
    for (int i = start; i < s.Length; i++)
    {
        char c = s[i];
        if (c == '"' && !inSingle) inDouble = !inDouble;
        else if (c == '\'' && !inDouble) inSingle = !inSingle;
        else if (c == '>' && !inDouble && !inSingle) return i;
    }
    return -1;
}

// 取 [start, endExclusive) 内指定属性的双引号值；无则 null
static string? ExtractAttr(string text, int start, int endExclusive, string attrName)
{
    var slice = text[start..endExclusive];
    var m = Regex.Match(slice, attrName + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value : null;
}

static int LineOf(string text, int index)
{
    int line = 1;
    for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
    return line;
}

// ══════════════ 检查 D：CI requirements 平台标记 ══════════════

// 实践出处（2026-10-19 CI run 39220268700）：ci/requirements-semgrep.txt 在
// Windows 上由 uv 生成，pywin32==311 没有平台标记；ubuntu-latest 的
// `pip install --require-hashes` 在扫描前就报 No matching distribution，整个
// semgrep job 失败。根因是「锁文件生成平台 ≠ CI 运行平台」：Windows 专用包
// 必须用 sys_platform == "win32" 排除，否则 Linux 装不上。macOS 专用同理
// （pyobjc 家族本仓未使用，先只守 Windows 名单，避免凭空误报）。
static List<string> CheckCiRequirements(string root, IReadOnlySet<string> staged)
{
    var dir = Path.Combine(root, "ci");
    if (!Directory.Exists(dir)) return [];
    var files = Directory.GetFiles(dir, "requirements-*.txt", SearchOption.TopDirectoryOnly)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) return [];

    var violations = new List<string>();
    foreach (var abs in files)
    {
        var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
        var content = ReadPolicyFile(root, rel, staged);
        if (content is null)
        {
            violations.Add($"{rel} —— requirements 文件不可读（fail-closed）");
            continue;
        }
        violations.AddRange(ValidateCiRequirements(content, rel));
    }
    return violations;
}

// 行式校验（纯函数，供 selftest 覆盖）：只认 `pkg==version` 起始行；注释、
// --hash 续行与其他形态忽略。允许两种等价排除形态：
//   pkg==ver ; sys_platform == "win32"
//   pkg==ver ; platform_system == "Windows"
static List<string> ValidateCiRequirements(string text, string fileLabel)
{
    string[] windowsOnly = ["pywin32", "pywin32-ctypes", "wincertstore", "pyreadline3", "windows-curses"];
    var violations = new List<string>();
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var trimmed = lines[i].Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') continue;
        var m = Regex.Match(trimmed, @"^([A-Za-z0-9_.-]+)==");
        if (!m.Success) continue;
        var package = m.Groups[1].Value;
        if (!windowsOnly.Any(p => string.Equals(p, package, StringComparison.OrdinalIgnoreCase))) continue;
        if (Regex.IsMatch(trimmed, @"sys_platform\s*==\s*[""']win32[""']", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmed, @"platform_system\s*==\s*[""']Windows[""']", RegexOptions.IgnoreCase))
            continue;
        violations.Add($"{fileLabel}:{i + 1} Windows 专用依赖 {package} 缺少平台标记（Linux CI 安装会失败；加 ; sys_platform == \"win32\"）");
    }
    return violations;
}

// ══════════════ 文件读取（暂存优先——提交什么查什么） ══════════════

// 已入暂存集则读暂存 blob（git add 后又改工作树时，要校验的是将被提交的内容，
// 沿 xml-guard 先例）；否则读工作树。读不出返回 null（调用方 fail-closed）。
static string? ReadPolicyFile(string root, string relPath, IReadOnlySet<string> staged)
{
    if (staged.Contains(relPath)) return GitShowStaged(root, relPath);
    var abs = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
    try
    {
        return File.Exists(abs) ? File.ReadAllText(abs) : null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

static HashSet<string> GitStagedSet(string root)
{
    var psi = new ProcessStartInfo("git", "diff --cached --name-only")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = root,
    };
    try
    {
        using var p = Process.Start(psi);
        if (p is null) return [];
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginErrorReadLine();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.WaitForExit();   // 双调用：确保异步缓冲 flush（沿 xml-guard 先例）
        return p.ExitCode == 0
            ? new HashSet<string>(output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0), StringComparer.Ordinal)
            : [];
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return [];   // git 不可用——降级读工作树，不阻塞
    }
}

// 取暂存 blob（`git show :<path>`）；非零退出/不可用返回 null（调用方 fail-closed）
static string? GitShowStaged(string root, string relPath)
{
    var psi = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = root,
    };
    psi.ArgumentList.Add("show");
    psi.ArgumentList.Add($":{relPath}");
    try
    {
        using var p = Process.Start(psi);
        if (p is null) return null;
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginErrorReadLine();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}

static string FindRepoRoot()
{
    foreach (var start in (string?[])[Environment.CurrentDirectory, AppContext.BaseDirectory])
    {
        var dir = start;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）——请在仓库内执行");
    Environment.Exit(2);
    return ""; // 不可达
}

// ══════════════ 自测（判定逻辑单元验证，不跑真实文件） ══════════════

static int SelfTest()
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // ── 检查 A 正例 ──
    var goodSample = """
        # 注释里出现 version-update:semver-patch 与 minor/patch/major 不得误报
        version: 2
        updates:
          - package-ecosystem: nuget
            directory: "/"
            schedule:
              interval: weekly
            ignore:
              - dependency-name: "Verify.TUnit"
                versions: [">= 33.0.0"]
            labels:
              - dependencies
            commit-message:
              prefix: "依赖"
            groups:
              preview:
                patterns:
                  - "Microsoft.*"
                update-types:
                  - patch
                  - minor
        """;
    Case("A 正例：贴近真实配置全通过（含注释词表字样不误报）",
        ValidateDependabot(goodSample, "d.yml").Count == 0);

    var goodIgnoreUtypes = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            ignore:
              - dependency-name: "X"
                update-types:
                  - version-update:semver-major
        """;
    Case("A 正例：ignore 仅 update-types（无 versions）合法变体放行",
        ValidateDependabot(goodIgnoreUtypes, "d.yml").Count == 0);

    var goodInline = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: daily
            labels:
              - ci
            groups:
              g:
                patterns:
                  - "*"
                update-types: [major, minor, patch]  # 行尾注释
        """;
    Case("A 正例：groups 内联 flow 三合法值 + 行尾注释",
        ValidateDependabot(goodInline, "d.yml").Count == 0);

    // ── 检查 A 负例（每条规则至少一个注入样本）──
    var badGroupsBlock = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            groups:
              g:
                patterns:
                  - "*"
                update-types:
                  - version-update:semver-patch
        """;
    var m1 = ValidateDependabot(badGroupsBlock, ".github/dependabot.yml");
    Case("A1 负例：groups 块形态混用 ignore 词表必红（报行号）",
        m1.Count == 1 && m1[0].Contains("A1", StringComparison.Ordinal) && m1[0].Contains(":13", StringComparison.Ordinal));

    var badGroupsInline = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            groups:
              g:
                patterns:
                  - "*"
                update-types: [version-update:semver-minor]
        """;
    var m2 = ValidateDependabot(badGroupsInline, "d.yml");
    Case("A1 负例：groups 内联混用 ignore 词表必红",
        m2.Count == 1 && m2[0].Contains("A1", StringComparison.Ordinal));

    var badIgnoreVocab = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            ignore:
              - dependency-name: "X"
                update-types:
                  - patch
        """;
    var m3 = ValidateDependabot(badIgnoreVocab, "d.yml");
    Case("A2 负例：ignore 用 groups 裸词表必红",
        m3.Count == 1 && m3[0].Contains("A2", StringComparison.Ordinal));

    var badVersionsEmpty = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            ignore:
              - dependency-name: "X"
                versions: []
        """;
    var m4 = ValidateDependabot(badVersionsEmpty, "d.yml");
    Case("A3 负例：ignore versions 空列表必红",
        m4.Count == 1 && m4[0].Contains("A3", StringComparison.Ordinal) && m4[0].Contains(":10", StringComparison.Ordinal));

    var badVersionsBlank = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            ignore:
              - dependency-name: "X"
                versions: [""]
        """;
    var m5 = ValidateDependabot(badVersionsBlank, "d.yml");
    Case("A3 负例：ignore versions 空串项必红（含行级空串报告）",
        m5.Exists(x => x.Contains("A3", StringComparison.Ordinal) && x.Contains("空串", StringComparison.Ordinal)));

    var badIgnoreNoCriteria = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            ignore:
              - dependency-name: "X"
        """;
    var m6 = ValidateDependabot(badIgnoreNoCriteria, "d.yml");
    Case("A3 负例：ignore 条目既无 versions 也无 update-types 必红",
        m6.Count == 1 && m6[0].Contains("A3", StringComparison.Ordinal) && m6[0].Contains(":9", StringComparison.Ordinal));

    var badNoLabels = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
        """;
    var m7 = ValidateDependabot(badNoLabels, "d.yml");
    Case("A4 负例：entry 缺 labels 必红（报 entry 首行）",
        m7.Count == 1 && m7[0].Contains("A4", StringComparison.Ordinal) && m7[0].Contains(":3", StringComparison.Ordinal));

    var badEmptyLabels = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels: []
        """;
    var m8 = ValidateDependabot(badEmptyLabels, "d.yml");
    Case("A4 负例：labels 零项必红",
        m8.Count == 1 && m8[0].Contains("A4", StringComparison.Ordinal) && m8[0].Contains(":6", StringComparison.Ordinal));

    var badBlankLabelItem = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - ""
        """;
    var m8b = ValidateDependabot(badBlankLabelItem, "d.yml");
    Case("A4 负例：labels 空串项必红",
        m8b.Exists(x => x.Contains("A4", StringComparison.Ordinal)));

    var badPrefix = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            commit-message:
              prefix: ""
        """;
    var m9 = ValidateDependabot(badPrefix, "d.yml");
    Case("A5 负例：commit-message.prefix 空串必红（报行号）",
        m9.Count == 1 && m9[0].Contains("A5", StringComparison.Ordinal) && m9[0].Contains(":9", StringComparison.Ordinal));

    var badNoInterval = """
        version: 2
        updates:
          - package-ecosystem: nuget
            labels:
              - dependencies
        """;
    var m10 = ValidateDependabot(badNoInterval, "d.yml");
    Case("A6 负例：entry 缺 schedule.interval 必红（报 entry 首行）",
        m10.Count == 1 && m10[0].Contains("A6", StringComparison.Ordinal) && m10[0].Contains(":3", StringComparison.Ordinal));

    var badBlankInterval = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval:
            labels:
              - dependencies
        """;
    var m11 = ValidateDependabot(badBlankInterval, "d.yml");
    Case("A6 负例：schedule.interval 空值必红（报行号）",
        m11.Count == 1 && m11[0].Contains("A6", StringComparison.Ordinal) && m11[0].Contains(":5", StringComparison.Ordinal));

    var badNoUpdates = """
        # 只剩注释的配置
        version: 2
        """;
    var m12 = ValidateDependabot(badNoUpdates, "d.yml");
    Case("A7 负例：解析不出 updates entry 必红（fail-closed）",
        m12.Count == 1 && m12[0].Contains("A7", StringComparison.Ordinal));

    var badUtypesContext = """
        version: 2
        updates:
          - package-ecosystem: nuget
            schedule:
              interval: weekly
            labels:
              - dependencies
            update-types:
              - patch
        """;
    var m13 = ValidateDependabot(badUtypesContext, "d.yml");
    Case("附加负例：update-types 出现在 groups/ignore 之外必红（报取值行号）",
        m13.Count == 1 && m13[0].Contains("未知上下文", StringComparison.Ordinal) && m13[0].Contains(":9", StringComparison.Ordinal));

    // ── 检查 B 正例 ──
    var bGoodIgnoreExit = """
        <Project>
          <PropertyGroup>
          </PropertyGroup>
          <Target Name="T" BeforeTargets="Build">
            <Exec Command="git config --local --get core.hooksPath"
                  ConsoleToMSBuild="true"
                  IgnoreExitCode="true">
              <Output TaskParameter="ExitCode" PropertyName="X" />
            </Exec>
          </Target>
        </Project>
        """;
    Case("B 正例：IgnoreExitCode=\"true\"（多行属性）放行",
        ValidateDirectoryBuildExec(bGoodIgnoreExit, "Directory.Build.targets").Count == 0);

    var bGoodCiSkip = """
        <Project>
          <Target Name="ConfigureGitHooks"
                  Condition="'$(DesignTimeBuild)' != 'true' and '$(GITHUB_ACTIONS)' != 'true'">
            <Exec Command="git config --local core.hooksPath .githooks"
                  ContinueOnError="true" />
          </Target>
        </Project>
        """;
    Case("B 正例：Target 带 GITHUB_ACTIONS 跳过（ContinueOnError-only 放行）",
        ValidateDirectoryBuildExec(bGoodCiSkip, "Directory.Build.targets").Count == 0);

    var bGoodCiSkipTight = """
        <Project>
          <Target Name="T" Condition="'$(GITHUB_ACTIONS)'!='true'">
            <Exec Command="git x" />
          </Target>
        </Project>
        """;
    Case("B 正例：Condition 无空格变体归一后仍识别",
        ValidateDirectoryBuildExec(bGoodCiSkipTight, "Directory.Build.targets").Count == 0);

    var bGoodComment = """
        <Project>
          <!--
          <Exec Command="bad" />
          -->
        </Project>
        """;
    Case("B 正例：XML 注释内的 Exec 不计（等长遮蔽）",
        ValidateDirectoryBuildExec(bGoodComment, "Directory.Build.targets").Count == 0);

    Case("B 正例：无 Exec 的 props 放行",
        ValidateDirectoryBuildExec("<Project>\n  <PropertyGroup>\n  </PropertyGroup>\n</Project>\n", "Directory.Build.props").Count == 0);

    // ── 检查 B 负例 ──
    var bBadPlain = """
        <Project>
          <Target Name="T" BeforeTargets="Build">
            <Exec Command="git x" />
          </Target>
        </Project>
        """;
    var m20 = ValidateDirectoryBuildExec(bBadPlain, "Directory.Build.targets");
    Case("B 负例：无 IgnoreExitCode 且 Target 无 CI 跳过必红（报行号+缺失项）",
        m20.Count == 1 && m20[0].Contains("Directory.Build.targets:3", StringComparison.Ordinal)
            && m20[0].Contains("GITHUB_ACTIONS", StringComparison.Ordinal));

    var bBadNoTarget = """
        <Project>
          <Exec Command="bad" />
        </Project>
        """;
    var m21 = ValidateDirectoryBuildExec(bBadNoTarget, "Directory.Build.props");
    Case("B 负例：Target 外的 Exec 必红",
        m21.Count == 1 && m21[0].Contains("不在任何 <Target>", StringComparison.Ordinal));

    var bBadContinueOnError = """
        <Project>
          <Target Name="T">
            <Exec Command="bad" ContinueOnError="true" />
          </Target>
        </Project>
        """;
    var m22 = ValidateDirectoryBuildExec(bBadContinueOnError, "Directory.Build.targets");
    Case("B 负例：仅 ContinueOnError（warnaserror 事故形态）必红",
        m22.Count == 1 && m22[0].Contains("warnaserror", StringComparison.Ordinal));

    // ── 检查 C 正例 / 负例 ──
    var cGood = """
        jobs:
          build:
            steps:
              - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0
              - uses: ./local-action
              - uses: docker://alpine:3.20
              - uses: org/repo/.github/workflows/reusable.yml@eef61447b9ff4aafe5dcd4e0bbf5d482be7e7871 # v4.2.1
        """;
    Case("C 正例：SHA + 版本注释 / 本地动作 / docker 镜像 / 可复用工作流放行",
        ValidateWorkflowPins(cGood, "w.yml").Count == 0);

    var cBadTag = "      - uses: actions/checkout@v4 # v4.4.0\n";
    var c1 = ValidateWorkflowPins(cBadTag, ".github/workflows/ci.yml");
    Case("C1 负例：可变 major 标签必红（报行号）",
        c1.Count == 1 && c1[0].Contains("C1", StringComparison.Ordinal) && c1[0].Contains(":1", StringComparison.Ordinal));

    var cBadShortSha = "      - uses: actions/checkout@11d5960 # v4.4.0\n";
    Case("C1 负例：短 SHA 必红",
        ValidateWorkflowPins(cBadShortSha, "w.yml").Exists(v => v.Contains("C1", StringComparison.Ordinal)));

    var cBadNoComment = "      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262\n";
    var c2 = ValidateWorkflowPins(cBadNoComment, "w.yml");
    Case("C2 负例：缺版本注释必红",
        c2.Count == 1 && c2[0].Contains("C2", StringComparison.Ordinal));

    var cBadNoRef = "      - uses: actions/checkout\n";
    Case("C1 负例：缺 @ref 必红",
        ValidateWorkflowPins(cBadNoRef, "w.yml").Exists(v => v.Contains("C1", StringComparison.Ordinal)));

    // ── 检查 D 正例 / 负例 ──
    var dGood = """
        pywin32==311 ; sys_platform == "win32" \
            --hash=sha256:abc
        colorama==0.4.6 \
            --hash=sha256:def
        """;
    Case("D 正例：Windows 专用依赖带 marker + 通用依赖放行",
        ValidateCiRequirements(dGood, "ci/requirements-x.txt").Count == 0);

    var dGoodPlatformSystem = "pywin32==311 ; platform_system == \"Windows\"\n";
    Case("D 正例：platform_system 等价形态放行",
        ValidateCiRequirements(dGoodPlatformSystem, "r.txt").Count == 0);

    var dBad = """
        pywin32==311 \
            --hash=sha256:abc
        """;
    var d1 = ValidateCiRequirements(dBad, "ci/requirements-semgrep.txt");
    Case("D 负例：Windows 专用依赖无 marker 必红（报行号）",
        d1.Count == 1 && d1[0].Contains(":1", StringComparison.Ordinal) && d1[0].Contains("pywin32", StringComparison.Ordinal));

    Case("D 负例：wincertstore 无 marker 必红",
        ValidateCiRequirements("wincertstore==0.2\n", "r.txt").Count == 1);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 2;   // 退出码 2 = 自测失败
}
