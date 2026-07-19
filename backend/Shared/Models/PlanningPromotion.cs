namespace AgentStudio.Shared;

/// <summary>
/// Pure text helpers behind "promote a finished planning task to a coding
/// task" (see docs/concepts/planning-research-task-kinds-2026-05.md). The
/// planning agent writes its report to <c>status.md</c>; these functions
/// pull the prefill the create-task modal needs out of it without touching
/// the filesystem, so they unit-test in isolation.
/// </summary>
public static class PlanningPromotion
{
    /// <summary>The stable heading the planning prompt asks the agent to write the next task's prompt under.</summary>
    public const string ProposedHeading = "Proposed task prompt";

    /// <summary>
    /// Extracts the body the new coding task's <c>prompt.md</c> should start
    /// with. When the report carries the stable <c>## Proposed task prompt</c>
    /// heading, returns everything from after it up to the next level-1/2
    /// heading (a level-3+ sub-heading stays inside the section). When the
    /// heading is absent, falls back to the whole report so the user still
    /// gets a populated prompt to trim by hand.
    /// </summary>
    public static string ExtractProposedTaskPrompt(string? statusMarkdown)
    {
        if (string.IsNullOrWhiteSpace(statusMarkdown)) return "";

        var lines = statusMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsProposedHeading(lines[i]))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0) return statusMarkdown.Trim();

        var body = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            if (IsSectionBoundary(lines[i])) break;
            body.Add(lines[i]);
        }

        return string.Join("\n", body).Trim();
    }

    /// <summary>
    /// Title for the promoted task: the planning task's own title when set,
    /// otherwise the first markdown heading in the report, otherwise the
    /// source id as a last resort so the modal is never created title-less.
    /// </summary>
    public static string DeriveTitle(string? planningTitle, string? statusMarkdown, string fallbackId)
    {
        if (!string.IsNullOrWhiteSpace(planningTitle)) return planningTitle.Trim();

        var heading = FirstHeading(statusMarkdown);
        if (!string.IsNullOrWhiteSpace(heading)) return heading;

        return fallbackId;
    }

    private static bool IsProposedHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("## ", StringComparison.Ordinal)) return false;
        // Reject level-3+ headings: the char after "##" must be a space.
        return string.Equals(
            trimmed[3..].Trim(),
            ProposedHeading,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSectionBoundary(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("# ", StringComparison.Ordinal)
            || trimmed.StartsWith("## ", StringComparison.Ordinal);
    }

    private static string? FirstHeading(string? statusMarkdown)
    {
        if (string.IsNullOrWhiteSpace(statusMarkdown)) return null;
        foreach (var raw in statusMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var text = line.TrimStart('#').Trim();
                if (text.Length > 0) return text;
            }
        }
        return null;
    }
}
