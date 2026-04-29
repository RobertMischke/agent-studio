using System.Globalization;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public static partial class CliOutputLogParser
{
    private static readonly string[] TimeFormats = ["HH:mm:ss.fff", "HH:mm:ss"];

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

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+\[(?<stream>stdout|stderr|user|system)\]\s?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistedLineRegex();
}
