using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Parses Codex's native <c>todo_list</c> item family. Unlike the older
/// <c>update_plan</c> tool item, Codex emits the whole checklist on
/// <c>item.started</c> and every <c>item.updated</c> frame and represents item
/// state as a <c>completed</c> boolean. The first unfinished entry is the
/// active item; later unfinished entries remain pending.
/// </summary>
internal static class CodexTodoListFrameParser
{
    internal const string Source = "codex/todo_list";

    public static bool TryParse(string? raw, out CliRunEvent.PlanUpdated plan)
    {
        plan = null!;
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart()[0] != '{') return false;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeValue)
                || typeValue.GetString() is not ("item.started" or "item.updated" or "item.completed")
                || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var itemType)
                || !string.Equals(itemType.GetString(), "todo_list", StringComparison.Ordinal)
                || !item.TryGetProperty("items", out var itemsValue)
                || itemsValue.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var items = new List<PlanFrameItem>();
            var activeAssigned = false;
            foreach (var value in itemsValue.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("text", out var textValue)
                    || textValue.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var title = textValue.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;
                var completed = value.TryGetProperty("completed", out var completedValue)
                                && completedValue.ValueKind == JsonValueKind.True;
                var status = completed
                    ? "done"
                    : !activeAssigned
                        ? "active"
                        : "pending";
                if (!completed) activeAssigned = true;
                items.Add(new PlanFrameItem(PlanItemId.From(title), title, status));
            }

            if (items.Count == 0) return false;
            plan = new CliRunEvent.PlanUpdated(Source, items);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Marker vocabulary understood by coding-agent-chat's plan projection.</summary>
    public static string RenderMarker(CliRunEvent.PlanUpdated plan)
        => "Todo " + string.Join("; ", plan.Items.Select(item => $"[{item.Status}] {SingleLine(item.Title)}"));

    private static string SingleLine(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
