using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentStudio.Docs;
using AgentStudio.Host;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestrator;

public interface IOrchestratorChatPersistence
{
    bool IsCentralTaskServerStore { get; }

    Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        int limit,
        CancellationToken ct);

    Task AppendAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        OrchestratorChatTurn turn,
        CancellationToken ct);

    Task<IReadOnlyList<OrchestratorContextDto>> ListContextsAsync(
        bool includeHidden,
        CancellationToken ct);
}

/// <summary>
/// In-process context store for the monolith profile. The local host derives
/// project ownership from its registry-backed watch paths and uses the
/// existing context-keyed transcript files directly, so no self-HTTP URL is
/// required. Configuring a standalone Task Server selects the remote store
/// instead.
/// </summary>
public sealed class LocalOrchestratorChatPersistence(
    OrchestratorChat chat,
    TaskScannerService scanner,
    WorkbenchCatalogueService? workbenches = null)
    : IOrchestratorChatPersistence
{
    private const int ContextSummaryMaxLength = 180;

    public bool IsCentralTaskServerStore => false;

    public Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDossier(projectName, context);
        if (!chat.EnsureContext(watchPath, context))
            throw new InvalidOperationException(
                $"The local context store could not materialize a context for project '{projectName}'.");
        return Task.FromResult<IReadOnlyList<OrchestratorChatTurn>>(
            chat.Read(watchPath, context)
                .TakeLast(Math.Clamp(limit, 1, 1000))
                .ToArray());
    }

    public Task AppendAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        OrchestratorChatTurn turn,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDossier(projectName, context);
        if (!chat.Append(watchPath, turn, context))
            throw new InvalidOperationException(
                $"The local context store could not append a turn for project '{projectName}'.");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrchestratorContextDto>> ListContextsAsync(
        bool includeHidden,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tasks = scanner.ScanAllJobsWithArchive();
        var result = new List<OrchestratorContextDto>();
        foreach (var project in scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (!OrchestratorContextKey.TryParse($"project:{project.Name}", out var projectContext))
                continue;

            var projectTurns = chat.Read(project.Path, projectContext);
            result.Add(BuildContext(
                projectContext,
                project.Name,
                task: null,
                dossier: null,
                projectTurns,
                OrchestratorChat.ResolveContextPath(project.Path, projectContext),
                project.Path));

            var contextDirectory = Path.Combine(
                project.Path,
                ".orchestrator",
                "context-chats");
            if (!Directory.Exists(contextDirectory)) continue;
            var dossiers = workbenches?.List(project.Name, includeHistory: true)?.Items ?? [];

            foreach (var path in Directory.EnumerateFiles(
                         contextDirectory,
                         "*.jsonl",
                         SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var encoded = Path.GetFileNameWithoutExtension(path);
                if (!OrchestratorContextKey.TryDecode(encoded, out var context)
                    || context.Kind is not (OrchestratorContextKey.TaskKind or OrchestratorContextKey.DossierKind)
                    || !string.Equals(
                        context.ProjectId,
                        project.Name,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var task = context.Kind == OrchestratorContextKey.TaskKind
                    ? tasks.FirstOrDefault(candidate =>
                        string.Equals(candidate.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(candidate.Key, context.TaskKey, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidate.TaskKey, context.TaskKey, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidate.Id, context.TaskKey, StringComparison.OrdinalIgnoreCase)))
                    : null;
                var dossier = context.Kind == OrchestratorContextKey.DossierKind
                    ? dossiers.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, context.DossierId, StringComparison.Ordinal))
                    : null;
                var hidden = OrchestratorContextVisibilityPolicy.IsHidden(
                    context.Kind,
                    task?.State ?? dossier?.Status);
                if (hidden && !includeHidden) continue;

                result.Add(BuildContext(
                    context,
                    project.Name,
                    task,
                    dossier,
                    chat.Read(project.Path, context),
                    path,
                    project.Path));
            }
        }

        return Task.FromResult<IReadOnlyList<OrchestratorContextDto>>(result);
    }

    private void ValidateDossier(string projectName, OrchestratorContextKey? context)
    {
        if (context?.Kind != OrchestratorContextKey.DossierKind) return;
        if (workbenches?.Read(projectName, context.DossierId!) is not null) return;
        throw new KeyNotFoundException(
            $"Unknown Dossier '{context.DossierId}' in project '{projectName}'.");
    }

    private static OrchestratorContextDto BuildContext(
        OrchestratorContextKey context,
        string projectName,
        AgentStudio.Shared.TaskInfo? task,
        WorkbenchListItem? dossier,
        IReadOnlyList<OrchestratorChatTurn> turns,
        string transcriptPath,
        string projectPath)
    {
        var fallbackTimestamp = File.Exists(transcriptPath)
            ? File.GetCreationTimeUtc(transcriptPath)
            : Directory.Exists(projectPath)
                ? Directory.GetCreationTimeUtc(projectPath)
                : DateTime.UnixEpoch;
        var createdAt = turns.Count == 0 ? fallbackTimestamp : turns.Min(turn => turn.Ts);
        var updatedAt = turns.Count == 0 ? fallbackTimestamp : turns.Max(turn => turn.Ts);
        var hiddenAt = OrchestratorContextVisibilityPolicy.IsHidden(
                context.Kind,
                task?.State ?? dossier?.Status)
            ? updatedAt
            : (DateTime?)null;
        var fallbackSummary = task?.Title
                              ?? dossier?.Title
                              ?? context.DossierId
                              ?? context.TaskKey
                              ?? $"Project chat for {projectName}";
        var summary = BuildSummary(
            turns.LastOrDefault(turn => turn.Role == OrchestratorChatRoles.User)?.Text,
            fallbackSummary);
        var latestModel = turns.LastOrDefault(turn =>
            !string.IsNullOrWhiteSpace(turn.Model)
            || !string.IsNullOrWhiteSpace(turn.TokenUsage?.Model));

        return new OrchestratorContextDto(
            context.Value,
            context.Kind,
            projectName,
            projectName,
            task?.Id,
            context.TaskKey,
            summary,
            createdAt,
            updatedAt,
            hiddenAt,
            turns.Count,
            latestModel?.Model ?? latestModel?.TokenUsage?.Model,
            turns.Sum(turn => (long)(turn.TokenUsage?.InputTokens ?? 0)),
            turns.Sum(turn => (long)(turn.TokenUsage?.OutputTokens ?? 0)),
            turns.Sum(turn => (long)(turn.TokenUsage?.CacheReadTokens ?? 0)),
            turns.Sum(turn => (long)(turn.TokenUsage?.CacheCreationTokens ?? 0)),
            DossierId: context.DossierId,
            DossierKey: dossier?.Key,
            DossierTitle: dossier?.Title,
            DossierState: dossier?.Status);
    }

    private static string BuildSummary(string? body, string fallback)
    {
        var compact = string.Join(
            ' ',
            (body ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact)) return fallback;
        return compact.Length <= ContextSummaryMaxLength
            ? compact
            : compact[..(ContextSummaryMaxLength - 3)].TrimEnd() + "...";
    }
}

