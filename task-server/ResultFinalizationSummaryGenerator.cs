using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Application boundary for producing the remote Result document. The coding
/// agent remains a read-only source of durable events and deliverables; this
/// generator is the sole writer of the canonical <c>status.md</c> artifact.
/// </summary>
public interface IResultFinalizationSummaryGenerator
{
    Task<ResultSummaryGeneration> GenerateAsync(
        ResultSummaryContext context,
        CancellationToken cancellationToken);
}

public sealed record ResultSummaryContext(
    TaskDto Task,
    RunDto Run,
    IReadOnlyList<EventDto> Events,
    IReadOnlyList<ArtifactDto> Artifacts);

public sealed record ResultSummaryGeneration(bool Succeeded, string? Markdown, string? Error)
{
    public static ResultSummaryGeneration Success(string markdown) => new(true, markdown, null);
    public static ResultSummaryGeneration Failure(string error) => new(false, null, error);
}

/// <summary>
/// Deterministic Task Server summary used by the separated V1 control plane.
/// It deliberately relies only on durable core events and artifact metadata,
/// so Result finalization continues while Studio is detached and never starts
/// a second coding-agent run.
/// </summary>
public sealed class ApplicationResultFinalizationSummaryGenerator
    : IResultFinalizationSummaryGenerator
{
    public Task<ResultSummaryGeneration> GenerateAsync(
        ResultSummaryContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = context.Events
            .Where(item => string.Equals(
                item.Kind,
                LifecycleEventKinds.AgentMessage,
                StringComparison.Ordinal))
            .Select(TryReadText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => OneLine(text!))
            .Where(text => !IsTerminalSentinel(text))
            .TakeLast(5)
            .ToList();
        var terminal = context.Events
            .Select(TryReadText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .LastOrDefault(IsTerminalSentinel);
        var result = ResultValue(terminal);

        var markdown = new StringBuilder()
            .AppendLine("# Status")
            .AppendLine()
            .AppendLine($"- Result: {result}")
            .AppendLine("- Case: remote-run")
            .AppendLine()
            .AppendLine("## Overview")
            .AppendLine()
            .AppendLine($"- {OneLine(context.Task.Title)}")
            .AppendLine("- The application finalized this Result from the durable remote core-run events and uploaded deliverables.")
            .AppendLine()
            .AppendLine("## What Was Done")
            .AppendLine();
        if (messages.Count == 0)
        {
            markdown.AppendLine("- The remote core run completed and recorded no additional narrative messages.");
        }
        else
        {
            foreach (var message in messages)
                markdown.AppendLine($"- {message}");
        }

        markdown
            .AppendLine()
            .AppendLine("## Deliverables")
            .AppendLine();
        var deliverables = context.Artifacts
            .Where(item => !string.Equals(item.Name, "status.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
        if (deliverables.Count == 0)
        {
            markdown.AppendLine("- No separate result artifacts were uploaded.");
        }
        else
        {
            foreach (var artifact in deliverables)
                markdown.AppendLine($"- `{artifact.Name.Replace('`', '\'')}`");
        }

        markdown
            .AppendLine()
            .AppendLine("## Open Items")
            .AppendLine()
            .AppendLine(result is "Success" or "NoOp"
                ? "- None recorded by the completed core run."
                : "- Review the terminal core-run signal and its durable evidence.");

        return Task.FromResult(ResultSummaryGeneration.Success(markdown.ToString()));
    }

    private static string? TryReadText(EventDto item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "text", "Text" })
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed diagnostic payloads stay available in canonical replay,
            // but they do not make Result finalization fail.
        }
        return null;
    }

    private static bool IsTerminalSentinel(string text)
        => text.Contains("[[TASK_DONE]]", StringComparison.Ordinal)
           || text.Contains("[[TASK_NOOP]]", StringComparison.Ordinal)
           || text.Contains("[[TASK_BLOCKED:", StringComparison.Ordinal)
           || text.Contains("[[TASK_NEEDS_INPUT:", StringComparison.Ordinal);

    private static string ResultValue(string? terminal)
    {
        if (terminal?.Contains("[[TASK_DONE]]", StringComparison.Ordinal) == true) return "Success";
        if (terminal?.Contains("[[TASK_NOOP]]", StringComparison.Ordinal) == true) return "NoOp";
        return "Partial";
    }

    private static string OneLine(string value)
    {
        var line = string.Join(' ', value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 500 ? line : line[..497] + "...";
    }
}
