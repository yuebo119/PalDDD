// ============================================================================
// osc-check.cs——OSC 翻转检测器（MIG-011c，2026-09-11）
// 由 .ai/scripts/test-gate.sh 内嵌 82 行 python 等价迁移为 C#（dotnet file-based app）。
//
// 用法：dotnet run scripts/osc-check.cs -- <state.json 路径> <报告 glob> [--selftest]
//   state.json 路径：OSC 状态文件（.ai/gate/oscillation-state.json，gitignore；
//                    每测试保留最近 3 次观测 + 报告指纹防重复计数）
//   报告 glob：      TestResults/*.tunit-report.json（引号包裹字面传入，本程序自行展开）
//   --selftest：     合成红测（ABA 必须唯一检出；AAA 与 AEA 不得误报）
//
// 输出与退出码与原 python 版逐行一致（test-gate.sh 依赖此格式）：
//   SKIP  OSC  无 TestResults/*.tunit-report.json（先跑测试再检测）   → exit 0
//   SKIP  OSC  报告指纹未变（无新观测，不重复计数）                 → exit 0
//   PASS  OSC  无翻转（观测 N 测试，环境性失败已隔离）               → exit 0
//   FAIL  OSC  <测试键>: A→B→A（翻转两次=打摆，换方案不加力度）      → exit 1
//   PASS/FAIL OSC-SELFTEST ...                                      → exit 0/1
//
// 迁移说明：
//   1) 指纹值由 python 的 float 秒改为毫秒整数——python 时代旧 state 首跑必然
//      指纹不等（等同"报告变了"），重观测一次无害；此后 C# 自洽可正确去重。
//   2) 环境性失败口径不变（T-DDD-6）：exception 的 type+message 命中 ENV_PAT
//      → 'E'，不参与翻转判定；真失败 → 'F'；passed → 'P'。
//   3) 参数化用例聚合口径不变：同 className.methodName 任一 case 真失败 → F，
//      有环境性失败且无真失败 → E，否则 P。
//   4) 零 package 依赖——System.Text.Json 框架内自动可用，不写 #:package
//      （NU1510 即错误）；写盘用 Utf8JsonWriter（无反射，AOT 分析器友好）。
// ============================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

// Windows 控制台默认编码非 UTF-8，中文/箭头字符经 bash 捕获会乱码——对齐 python UTF-8
Console.OutputEncoding = Encoding.UTF8;

// 环境性失败模式（与原 python ENV_PAT 逐项一致；大小写不敏感子串匹配）
var envPat = new[]
{
    "Npgsql", "MySql", "Socket", "Connection refused", "connect timeout",
    "9092", "broker", "No such host", "connection closed",
};

// UTF-8 严格解码（无效字节抛异常）+ 保留 BOM 字节：与 python open(encoding='utf-8')
// 的 strict 语义对齐——BOM/坏编码使 JSON 解析失败 → 跳过该报告（python 同路径）
var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

// ─── JSON 元素取值的 python 语义模拟 ───
// python str(None) == 'None'、get 缺省 ''；仅自测失败行与异常消息格式依赖此细节
string PyStr(JsonElement v) => v.ValueKind switch
{
    JsonValueKind.String => v.GetString()!,
    JsonValueKind.Null => "None",
    _ => v.ToString(),
};

string StrOr(JsonElement obj, string name, string dflt)
{
    if (!obj.TryGetProperty(name, out var v)) return dflt;
    return PyStr(v);
}

// ─── classify：passed → P；异常消息命中 ENV_PAT → E；否则 F ───
char Classify(JsonElement t)
{
    if (t.TryGetProperty("status", out var st)
        && st.ValueKind == JsonValueKind.String && st.GetString() == "passed")
        return 'P';
    var msg = "";
    // python: ex = t.get('exception') or {}（缺失/null/空对象 → 无异常消息）
    if (t.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.Object)
    {
        var ty = ex.TryGetProperty("type", out var x1) ? PyStr(x1) : "";
        var ms = ex.TryGetProperty("message", out var x2) ? PyStr(x2) : "";
        msg = ty + " " + ms;
    }
    // ENV_PAT 全 ASCII：OrdinalIgnoreCase 子串匹配与 python lower() 双侧小写等价
    foreach (var p in envPat)
        if (msg.Contains(p, StringComparison.OrdinalIgnoreCase)) return 'E';
    return 'F';
}

