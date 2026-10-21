using System.Globalization;
using System.Text.RegularExpressions;

namespace PalDDD.Integration.Tests;

/// <summary>
/// T4（基础库优化清单）：内部轮次注释棘轮。以基线文件为上限，新增 `vN PM` 注释即失败；
/// 存量按触碰文件逐步替换后调低基线。全仓 729 处一次性替换会丢失上下文，本测试只防新增。
/// </summary>
public sealed class CommentRatchetTests
{
    [Test]
    public async Task RoundNumberAnnotations_DoNotIncrease()
    {
        var root = FindRepoRoot();
        var baseline = int.Parse(
            File.ReadAllText(Path.Combine(root, "test", "PalDDD.Integration.Tests", "comment-round-baseline.txt"))
                .Trim(),
            CultureInfo.InvariantCulture);

        var regex = new Regex(@"v\d+ P\d", RegexOptions.CultureInvariant);
        var count = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var matches = regex.Count(line);
                if (matches == 0)
                    continue;
                count += matches;
                if (count > baseline)
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNumber}");
            }
        }

        await Assert.That(count)
            .IsLessThanOrEqualTo(baseline)
            .Because($"新增内部轮次注释会积累无索引的历史引用；替换存量后调低 comment-round-baseline.txt。新增点：{string.Join(", ", offenders.Take(10))}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
