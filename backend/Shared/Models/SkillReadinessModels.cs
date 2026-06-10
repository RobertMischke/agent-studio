namespace AgentStudio.Shared;

/// <summary>
/// DTOs for the skill readiness check + skill catalog returned from
/// <c>/api/projects/{projectName}/skill-readiness</c> and
/// <c>/api/projects/{projectName}/skills</c>. The check is naive in v1
/// (heading detection plus required phrases); see
/// <c>backend/Services/SkillReadinessService.cs</c> for the rules.
/// </summary>

public enum SkillReadinessStatus
{
    /// <summary>Heading missing from every checked file.</summary>
    Fail,
    /// <summary>Heading found, but the section is missing one or more expected phrases.</summary>
    Warning,
    /// <summary>Heading found and every expected phrase hit.</summary>
    Pass,
}

public record SkillReadinessReport
{
    public string ProjectName { get; init; } = "";
    public SkillReadinessStatus Status { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>
    /// Every file the service looked at, in priority order. The
    /// frontend renders this as a small "checked" list so it is
    /// transparent which files passed / were absent.
    /// </summary>
    public List<SkillReadinessFile> CheckedFiles { get; init; } = [];
    /// <summary>Relative path of the file whose heading matched, or null when none did.</summary>
    public string? MatchedFile { get; init; }
    /// <summary>The full heading line (e.g. "## Agent Software Studio Skills"); null on Fail.</summary>
    public string? Heading { get; init; }
    /// <summary>Labels of phrases the section contained.</summary>
    public List<string> MatchedPhrases { get; init; } = [];
    /// <summary>Labels of phrases the section was missing (drives the warning verdict).</summary>
    public List<string> MissingPhrases { get; init; } = [];
}

public class SkillReadinessFile
{
    public string RelPath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool Exists { get; set; }
    public bool HeadingFound { get; set; }
}

public record SkillReadinessFixTaskPreview
{
    public string WatchPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string PromptMarkdown { get; init; } = "";
    public string TargetState { get; init; } = "";
    public string TaskType { get; init; } = "";
    public SkillReadinessReport Report { get; init; } = new();
}

public record SkillReadinessFixTaskResult
{
    public string JobId { get; init; } = "";
    public string WatchPath { get; init; } = "";
    public string TargetState { get; init; } = "";
    public string Title { get; init; } = "";
}

public enum SkillCategory
{
    /// <summary>Cross-project skill that ships with the task processor.</summary>
    Standard,
    /// <summary>Skill scoped to one watched project, managed by the task processor.</summary>
    ProjectSpecific,
}

public enum SkillSelection
{
    /// <summary>Always attached to managed task runs for this project.</summary>
    Selected,
    /// <summary>Available, surfaced as a suggestion; not auto-attached.</summary>
    Suggested,
}

public record SkillEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public SkillCategory Category { get; init; }
    public SkillSelection Selection { get; init; }
    /// <summary>Path under <c>.agents/</c> in the task processor repo (display only).</summary>
    public string RelPath { get; init; } = "";
}

public record SkillCatalog
{
    public string ProjectName { get; init; } = "";
    public List<SkillEntry> Standard { get; init; } = [];
    public List<SkillEntry> ProjectSpecific { get; init; } = [];
}