// ─── observe：追加观测并检测 ABA（同测试状态翻转两次且无 E 参与）───
List<string> Observe(Dictionary<string, List<string>> hist, Dictionary<string, char> cur)
{
    var flags = new List<string>();
    foreach (var (k, s) in cur)   // Dictionary 保插入序 = 报告处理序，与 python dict 一致
    {
        hist.TryGetValue(k, out var h);
        h ??= new List<string>();
        h.Add(s.ToString());
        if (h.Count > 3) h.RemoveRange(0, h.Count - 3);   // 每测试仅保留最近 3 次观测
        hist[k] = h;
        if (h.Count == 3 && h[0] == h[2] && h[0] != h[1] && !h.Contains("E"))
            flags.Add($"{k}: {h[0]}→{h[1]}→{h[2]}（翻转两次=打摆，换方案不加力度）");
    }
    return flags;
}

// python list repr 模拟（仅 --selftest 失败行使用：['a', 'b']）
string PyListRepr(List<string> xs) => "[" + string.Join(", ", xs.Select(x => $"'{x}'")) + "]";

// ─── 参数解析：state 路径 + 报告 glob + 可选 --selftest ───
string statePath = "", reportGlob = "";
bool selftest = false;
foreach (var a in args)
{
    if (a == "--selftest") selftest = true;
    else if (string.IsNullOrEmpty(statePath)) statePath = a;
    else if (string.IsNullOrEmpty(reportGlob)) reportGlob = a;
}
if (!selftest && (string.IsNullOrEmpty(statePath) || string.IsNullOrEmpty(reportGlob)))
{
    Console.Error.WriteLine("用法: dotnet run osc-check.cs -- <state.json 路径> <报告 glob> [--selftest]");
    return 2;
}

// ─── 自测模式：合成 ABA/AAA/AEA 三序列，ABA 必须唯一检出 ───
if (selftest)
{
    var hist = new Dictionary<string, List<string>>
    {
        ["T1"] = new() { "P", "F" },   // ABA → 必须检出
        ["T2"] = new() { "P", "P" },   // AAA → 不得检出
        ["T3"] = new() { "P", "E" },   // AEA → 环境性失败隔离，不得检出
    };
    var cur = new Dictionary<string, char> { ["T1"] = 'P', ["T2"] = 'P', ["T3"] = 'P' };
    var flags = Observe(hist, cur);
    var ok = flags.Count == 1 && flags[0] == "T1: P→F→P（翻转两次=打摆，换方案不加力度）";
    Console.WriteLine(ok
        ? $"PASS OSC-SELFTEST  ABA 检出且 AAA/PEP 不误报"
        : $"FAIL OSC-SELFTEST  检出异常: {PyListRepr(flags)}");
    return ok ? 0 : 1;
}

