using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the structure of the CLI skill files under <c>docs/cli-skills/</c>.
/// Every CLI driver our project supports must have a corresponding skill
/// file, every skill file must have the YAML frontmatter the loader expects,
/// and every file must carry a unique sentinel string the @billable e2e
/// pickup specs rely on. If any of these slip, the cross-CLI guidance breaks
/// silently — these tests catch it before a PR.
/// </summary>
public class CliSkillFilesTests
{
    private static readonly string[] ExpectedSkills =
    [
        "cli-overview",
        "cli-claude",
        "cli-codex",
        "cli-copilot",
        "cli-gemini"
    ];

    private static string FindSkillsRoot([CallerFilePath] string sourceFile = "")
    {
        // Anchor at this source file's location so the lookup works regardless
        // of where the test binary is built. CallerFilePath gives the absolute
        // path to this .cs file at compile time; one level up is backend.Tests/,
        // two levels up is the repo root.
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "cli-skills");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"docs/cli-skills/ not found by walking up from {sourceFile}.");
    }

    [Theory]
    [InlineData("cli-overview")]
    [InlineData("cli-claude")]
    [InlineData("cli-codex")]
    [InlineData("cli-copilot")]
    [InlineData("cli-gemini")]
    public void EverySkill_HasFrontmatterAndSentinel(string skill)
    {
        var path = Path.Combine(FindSkillsRoot(), $"{skill}.md");
        Assert.True(File.Exists(path), $"Missing skill file: {path}");

        var text = File.ReadAllText(path);

        // Frontmatter block must be the first thing in the file.
        var frontmatter = Regex.Match(text, @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
            RegexOptions.Singleline);
        Assert.True(frontmatter.Success, $"Skill {skill} is missing the YAML frontmatter block");

        var fm = frontmatter.Groups["body"].Value;
        Assert.Matches($@"(?m)^name:\s*{Regex.Escape(skill)}\s*$", fm);
        Assert.Matches(@"(?m)^description:\s*\S.+$", fm);

        // Sentinel: a unique upper-case string the e2e pickup tests look for.
        // Format locked to avoid accidental collisions with prose that
        // happens to use the word "sentinel".
        var sentinel = Regex.Match(fm, @"(?m)^sentinel:\s*(?<value>TASKBOARD-CLI-SKILL-[A-Z0-9-]+)\s*$");
        Assert.True(sentinel.Success,
            $"Skill {skill} is missing 'sentinel: TASKBOARD-CLI-SKILL-...' in its frontmatter");

        // The sentinel must also appear in the body so a CLI that reads the
        // file (and reasons about it without parsing YAML) can still find it.
        Assert.Contains(sentinel.Groups["value"].Value, text);
    }

    [Fact]
    public void AllExpectedSkillFilesExist_AndNothingExtra()
    {
        var root = FindSkillsRoot();
        var found = Directory.EnumerateFiles(root, "cli-*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToArray();

        var expected = ExpectedSkills.OrderBy(n => n).ToArray();
        Assert.Equal(expected, found);
    }

    [Fact]
    public void AllSentinelsAreUnique()
    {
        var root = FindSkillsRoot();
        var sentinels = ExpectedSkills.Select(skill =>
        {
            var text = File.ReadAllText(Path.Combine(root, $"{skill}.md"));
            var m = Regex.Match(text, @"(?m)^sentinel:\s*(?<value>TASKBOARD-CLI-SKILL-[A-Z0-9-]+)\s*$");
            return (skill, value: m.Groups["value"].Value);
        }).ToList();

        var distinct = sentinels.Select(s => s.value).Distinct().Count();
        Assert.Equal(sentinels.Count, distinct);
    }
}
