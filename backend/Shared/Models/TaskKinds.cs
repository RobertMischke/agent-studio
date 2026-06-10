namespace AgentStudio.Shared;

/// <summary>
/// Card kinds. A <see cref="Task"/> is a runnable unit of work (the default).
/// An <see cref="Epic"/> is a container that brackets sub-tasks under one
/// overarching goal; it is not code-executed itself - its "run" is a planning /
/// decomposition run, and only its sub-tasks flow through the pipeline.
/// Persisted as the <c>"kind"</c> field in <c>job.json</c>; keep values stable.
/// </summary>
public static class TaskKinds
{
    public const string Task = "task";
    public const string Epic = "epic";

    /// <summary>Coerce a free-form value to a known kind; unknown / empty -> Task.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Epic, System.StringComparison.OrdinalIgnoreCase) ? Epic : Task;

    public static bool IsEpic(string? value) =>
        string.Equals(value?.Trim(), Epic, System.StringComparison.OrdinalIgnoreCase);
}
