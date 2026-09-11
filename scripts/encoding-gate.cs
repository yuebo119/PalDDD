// ============================================================================
// encoding-gate.cs——编码一致性门禁（MIG-012-A2，2026-09-11）
// 由 .ai/scripts/encoding-gate.sh（68 行）等价迁移为 C#（dotnet file-based app）。
//
// 用法：dotnet run scripts/encoding-gate.cs
// 退出码：0=通过；1=有违规；2=仓库根定位失败。
//
// 检查项（与原 bash 逐项对应，字节级检查直读字节）：
//   E1 .sh/.py 文件不含 CR 字节（0x0D，含 CRLF 即命中；CI Linux bash 必死）
//   E2 .cs 文件无 UTF-8 BOM（头 3 字节 EF BB BF；排除 *.g.cs 与 obj/bin）
//   E3 .cs 文件无 mojibake 指纹（UTF-8→GBK 双重编码产物字符）
//   E4 .verified.* 文件纯 LF（含 CR 即命中；Verify Linux 拒绝）
//
// 迁移说明：
//   1) E1/E4 与 bash `od -c | grep '\\r'` 同语义：文件内任意 0x0D 字节即违规
//      （不限于 0D0A 对——孤立 CR 同样是编码污染）。
//   2) E3 指纹字符全部以 \uXXXX 转义书写：若以字面字符写入本 .cs 源码，
//      本文件自身会命中 E3 检查（指纹是 grep 模式本体）——自指陷阱。
//   3) 仓库根定位同 verify-ai.cs：CWD 向上找 PalDDD.slnx 为主、
//      BaseDirectory 兜底（file-based app 的 BaseDirectory 实测指向
//      %TEMP%\dotnet\runfile\...，不可达仓库根）。
//   4) 原版输出带 ANSI 颜色码（bash printf 变量拼接）；本版不输出颜色码，
//      文本逐行一致（双跑归一颜色码后 diff 验证），PASS/FAIL 关键字 grep 不受影响。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的
// 固定协议行（PASS E* / FAIL E* 被 grep 消费），固定中文非用户可配文案——沿
// vuln-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Text;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 bash printf UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ─── 仓库根定位 ───
Environment.CurrentDirectory = FindRepoRoot();

var fail = 0;

Console.WriteLine("═══ 编码一致性门禁 ═══");

// ─── E1: .sh/.py 不混 CRLF（含孤立 CR；.ai 被 gitignore → 直读本地文件防空假绿）───
{
    var bad = new List<string>();
    foreach (var dir in (string[])["src", "test", "scripts", ".ai/scripts", "bench", "samples"])
        foreach (var f in EnumerateByExtension(dir, [ ".sh", ".py" ]))
            if (ContainsByte(File.ReadAllBytes(f), 0x0D))
            {
                bad.Add(ToPosix(f));
                if (bad.Count >= 5) break;
            }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL E1 脚本混 CRLF（CI Linux bash 必死）：");
        foreach (var f in bad) Console.WriteLine(f);
        fail++;
    }
    else Console.WriteLine("PASS E1 脚本无 CRLF");
}

// ─── E2: .cs 无 BOM（排除 *.g.cs——源生成器产物可能合法带 BOM）───
{
    var bad = new List<string>();
    foreach (var f in EnumerateCsExcludingGenerated("src").Concat(EnumerateCsExcludingGenerated("test")))
        if (HasUtf8Bom(f))
        {
            bad.Add(ToPosix(f));
            if (bad.Count >= 5) break;
        }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL E2 .cs 含 UTF-8 BOM：");
        foreach (var f in bad) Console.WriteLine(f);
        fail++;
    }
    else Console.WriteLine("PASS E2 .cs 无 BOM");
}

