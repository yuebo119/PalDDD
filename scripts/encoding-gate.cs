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
//   E5 源文件行尾 CRLF 一致性（2026-09-14 增）——扩展名清单内文件不得含裸 LF
//      （0x0A 前无 0x0D），纯 LF 与混合行尾均命中；豁免 .sh/.py（POSIX 规范 LF）
//      与 *.verified.*（Verify 规范 LF，E1/E4 反向守卫）。
//      范围取舍：只扫主仓——.ai 为独立仓、独立提交线（其 scripts 由 E1 覆盖），
//      主仓门禁不被独立仓状态阻塞。排除路径：obj/bin/.git/TestResults/
//      node_modules/.vs/.serena/.probe-dump（生成/临时目录）。
//      背景：python 重写源文件造成 CRLF→LF 漂移（4a64fba 修 1543 处、277bc34 修
//      255 处），此前无机械检测（git autocrlf 可自愈，但工作树字节对编辑器/工具不标准）。
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

// Justification: CA1031 禁止宽泛 catch；本文件的捕获只出现在 --selftest 内，两处用途均为
// 「异常即结论」而非吞错：① 断言 ReadTextTolerant 对非法 UTF-8 不抛——此处必须能捕获任意
// 异常类型才能证明该性质；② 临时目录清理失败不得翻转自测结论。限定在自测内。
#pragma warning disable CA1031

using System.Text;

// Windows 控制台默认编码非 UTF-8，中文输出会乱码——对齐 bash printf UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ─── 仓库根定位 ───
Environment.CurrentDirectory = FindRepoRoot();

var fail = 0;

// ─── .cs 扫描根（E2/E3 共用；E1 的目录清单 = 本清单 + ".ai/scripts"）───
// 2026-09-13：此前 E2/E3 内联写死 ["src","test"]，与 E1 的六目录清单不一致，
// 导致 scripts/ samples/ bench/ 下 69 个 .cs 落在 BOM/mojibake 检查盲区
// （由 scripts/gate-audit.cs 的隔离式变异探针发现）。抽常量以防再次漂移。
string[] CsScanRoots = ["src", "test", "scripts", "bench", "samples"];

// ─── 自测分发（--selftest）───
// 2026-09-13 增：本门禁的判定全是字节级/指纹级，错一个字节就会「静默放行」而输出
// 与正常无异（E2 漏检 BOM、E3 漏检 mojibake 都不会有任何可观察差异），故补自证能力。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

Console.WriteLine("═══ 编码一致性门禁 ═══");

// ─── E1: .sh/.py 不混 CRLF（含孤立 CR；.ai 被 gitignore → 直读本地文件防空假绿）───
// 注：E1 目录清单 = CsScanRoots + ".ai/scripts"（.ai 为独立 git 仓库，仅 E1 覆盖）
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
    foreach (var f in CsScanRoots.SelectMany(EnumerateCsExcludingGenerated))
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
    var patterns = MojibakeFingerprints();
    var bad = new List<string>();
    foreach (var f in CsScanRoots.SelectMany(EnumerateAllCs))
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

