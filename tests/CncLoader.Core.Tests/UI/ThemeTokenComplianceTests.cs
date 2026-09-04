using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// 主题令牌合规：色值只能在 Tokens.xaml 里出现一次，其他 XAML 一律引用令牌。
/// 否则改一次配色要满仓库找字面量，且同一语义色会漂成好几个近似值。
/// </summary>
[TestFixture]
public sealed class ThemeTokenComplianceTests
{
    /// <summary>结构性透明与全黑/全白遮罩不属于语义色，不强制令牌化。</summary>
    private static readonly string[] AllowedLiterals = { "Transparent", "Black", "White" };

    private static readonly Regex HexColor = new(@"#[0-9A-Fa-f]{6,8}\b", RegexOptions.Compiled);

    [Test]
    public void OnlyTokensFile_DeclaresRawColorValues()
    {
        var uiRoot = LocateUiProjectRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "Tokens.xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!HexColor.IsMatch(lines[i])) continue;
                offenders.Add($"{Path.GetRelativePath(uiRoot, file)}:{i + 1} {lines[i].Trim()}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "色值字面量只允许出现在 Tokens.xaml；其余位置请引用令牌画刷：\n" + string.Join('\n', offenders));
    }

    [Test]
    public void NamedColorLiterals_AreLimitedToStructuralOnes()
    {
        var uiRoot = LocateUiProjectRoot();
        // 属性名须整词匹配，否则 LastChildFill="True" 这类会被误判成颜色
        var named = new Regex(
            @"(?<![A-Za-z])(?:Background|Foreground|BorderBrush|Fill|Stroke)=""(?<v>[A-Za-z]+)""",
            RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in named.Matches(lines[i]))
                {
                    var value = m.Groups["v"].Value;
                    if (AllowedLiterals.Contains(value)) continue;
                    offenders.Add($"{Path.GetRelativePath(uiRoot, file)}:{i + 1} {value}");
                }
            }
        }

        Assert.That(offenders, Is.Empty,
            "具名颜色仅允许 Transparent/Black/White（结构性）：\n" + string.Join('\n', offenders));
    }

    /// <summary>从测试输出目录回溯到 src/CncLoader.UI（不依赖工作目录）。</summary>
    private static string LocateUiProjectRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CncLoader.UI");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未找到 src/CncLoader.UI");
    }
}