// ─── E3: .cs 无 mojibake 指纹（UTF-8→GBK 双重编码常见产物）───
// 指纹为三十六轮 P1-3 扩充的本仓实测产物全集；\uXXXX 转义原因见头注释迁移说明 2
{
    var moji = "\u923a|\u93c8\u5d85|\u935a\u5ea1|\u9422\u71b7|\u5a34\u5b2d|\u5bb8\u53c9\u6e41|"
        + "\u934b\u6ec3|\u93b5\u6d98|\u704f\u5fdb|\u95b0\u5d87\u7586|\u7f01\u581f|\u5bee\u509a|"
        + "\u6d93\u5d85|\u9363\u3126|\u9422\u3124|\u922b|\u59dd\u30ed|\u9352\u6d98\u7f13|"
        + "\u741b\u30e5\u4f3f|\u59af\u2103|\u7039\u70b4|\u59ab\u20ac|\u95c5\u65c2|"
        + "\u9359\u509b\u669f|\u6437|\u02b5\u02be|\u8f2f|\u046d";
    var patterns = moji.Split('|');
    var bad = new List<string>();
    foreach (var f in EnumerateAllCs("src").Concat(EnumerateAllCs("test")))
    {
        // 路径含 obj/bin 子串排除（grep -v obj | grep -v bin 的子串语义）
        var posix = ToPosix(f);
        if (posix.Contains("obj") || posix.Contains("bin")) continue;
        var text = ReadTextTolerant(f);
        if (patterns.Any(p => text.Contains(p)))
        {
            bad.Add(posix);
            if (bad.Count >= 5) break;
        }
    }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL E3 .cs 含 mojibake 指纹（UTF-8→GBK 双重编码产物）：");
        foreach (var f in bad) Console.WriteLine(f);
        fail++;
    }
    else Console.WriteLine("PASS E3 .cs 无 mojibake");
}

// ─── E4: .verified.* 行尾 LF ───
{
    var bad = new List<string>();
    foreach (var f in EnumerateByExtension("test", [ ".verified.txt", ".verified.json" ]))
        if (ContainsByte(File.ReadAllBytes(f), 0x0D))
        {
            bad.Add(ToPosix(f));
            if (bad.Count >= 5) break;
        }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL E4 .verified.* 含 CR（Verify Linux 拒绝）：");
        foreach (var f in bad) Console.WriteLine(f);
        fail++;
    }
    else Console.WriteLine("PASS E4 .verified.* 纯 LF");
}

Console.WriteLine($"═══ 结果：{fail} 失败 ═══");
return fail == 0 ? 0 : 1;

// ══════════════ static 局部函数 ══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app
// 的 BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）
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

// 字节包含检测（等价 od -c | grep '\r' 的字节级语义）
static bool ContainsByte(byte[] bytes, byte target)
{
    foreach (var b in bytes) if (b == target) return true;
    return false;
}

// UTF-8 BOM 检测（头 3 字节 EF BB BF）
static bool HasUtf8Bom(string path)
{
    using var fs = File.OpenRead(path);
    Span<byte> head = stackalloc byte[3];
    var read = fs.Read(head);
    return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
}

// 递归收集指定扩展名文件（find -name 语义；目录缺失跳过 = bash stderr 吞掉）
static IEnumerable<string> EnumerateByExtension(string root, string[] extensions) =>
    Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
        : [];

// .cs 全量（含 obj/bin——E3 的排除在调用侧按路径子串过滤，对齐 grep -v 语义）
static IEnumerable<string> EnumerateAllCs(string root) =>
    Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories) : [];

// .cs 排除 *.g.cs 与 obj/bin 路径（find ! -name ! -path 语义）
static IEnumerable<string> EnumerateCsExcludingGenerated(string root) =>
    EnumerateAllCs(root)
        .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal))
        .Where(f => !ToPosix(f).Contains("/obj/") && !ToPosix(f).Contains("/bin/"));

// 容错 UTF-8 读（等价 grep 按字节匹配：指纹字符本身有效 UTF-8，
// 坏编码文件解码后不会凑出指纹字符，命中行为一致）
static string ReadTextTolerant(string path) =>
    File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));

// 路径转 posix 正斜杠（对齐 bash find/grep 输出形式）
static string ToPosix(string path) => path.Replace('\\', '/');
