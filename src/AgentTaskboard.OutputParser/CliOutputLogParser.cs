using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public static partial class CliOutputLogParser
{
    private static readonly string[] TimeFormats = ["HH:mm:ss.fff", "HH:mm:ss"];

    /// <summary>
    /// Hard memory bounds for <see cref="ParseFile"/>. <c>cli-output.log</c> is
    /// the raw redirect of a child CLI's stdout/stderr and is NOT capped on
    /// disk - a runaway agent can grow it to hundreds of MB or emit a single
    /// newline-less line of arbitrary size. <see cref="ParseFile"/> is called
    /// from many hot paths (the supervisor observation tick, the review-decision
    /// tick, the projection sources, the regression radar, and several
    /// frontend-polled <c>/api/tasks/...</c> endpoints), several of them
    /// concurrently. Materialising the whole file into a
    /// <see cref="List{T}"/> at every call site multiplied peak memory until the
    /// host died with no managed exception (OOM / runtime FailFast - the
    /// "silent disappearance" class the crash markers in Program.cs predict).
    /// These caps keep peak memory bounded regardless of how large the file
    /// grows; the most recent activity is preserved (tail), older bulk is
    /// dropped with a visible marker. Normal jobs (thousands of short lines)
    /// never hit these and parse unchanged.
    /// </summary>
    public const int MaxLinesCap = 50_000;
    public const int MaxLineCharsCap = 64 * 1024;
    public const long MaxTotalCharsCap = 16_000_000;

    public static List<CliOutputLine> ParseFile(string path)
    {
        if (!File.Exists(path)) return [];

        var fallbackDate = File.GetLastWriteTimeUtc(path).Date;
        return ParseLines(File.ReadLines(path), fallbackDate);
    }

    public static List<CliOutputLine> ParseLines(IEnumerable<string> lines, DateTime fallbackDateUtc)
    {
        var fallbackDate = fallbackDateUtc.Kind == DateTimeKind.Utc
            ? fallbackDateUtc.Date
            : fallbackDateUtc.ToUniversalTime().Date;

        var output = new List<CliOutputLine>();
        foreach (var line in lines)
        {
            output.Add(ParseLine(line, fallbackDate));
        }

        return output;
    }

    private static CliOutputLine ParseLine(string line, DateTime fallbackDateUtc)
    {
        var match = PersistedLineRegex().Match(line);
        if (!match.Success)
        {
            return new CliOutputLine
            {
                Timestamp = fallbackDateUtc,
                Stream = "stdout",
                Text = line
            };
        }

        var timestamp = fallbackDateUtc;
        if (DateTime.TryParseExact(match.Groups["time"].Value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
        {
            timestamp = fallbackDateUtc.Add(parsedTime.TimeOfDay);
        }

        return new CliOutputLine
        {
            Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            Stream = match.Groups["stream"].Value,
            Text = match.Groups["text"].Value
        };
    }

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+\[(?<stream>stdout|stderr|user|system|orchestrator)\]\s?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistedLineRegex();
}