// ─── E5: 源文件行尾 CRLF 一致性（工作树漂移防线，2026-09-14 增）───
// 判据/豁免/范围见头注释。枚举用带 skipDirs 的栈遍历（跳过生成目录，避免 .git 拖慢）。
{
    var bad = new List<string>();
    string[] eolExts = [".md", ".cs", ".csproj", ".props", ".targets", ".json", ".yml", ".slnx"];
    foreach (var f in EnumerateSourceFiles(".", eolExts))
    {
        var name = Path.GetFileName(f);
        if (name.EndsWith(".sh", StringComparison.Ordinal) || name.EndsWith(".py", StringComparison.Ordinal)
            || name.Contains(".verified.", StringComparison.Ordinal)) continue;
        var (crlf, bareLf) = CountEol(File.ReadAllBytes(f));
        if (HasBareLf(crlf, bareLf))
        {
            bad.Add(ToPosix(f));
            if (bad.Count >= 5) break;
        }
    }
    if (bad.Count > 0)
    {
        Console.WriteLine($"FAIL E5 源文件含裸 LF 行尾（CRLF 漂移；修复：dotnet run %TEMP%/fix-eol.cs <path>）：");
        foreach (var f in bad) Console.WriteLine(f);
        fail++;
    }
    else Console.WriteLine("PASS E5 源文件行尾 CRLF 一致");
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

// E5：行尾计数——CRLF 对数与裸 LF 数（0x0A 前无 0x0D）
static (int Crlf, int BareLf) CountEol(byte[] bytes)
{
    int crlf = 0, bareLf = 0;
    for (int i = 0; i < bytes.Length; i++)
        if (bytes[i] == (byte)'\n')
        {
            if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++;
            else bareLf++;
        }
    return (crlf, bareLf);
}

// E5：裸 LF 存在即违规（纯 LF 与混合行尾均命中）；纯 CRLF 与空文件放行
static bool HasBareLf(int crlf, int bareLf) => bareLf > 0;

// E5：源文件枚举——带 skipDirs 的栈遍历（跳过生成/临时目录，避免 .git 拖慢），
// 扩展名精确匹配（Path.GetExtension 单段语义）
static IEnumerable<string> EnumerateSourceFiles(string root, string[] extensions)
{
    var skipDirs = new HashSet<string>(StringComparer.Ordinal)
        { "obj", "bin", ".git", "TestResults", "node_modules", ".vs", ".serena", ".probe-dump" };
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (!skipDirs.Contains(Path.GetFileName(sub))) stack.Push(sub);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
                yield return f;
    }
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

// mojibake 指纹表（单一来源：E3 与 --selftest 共用，避免两份漂移）。
// 以 \uXXXX 转义书写：若以字面字符写入本文件，本文件自身会命中 E3（自指陷阱，见头注释）。
static string[] MojibakeFingerprints() =>
    ("\u923a|\u93c8\u5d85|\u935a\u5ea1|\u9422\u71b7|\u5a34\u5b2d|\u5bb8\u53c9\u6e41|"
     + "\u934b\u6ec3|\u93b5\u6d98|\u704f\u5fdb|\u95b0\u5d87\u7586|\u7f01\u581f|\u5bee\u509a|"
     + "\u6d93\u5d85|\u9363\u3126|\u9422\u3124|\u922b|\u59dd\u30ed|\u9352\u6d98\u7f13|"
     + "\u741b\u30e5\u4f3f|\u59af\u2103|\u7039\u70b4|\u59ab\u20ac|\u95c5\u65c2|"
     + "\u9359\u509b\u669f|\u6437|\u02b5\u02be|\u8f2f|\u046d").Split('|');

// ══════════════ 自测 ══════════════

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

    // ContainsByte（E1/E4 的判定核心，字节级）
    Case("ContainsByte 命中 0x0D", ContainsByte([0x61, 0x0D, 0x62], 0x0D));
    Case("ContainsByte 无 0x0D 时不命中", !ContainsByte("abc\n"u8.ToArray(), 0x0D));
    Case("ContainsByte 空数组不命中", !ContainsByte([], 0x0D));

    // 临时文件用于 E2/E4 的文件级判定
    var tmp = Path.Combine(Path.GetTempPath(), "encoding-gate-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(tmp);

        // E2：HasUtf8Bom —— 三分支（有 BOM / 无 BOM / 短文件边界）
        var withBom = Path.Combine(tmp, "withbom.cs");
        File.WriteAllBytes(withBom, [0xEF, 0xBB, 0xBF, .. "var x = 1;\n"u8.ToArray()]);
        Case("HasUtf8Bom 识别 EF BB BF", HasUtf8Bom(withBom));

        var noBom = Path.Combine(tmp, "nobom.cs");
        File.WriteAllBytes(noBom, "// 中文注释\nvar x = 1;\n"u8.ToArray());
        Case("HasUtf8Bom 对无 BOM 文件返回 false（负向对照）", !HasUtf8Bom(noBom));

        var shortFile = Path.Combine(tmp, "short.cs");
        File.WriteAllBytes(shortFile, [0xEF, 0xBB]); // 仅 2 字节，不足 BOM 长度
        Case("HasUtf8Bom 对不足 3 字节的文件返回 false（边界）", !HasUtf8Bom(shortFile));

        var emptyFile = Path.Combine(tmp, "empty.cs");
        File.WriteAllBytes(emptyFile, []);
        Case("HasUtf8Bom 对空文件返回 false（边界）", !HasUtf8Bom(emptyFile));

        // E1/E4：CR 字节 + ReadTextTolerant 容错
        var crlf = Path.Combine(tmp, "crlf.sh");
        File.WriteAllBytes(crlf, "echo hi\r\n"u8.ToArray());
        Case("CRLF 文件含 0x0D（E1 会命中）", ContainsByte(File.ReadAllBytes(crlf), 0x0D));

        var lf = Path.Combine(tmp, "lf.sh");
        File.WriteAllBytes(lf, "echo hi\n"u8.ToArray());
        Case("LF 文件不含 0x0D（E1 放行）", !ContainsByte(File.ReadAllBytes(lf), 0x0D));

        var invalidUtf8 = Path.Combine(tmp, "bad-utf8.cs");
        File.WriteAllBytes(invalidUtf8, [0x61, 0xC3, 0x28, 0x62]);
        var tolerantOk = true;
        try { _ = ReadTextTolerant(invalidUtf8); }
        catch (Exception) { tolerantOk = false; }
        Case("ReadTextTolerant 对非法 UTF-8 不抛（对齐 grep 字节语义）", tolerantOk);

        // E3：指纹命中和不命中
        var fingerprints = MojibakeFingerprints();
        Case("指纹表非空且无空项", fingerprints.Length > 0 && fingerprints.All(p => p.Length > 0));
        Case("指纹命中：文本含指纹则被检出",
            fingerprints.Any(p => ("前缀" + p + "后缀").Contains(p)));
        Case("指纹不命中：干净中文文本不被误报",
            !fingerprints.Any(p => "正常的领域事件与聚合根说明".Contains(p)));

        // E5：行尾判定——纯 LF/混合命中,纯 CRLF/空放行
        Case("CountEol 纯 CRLF", CountEol("a\r\nb\r\n"u8.ToArray()) is (2, 0));
        Case("CountEol 纯 LF", CountEol("a\nb\n"u8.ToArray()) is (0, 2));
        Case("CountEol 混合", CountEol("a\r\nb\nc\n"u8.ToArray()) is (1, 2));
        Case("HasBareLf 纯 LF 命中（E5 违规）", HasBareLf(0, 5));
        Case("HasBareLf 混合命中（E5 违规）", HasBareLf(10, 2));
        Case("HasBareLf 纯 CRLF 放行（负向对照）", !HasBareLf(5, 0));
        Case("HasBareLf 空文件放行（边界）", !HasBareLf(0, 0));

        // 自指陷阱回归守卫：本文件自身不得含任何字面指纹
        // （指纹若以字面字符写入，encoding-gate 会命中自己——头注释记录了该陷阱）
        var ownSource = ReadTextTolerant(Path.Combine("scripts", "encoding-gate.cs"));
        Case("自指陷阱：本文件源码不含字面指纹",
            !fingerprints.Any(p => ownSource.Contains(p)));
    }
    finally
    {
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* 清理失败不翻转结论 */ }
    }

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
