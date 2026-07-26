using AgentStudio.Docs;
using AgentStudio.Orchestrator;

namespace AgentStudio.Runner;

/// <summary>
/// Persists compact, model-free Workbench events in the canonical project
/// transcript. Resolution happens before append, so every anchor identifies
/// repository bytes observed in that same project at that moment.
/// </summary>
public sealed class WorkbenchTranscriptAnchorService
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
        { "open", "close", "decision" };

    private readonly OrchestratorChat _chat;
    private readonly WorkbenchCatalogueService _workbenches;
    private readonly TaskScannerService _scanner;

    public WorkbenchTranscriptAnchorService(
        OrchestratorChat chat,
        WorkbenchCatalogueService workbenches,
        TaskScannerService scanner)
    {
        _chat = chat;
        _workbenches = workbenches;
        _scanner = scanner;
    }

    public OrchestratorChatTurn Append(
        string projectName,
        string watchPath,
        WorkbenchTranscriptAnchorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventName = request.Event?.Trim().ToLowerInvariant() ?? "";
        if (!AllowedEvents.Contains(eventName))
            throw WorkbenchAttachmentException.Invalid(
                "Workbench anchor event must be open, close, or decision.");
        if (eventName == "decision" && !BoundedPlain(request.Decision, 240))
            throw WorkbenchAttachmentException.Invalid(
                "A Workbench decision anchor requires a bounded decision value.");
        if (eventName != "decision" && request.Decision != null)
            throw WorkbenchAttachmentException.Invalid(
                "Decision text is accepted only for a decision anchor.");
        if (request.Workbench == null)
            throw WorkbenchAttachmentException.Invalid(
                "Workbench anchor requires a Workbench attachment request.");
        var project = _scanner.GetWatchPaths().FirstOrDefault(entry =>
            string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null
            || !string.Equals(
                Path.GetFullPath(project.Path),
                Path.GetFullPath(watchPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            throw WorkbenchAttachmentException.Invalid(
                "Workbench anchor storage does not match the canonical project transcript.");

        var attachment = _workbenches.ResolveAttachment(projectName, request.Workbench);
        if (!OrchestratorContextKey.TryParse($"project:{projectName}", out var context))
            throw WorkbenchAttachmentException.Invalid("Project orchestrator context is invalid.");

        var anchor = new WorkbenchTranscriptAnchor(
            eventName,
            attachment.Id,
            attachment.Branch,
            attachment.Revision,
            attachment.Revision == null ? attachment.ContentFingerprint : null,
            attachment.ProvenanceState,
            request.Decision,
            attachment.PresentationSelection);
        var turn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.Anchor,
            Text = "",
            WorkbenchAnchor = anchor,
        };
        if (!_chat.Append(watchPath, turn, context))
            throw new IOException("Workbench transcript anchor could not be persisted.");
        return turn;
    }

    private static bool BoundedPlain(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= max
        && value.All(c => !char.IsControl(c));
}

public sealed record WorkbenchTranscriptAnchorRequest(
    string Event,
    WorkbenchAttachmentRequest Workbench,
    string? Decision = null);

public sealed record WorkbenchTranscriptAnchor(
    string Event,
    string WorkbenchId,
    string? Branch,
    string? Revision,
    string? ContentFingerprint,
    string ProvenanceState,
    string? Decision,
    WorkbenchPresentationSelection? PresentationSelection);