// ─── 报告 glob 展开（目录 + 单层 * 通配；python glob 语义：* 匹配任意字符含点）───
List<string> ExpandGlob(string glob)
{
    var dir = Path.GetDirectoryName(glob) ?? ".";
    var pat = Path.GetFileName(glob);
    if (string.IsNullOrEmpty(pat) || !Directory.Exists(dir)) return new List<string>();
    var rx = new System.Text.RegularExpressions.Regex(
        "^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace(@"\*", ".*") + "$");
    return Directory.EnumerateFiles(dir)
        .Select(f => Path.GetFileName(f) ?? "")
        .Where(n => n.Length > 0 && rx.IsMatch(n))
        .Select(n => Path.Combine(dir, n))
        .OrderBy(p => p, StringComparer.Ordinal)   // python sorted()：code point 序
        .ToList();
}

var reports = ExpandGlob(reportGlob);
if (reports.Count == 0)
{
    // Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的
    // 固定协议行（test-gate.sh 依赖逐字符匹配），无本地化需求——沿 vuln-scan.cs 先例
#pragma warning disable CA1303
    Console.WriteLine("SKIP  OSC  无 TestResults/*.tunit-report.json（先跑测试再检测）");
#pragma warning restore CA1303
    return 0;
}

// 指纹 = {文件名: 修改时间毫秒}（python 版为 float 秒——见头注释迁移说明 1）
var fps = new Dictionary<string, double>();
foreach (var r in reports)
    fps[Path.GetFileName(r)] = new DateTimeOffset(File.GetLastWriteTimeUtc(r)).ToUnixTimeMilliseconds();

// ─── 加载历史 state（缺失/损坏 → 空 state，与 python try/except 一致）───
(Dictionary<string, List<string>> hist, Dictionary<string, double> fp) LoadState(string path)
{
    var hist = new Dictionary<string, List<string>>();
    var fp = new Dictionary<string, double>();
    try
    {
        using var doc = JsonDocument.Parse(utf8Strict.GetString(File.ReadAllBytes(path)));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("hist", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var kv in h.EnumerateObject())
                {
                    var list = new List<string>();
                    if (kv.Value.ValueKind == JsonValueKind.Array)
                        foreach (var v in kv.Value.EnumerateArray())
                            list.Add(v.ValueKind == JsonValueKind.String ? v.GetString()! : PyStr(v));
                    hist[kv.Name] = list;
                }
            if (root.TryGetProperty("fp", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var kv in f.EnumerateObject())
                    if (kv.Value.TryGetDouble(out var d)) fp[kv.Name] = d;
        }
    }
    catch (IOException) { /* 缺失/不可读 → 静默重建（python except 同） */ }
    catch (UnauthorizedAccessException) { }
    catch (JsonException) { /* 坏 JSON → 静默重建 */ }
    catch (DecoderFallbackException) { /* 坏编码 → 静默重建 */ }
    return (hist, fp);
}

var (histState, fpState) = LoadState(statePath);
// 指纹去重：键集合与数值全等才视为"无新观测"（python dict == dict）
if (fpState.Count == fps.Count && fpState.All(kv => fps.TryGetValue(kv.Key, out var v) && v == kv.Value))
{
#pragma warning disable CA1303   // 固定协议行，无本地化需求（同上 Justification）
    Console.WriteLine("SKIP  OSC  报告指纹未变（无新观测，不重复计数）");
#pragma warning restore CA1303
    return 0;
}

// ─── 聚合本轮观测（参数化用例聚合：任一 F → F；有 E 无 F → E；否则 P）───
var curObs = new Dictionary<string, char>();
foreach (var r in reports)
{
    JsonDocument doc;
    try { doc = JsonDocument.Parse(utf8Strict.GetString(File.ReadAllBytes(r))); }
    // 坏 JSON/坏编码报告跳过（python 同路径；TestResults 留有此容错红测样本 MIG011-Broken）
    catch (JsonException) { continue; }
    catch (IOException) { continue; }
    catch (DecoderFallbackException) { continue; }
    using (doc)
    {
        if (!doc.RootElement.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array) continue;
        foreach (var g in groups.EnumerateArray())
        {
            if (!g.TryGetProperty("tests", out var tests)
                || tests.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in tests.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var key = StrOr(t, "className", "?") + "." + StrOr(t, "methodName", "?");
                var s = Classify(t);
                var prev = curObs.TryGetValue(key, out var p) ? p : 'P';
                if (s == 'F' || prev == 'F') curObs[key] = 'F';
                else if (s == 'E' || prev == 'E') curObs[key] = 'E';
                else curObs[key] = 'P';
            }
        }
    }
}

var newFlags = Observe(histState, curObs);

// ─── 写回 state：Utf8JsonWriter 手工序列化（无反射，AOT 分析器友好）───
// Encoder=UnsafeRelaxedJsonEscaping 等价 python ensure_ascii=False：中文/箭头不转义
var dirOfState = Path.GetDirectoryName(Path.GetFullPath(statePath));
if (!string.IsNullOrEmpty(dirOfState)) Directory.CreateDirectory(dirOfState);
using (var stream = new MemoryStream())
{
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
    {
        writer.WriteStartObject();
        writer.WritePropertyName("hist");
        writer.WriteStartObject();
        foreach (var kv in histState)
        {
            writer.WritePropertyName(kv.Key);
            writer.WriteStartArray();
            foreach (var obs in kv.Value) writer.WriteStringValue(obs);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WritePropertyName("fp");
        writer.WriteStartObject();
        foreach (var kv in fps)
        {
            writer.WritePropertyName(kv.Key);
            writer.WriteNumberValue(kv.Value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    File.WriteAllBytes(statePath, stream.ToArray());   // UTF-8 无 BOM
}

if (newFlags.Count > 0)
{
    foreach (var f in newFlags) Console.WriteLine($"FAIL  OSC  {f}");
    return 1;
}
Console.WriteLine($"PASS  OSC  无翻转（观测 {curObs.Count} 测试，环境性失败已隔离）");
return 0;
