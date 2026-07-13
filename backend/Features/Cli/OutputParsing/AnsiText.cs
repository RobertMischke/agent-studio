using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>Sanitises terminal control sequences before plain-text persistence.</summary>
public static partial class AnsiText
{
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var clean = OscRegex().Replace(text, string.Empty);
        clean = CsiRegex().Replace(clean, string.Empty);
        return NakedSgrRegex().Replace(clean, string.Empty);
    }

    [GeneratedRegex(@"\x1B\][^\x07]*(?:\x07|\x1B\\)")]
    private static partial Regex OscRegex();

    [GeneratedRegex(@"(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiRegex();

    [GeneratedRegex(@"\[(?:\d{1,3}(?:;\d{1,3})*)?m")]
    private static partial Regex NakedSgrRegex();
}
