using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class ContextUsageParser
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(?:[-*•]\s+|\d+\.\s+)", RegexOptions.Compiled);
    private static readonly Regex MetricRegex = new(@"^(?<label>[^:]{2,80}):\s+(?<value>.+)$", RegexOptions.Compiled);

    public ContextUsageSnapshot Parse(string stdout, string stderr, int? exitCode, string command = "/context usage")
    {
        var output = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : string.IsNullOrWhiteSpace(stdout)
                ? stderr
                : $"{stdout}{Environment.NewLine}{Environment.NewLine}{stderr}";

        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(NormalizeLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var metrics = new List<ContextUsageMetric>();
        var sections = new List<ContextUsageSection>();
        var notes = new List<string>();
        ContextUsageSection? currentSection = null;

        foreach (var line in lines)
        {
            if (TryParseHeader(line, out var header))
            {
                currentSection = new ContextUsageSection { Title = header };
                sections.Add(currentSection);
                continue;
            }

            if (TryParseMetric(line, out var metric))
            {
                if (currentSection == null)
                {
                    metrics.Add(metric);
                }
                else
                {
                    currentSection.Items.Add($"{metric.Label}: {metric.Value}");
                }
                continue;
            }

            if (TryParseBullet(line, out var bullet))
            {
                currentSection ??= new ContextUsageSection { Title = "Details" };
                if (!sections.Contains(currentSection))
                {
                    sections.Add(currentSection);
                }
                currentSection.Items.Add(bullet);
                continue;
            }

            if (currentSection != null)
            {
                currentSection.Items.Add(line);
            }
            else
            {
                notes.Add(line);
            }
        }

        var status = exitCode is null or 0 ? "ok" : "error";
        var error = status == "error"
            ? lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                ?? $"Command exited with code {exitCode}"
            : null;

        return new ContextUsageSnapshot
        {
            At = DateTime.UtcNow,
            Command = command,
            Status = status,
            Error = error,
            Metrics = metrics,
            Sections = sections.Where(section => section.Items.Count > 0).ToList(),
            Notes = notes,
            RawText = output.Trim()
        };
    }

    private static string NormalizeLine(string line)
    {
        var cleaned = AnsiRegex.Replace(line, string.Empty)
            .Replace("\t", " ")
            .Trim();
        return Regex.Replace(cleaned, @"\s{2,}", " ");
    }

    private static bool TryParseHeader(string line, out string header)
    {
        header = string.Empty;

        if (line.StartsWith("#", StringComparison.Ordinal))
        {
            header = line.TrimStart('#', ' ').TrimEnd(':');
            return header.Length > 0;
        }

        if (line.EndsWith(":", StringComparison.Ordinal)
            && !line.Contains("://", StringComparison.Ordinal)
            && !MetricRegex.IsMatch(line))
        {
            header = line.TrimEnd(':').Trim();
            return header.Length > 0;
        }

        return false;
    }

    private static bool TryParseMetric(string line, out ContextUsageMetric metric)
    {
        metric = new ContextUsageMetric();
        var match = MetricRegex.Match(line);
        if (!match.Success) return false;

        var label = match.Groups["label"].Value.Trim();
        var value = match.Groups["value"].Value.Trim();
        if (label.Length == 0 || value.Length == 0) return false;

        metric = new ContextUsageMetric
        {
            Label = label,
            Value = value
        };
        return true;
    }

    private static bool TryParseBullet(string line, out string bullet)
    {
        bullet = string.Empty;
        var match = BulletRegex.Match(line);
        if (!match.Success) return false;

        bullet = line[match.Length..].Trim();
        return bullet.Length > 0;
    }
}