/// <summary>
/// Remote-ready persistence boundary for Orchestrator Chat. All transcript,
/// summary, lifecycle, and receipt state is owned by the configured Task
/// Server; the Studio host only resolves execution evidence and invokes the
/// coding CLI.
/// </summary>
public sealed class TaskServerOrchestratorChatPersistence(
    IHttpClientFactory clients,
    WorkbenchCatalogueService? workbenches = null,
    OrchestratorContextHubBroadcaster? contextBroadcaster = null)
    : IOrchestratorChatPersistence
{
    public bool IsCentralTaskServerStore => true;

    public async Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        int limit,
        CancellationToken ct)
    {
        using var response = await Client().GetAsync(
            ContextPath(projectName, context)
            + $"/turns?limit={Math.Clamp(limit, 1, 1000)}"
            + DossierQuery(projectName, context, "&"),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var transcript = await response.Content.ReadFromJsonAsync<OrchestratorContextTranscriptResponse>(
            cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Task Server returned an empty context transcript response.");
        if (contextBroadcaster is not null)
        {
            await contextBroadcaster.ContextChangedAsync(
                transcript.Context.ProjectName,
                transcript.Context.ContextKey,
                transcript.Context.UpdatedAt,
                ct).ConfigureAwait(false);
        }
        return transcript.Turns.Select(FromDto).ToArray();
    }

    public async Task AppendAsync(
        string projectName,
        string watchPath,
        OrchestratorContextKey? context,
        OrchestratorChatTurn turn,
        CancellationToken ct)
    {
        using var response = await Client().PostAsJsonAsync(
            ContextPath(projectName, context) + "/turns" + DossierQuery(projectName, context, "?"),
            new AppendOrchestratorContextTurnRequest(ToDto(turn)),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        if (contextBroadcaster is not null)
        {
            var contextKey = context?.Kind is OrchestratorContextKey.TaskKind or OrchestratorContextKey.DossierKind
                ? context.Value
                : $"project:{projectName}";
            await contextBroadcaster.ContextChangedAsync(
                projectName,
                contextKey,
                turn.Ts,
                ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<OrchestratorContextDto>> ListContextsAsync(
        bool includeHidden,
        CancellationToken ct)
    {
        using var response = await Client().GetAsync(
            $"/api/v1/orchestrator-contexts?includeHidden={includeHidden.ToString().ToLowerInvariant()}",
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<OrchestratorContextListResponse>(
            cancellationToken: ct).ConfigureAwait(false);
        if (result is null) return [];

        var reconciled = new List<OrchestratorContextDto>(result.Contexts.Count);
        foreach (var context in result.Contexts)
        {
            if (context.Kind != OrchestratorContextKinds.Dossier || context.DossierId is null)
            {
                reconciled.Add(context);
                continue;
            }

            var dossier = workbenches?.Read(context.ProjectName, context.DossierId)?.Workbench;
            if (dossier is null) continue;
            var live = new OrchestratorDossierContext(
                dossier.Id,
                dossier.Key,
                dossier.Title,
                dossier.Status);
            var differs = !string.Equals(context.DossierKey, live.DossierKey, StringComparison.Ordinal)
                          || !string.Equals(context.DossierTitle, live.DossierTitle, StringComparison.Ordinal)
                          || !string.Equals(context.DossierState, live.LifecycleState, StringComparison.Ordinal);
            var current = differs
                ? await ReconcileDossierAsync(context.ProjectName, live, ct).ConfigureAwait(false)
                : context;
            if (includeHidden || !OrchestratorContextVisibilityPolicy.IsHidden(
                    OrchestratorContextKinds.Dossier,
                    live.LifecycleState))
                reconciled.Add(current);
        }
        return reconciled;
    }

    public async Task<ImportLegacyOrchestratorChatResponse> ImportLegacyAsync(
        string projectName,
        string sourceSha256,
        IReadOnlyList<OrchestratorChatTurn> turns,
        CancellationToken ct)
    {
        using var response = await Client().PostAsJsonAsync(
            $"/api/v1/orchestrator-contexts/projects/{Uri.EscapeDataString(projectName)}/legacy-import",
            new ImportLegacyOrchestratorChatRequest(
                sourceSha256,
                turns.Select(turn => ToDto(turn) with { Receipt = null }).ToArray()),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ImportLegacyOrchestratorChatResponse>(
            cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Task Server returned an empty legacy import response.");
    }

    private HttpClient Client() => clients.CreateClient(TaskServerPlaneProxy.ClientName);

    private async Task<OrchestratorContextDto> ReconcileDossierAsync(
        string projectName,
        OrchestratorDossierContext dossier,
        CancellationToken ct)
    {
        using var response = await Client().PutAsJsonAsync(
            $"/api/v1/orchestrator-contexts/projects/{Uri.EscapeDataString(projectName)}"
            + $"/dossiers/{Uri.EscapeDataString(dossier.DossierId)}",
            dossier,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<OrchestratorContextDto>(cancellationToken: ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Task Server returned an empty Dossier context response.");
    }

    private string ContextPath(string projectName, OrchestratorContextKey? context)
    {
        var project = Uri.EscapeDataString(projectName);
        if (context?.Kind == OrchestratorContextKey.TaskKind)
            return $"/api/v1/orchestrator-contexts/projects/{project}/tasks/{Uri.EscapeDataString(context.TaskKey!)}";
        if (context?.Kind == OrchestratorContextKey.DossierKind)
            return $"/api/v1/orchestrator-contexts/projects/{project}/dossiers/{Uri.EscapeDataString(context.DossierId!)}";
        return $"/api/v1/orchestrator-contexts/projects/{project}";
    }

    private string DossierQuery(
        string projectName,
        OrchestratorContextKey? context,
        string prefix)
    {
        if (context?.Kind != OrchestratorContextKey.DossierKind) return string.Empty;
        var dossier = workbenches?.Read(projectName, context.DossierId!)?.Workbench
            ?? throw new KeyNotFoundException(
                $"Unknown Dossier '{context.DossierId}' in project '{projectName}'.");
        var values = new Dictionary<string, string?>
        {
            ["dossierKey"] = dossier.Key,
            ["dossierTitle"] = dossier.Title,
            ["lifecycleState"] = dossier.Status,
        };
        var query = string.Join("&", values
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{item.Key}={Uri.EscapeDataString(item.Value!)}"));
        return string.IsNullOrEmpty(query) ? string.Empty : prefix + query;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Task Server context store returned {(int)response.StatusCode}: {detail}");
    }

    private static OrchestratorContextTurnDto ToDto(OrchestratorChatTurn turn)
    {
        var usage = turn.TokenUsage is null
            ? null
            : new OrchestratorContextTokenUsageDto(
                turn.TokenUsage.Model,
                turn.TokenUsage.InputTokens,
                turn.TokenUsage.OutputTokens,
                turn.TokenUsage.CacheReadTokens,
                turn.TokenUsage.CacheCreationTokens);
        OrchestratorContextReceiptDto? receipt = null;
        if (turn.ContextReceipt is
            {
                ReceiptId: not null,
                UserTurnId: not null,
                Budget: not null,
                Sources: not null,
            } sourceReceipt)
        {
            receipt = new OrchestratorContextReceiptDto(
                sourceReceipt.ReceiptId,
                sourceReceipt.UserTurnId,
                sourceReceipt.ContextKey,
                sourceReceipt.CapturedAt,
                new OrchestratorContextBudgetReceiptDto(
                    sourceReceipt.Budget.AutomaticSoftCapTokens,
                    sourceReceipt.Budget.AutomaticHardCapTokens,
                    sourceReceipt.Budget.TotalHardCapTokens,
                    sourceReceipt.Budget.EstimatedIncludedTokens),
                sourceReceipt.Sources.Select(source => new OrchestratorContextSourceReceiptDto(
                    source.SourceId,
                    source.Kind,
                    source.Revision,
                    source.Sha256,
                    source.Freshness,
                    source.IncludedCharacters,
                    source.EstimatedTokens,
                    source.Status,
                    source.Reason)).ToArray());
        }
        return new OrchestratorContextTurnDto(
            turn.Id,
            turn.Ts,
            turn.Role,
            turn.Text,
            turn.Model,
            usage,
            turn.ErrorMessage,
            turn.ErrorDetail,
            turn.Attachments?.Select(item =>
                new OrchestratorContextAttachmentDto(item.Alt, item.RelativePath)).ToArray(),
            receipt);
    }

    private static OrchestratorChatTurn FromDto(OrchestratorContextTurnDto turn)
    {
        var usage = turn.TokenUsage is null
            ? null
            : new OrchestratorTokenUsage
            {
                Model = turn.TokenUsage.Model,
                InputTokens = checked((int)turn.TokenUsage.InputTokens),
                OutputTokens = checked((int)turn.TokenUsage.OutputTokens),
                CacheReadTokens = checked((int)turn.TokenUsage.CacheReadTokens),
                CacheCreationTokens = checked((int)turn.TokenUsage.CacheCreationTokens),
            };
        OrchestratorContextReceipt? receipt = null;
        if (turn.Receipt is not null)
        {
            receipt = new OrchestratorContextReceipt(
                turn.Receipt.ContextKey.StartsWith("task:", StringComparison.Ordinal) ? "task" : "project",
                turn.Receipt.ContextKey,
                TaskKeyFromContext(turn.Receipt.ContextKey),
                turn.Receipt.Sources.Select(source => source.SourceId).ToArray(),
                turn.Receipt.CapturedAt,
                turn.Receipt.ReceiptId,
                turn.Receipt.UserTurnId,
                new OrchestratorContextBudgetReceipt(
                    turn.Receipt.Budget.AutomaticSoftCapTokens,
                    turn.Receipt.Budget.AutomaticHardCapTokens,
                    turn.Receipt.Budget.TotalHardCapTokens,
                    turn.Receipt.Budget.EstimatedIncludedTokens),
                turn.Receipt.Sources.Select(source => new OrchestratorContextSourceReceipt(
                    source.SourceId,
                    source.Kind,
                    source.Revision,
                    source.Sha256,
                    source.Freshness,
                    source.IncludedCharacters,
                    source.EstimatedTokens,
                    source.Status,
                    source.Reason)).ToArray());
        }
        return new OrchestratorChatTurn
        {
            Id = turn.TurnId,
            Ts = turn.CreatedAt,
            Role = turn.Role,
            Text = turn.Body,
            Model = turn.Model,
            TokenUsage = usage,
            ErrorMessage = turn.ErrorMessage,
            ErrorDetail = turn.ErrorDetail,
            Attachments = turn.Attachments?.Select(item => new OrchestratorChatAttachment
            {
                Alt = item.Alt,
                RelativePath = item.RelativePath,
            }).ToList(),
            ContextReceipt = receipt,
        };
    }

    private static string? TaskKeyFromContext(string contextKey)
    {
        var slash = contextKey.LastIndexOf('/');
        return slash < 0 || slash == contextKey.Length - 1 ? null : contextKey[(slash + 1)..];
    }
}

public sealed class OrchestratorChatLegacyMigrationHostedService(
    IOrchestratorChatPersistence persistence,
    TaskScannerService scanner,
    OrchestratorChat legacy,
    ILogger<OrchestratorChatLegacyMigrationHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (persistence is not TaskServerOrchestratorChatPersistence central)
            return;
        foreach (var project in scanner.GetWatchPaths())
        {
            var path = OrchestratorChat.ResolveContextPath(project.Path, context: null);
            if (!File.Exists(path)) continue;
            try
            {
                var digest = await HashFileAsync(path, stoppingToken).ConfigureAwait(false);
                var result = await central.ImportLegacyAsync(
                    project.Name,
                    digest,
                    legacy.Read(project.Path),
                    stoppingToken).ConfigureAwait(false);
                logger.LogInformation(
                    "orchestrator-context-legacy-migrated project={Project} imported={Imported} alreadyPresent={AlreadyPresent} rejected={Rejected} sourceSha256={SourceSha256}",
                    project.Name, result.Imported, result.AlreadyPresent, result.Rejected, digest);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "orchestrator-context-legacy-migration-failed project={Project} source={Source}",
                    project.Name,
                    path);
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
