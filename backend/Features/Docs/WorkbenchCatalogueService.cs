using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

/// <summary>
/// Read-only repository discovery for experiment Workbenches. Canonical items
/// are folders that carry a <c>workbench.json</c> descriptor and live anywhere
/// under docs/ (each Workbench sits with its own theme, e.g.
/// docs/operations/&lt;id&gt;/ or docs/quality/&lt;id&gt;/); the recursive scan
/// skips dot-directories and node_modules-like folders. The small legacy list
/// is an explicit migration bridge for named, already-existing artifacts, never
/// a heuristic scan of arbitrary HTML.
/// </summary>
public sealed class WorkbenchCatalogueService
{
    private const long MaxHtmlBytes = 20L * 1024 * 1024;

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly GitService _git;

    private static readonly HashSet<string> CurrentStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending" };
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending", "decided", "archived" };
    private static readonly HashSet<string> AllowedPhases = new(StringComparer.Ordinal)
        { "shaping", "testing", "decision-ready" };

    private sealed record LegacyWorkbench(
        string Id, string Title, string Summary, string RepoRelPath, string Phase,
        string[] SourceTaskKeys);

    private static readonly LegacyWorkbench[] LegacyPilot =
    [
        // "Pipeline workbench" removed 2026-07-24: idea discarded by the operator.
        new("workbench-mockup-family", "Workbench mockup family",
            "Shape the Workbench host, list, viewer, and later decision surfaces.",
            "docs/concepts/mockups/experimentier-workbench.html", "testing", ["AGT-2122"]),
        new("app-survey", "Application survey",
            "Understand the current product surfaces through the visual survey findings.",
            "docs/quality/design/app-survey-2026-07-11.html", "decision-ready", []),
        // "Decoupled lifecycles" removed 2026-08-08: wiki page deleted by the operator.
    ];

    public WorkbenchCatalogueService(TaskScannerService scanner, ProjectRegistry registry, GitService git)
    {
        _scanner = scanner;
        _registry = registry;
        _git = git;
    }

    public WorkbenchCatalogue? List(string projectName, bool includeHistory = false)
    {
        var root = ResolveRoot(projectName);
        if (root == null) return null;

        var items = DiscoverCanonical(root);
        foreach (var duplicate in items
                     .Where(item => item.Valid)
                     .GroupBy(item => item.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .SelectMany(group => group)
                     .ToList())
        {
            var index = items.IndexOf(duplicate);
            items[index] = duplicate with
            {
                Valid = false,
                Status = "invalid",
                Error = "Workbench id is duplicated by another canonical descriptor.",
            };
        }
        var ids = items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var legacy in LegacyPilot)
        {
            if (ids.Contains(legacy.Id)) continue;
            var full = ContainedPath(root, legacy.RepoRelPath);
            if (full == null || !File.Exists(full)) continue;
            if (!IsHtmlWithinLimit(full))
            {
                items.Add(new WorkbenchListItem(
                    legacy.Id, legacy.Title, legacy.Summary, "invalid", legacy.Phase,
                    File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, false,
                    $"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Workbench limit.",
                    legacy.SourceTaskKeys));
                continue;
            }
            items.Add(new WorkbenchListItem(
                legacy.Id, legacy.Title, legacy.Summary, "active", legacy.Phase,
                File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, true, null,
                legacy.SourceTaskKeys));
        }

        var visible = items
            .Where(x => !x.Valid || includeHistory || CurrentStatuses.Contains(x.Status))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WorkbenchCatalogue(projectName, includeHistory, visible.Count, visible);
    }

    public WorkbenchDocument? Read(string projectName, string id)
    {
        if (!SafeId(id)) return null;
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var item = List(projectName, includeHistory: true)?.Items.FirstOrDefault(x => x.Id == id);
        if (item is not { Valid: true }) return null;
        var full = ContainedPath(root, item.EntryPath);
        if (full == null || !File.Exists(full)) return null;
        var html = ReadHtmlWithinLimit(full);
        if (html == null) return null;
        var docsRoot = ContainedPath(root, "docs");
        string? descriptorPath = null;
        if (docsRoot != null)
        {
            var descriptorMatches = EnumerateWorkbenchDescriptors(docsRoot)
                .Where(path =>
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), id, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (descriptorMatches.Count == 1) descriptorPath = descriptorMatches[0];
        }
        var provenancePaths = new List<string> { item.EntryPath };
        if (descriptorPath != null)
            provenancePaths.Add(Path.GetRelativePath(root, descriptorPath).Replace('\\', '/'));
        var status = _git.GetStatusForRepoRoot(root);
        var workingTreeModified = status.IsRepo && status.Files.Any(change =>
            provenancePaths.Any(path => ChangeTouchesPath(change.Path, path)));
        var revision = status.IsRepo && status.Error == null && !workingTreeModified
            ? _git.GetHeadShaCached(root)
            : null;
        var fingerprint = descriptorPath == null
            ? null
            : ComputeWorkbenchFingerprint(descriptorPath, full);
        return new WorkbenchDocument(item, html, status.Branch, revision, workingTreeModified, fingerprint);
    }

    /// <summary>
    /// Resolves the one canonical descriptor owned by a Workbench. Decision
    /// mutations use the catalogue's containment and validation rules; legacy
    /// pilot rows never acquire mutation authority. Both descriptor schemas are
    /// resolvable (AGT-2375): schema v1 still carries the majority of the
    /// repository's Workbenches, and its visibility hangs on the same file.
    /// </summary>
    internal WorkbenchMutationSnapshot? ResolveCanonicalForMutation(string projectName, string id)
    {
        if (!SafeId(id)) return null;
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return null;
        var matches = EnumerateWorkbenchDescriptors(docsRoot)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), id, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1) return null;

        var item = List(projectName, includeHistory: true)?.Items
            .Where(candidate => candidate.Valid && candidate.Id == id)
            .ToList();
        if (item is not { Count: 1 }) return null;

        var descriptorPath = ContainedPath(root, Path.GetRelativePath(root, matches[0]));
        var entryPath = ContainedPath(root, item[0].EntryPath);
        if (descriptorPath == null || entryPath == null) return null;
        try
        {
            var descriptorText = File.ReadAllText(descriptorPath);
            var descriptor = JsonNode.Parse(descriptorText) as JsonObject;
            var schemaVersion = descriptor?["schemaVersion"]?.GetValue<int>();
            if (descriptor == null || schemaVersion is not (1 or 2)) return null;
            var status = _git.GetStatusForRepoRoot(root);
            var descriptorRel = Path.GetRelativePath(root, descriptorPath).Replace('\\', '/');
            var entryRel = Path.GetRelativePath(root, entryPath).Replace('\\', '/');
            var dirty = status.IsRepo && status.Files.Any(change =>
                ChangeTouchesPath(change.Path, descriptorRel)
                || ChangeTouchesPath(change.Path, entryRel));
            return new WorkbenchMutationSnapshot(
                root,
                descriptorPath,
                descriptorRel,
                entryPath,
                entryRel,
                descriptor,
                schemaVersion!.Value,
                ComputeDescriptorFingerprint(descriptorText),
                ComputeWorkbenchFingerprint(descriptorPath, entryPath),
                status.IsRepo ? _git.ReadHeadShaAt(root) : null,
                dirty,
                item[0]);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    internal bool OperationIdOwnedByAnotherWorkbench(
        string projectName, string operationId, string workbenchId)
    {
        var root = ResolveRoot(projectName);
        var docsRoot = root == null ? null : ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return false;
        foreach (var descriptorPath in EnumerateWorkbenchDescriptors(docsRoot))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptorPath));
                var obj = json.RootElement;
                // Both schemas store the receipt under the same key, so both
                // can own an operationId (AGT-2375).
                if (RequiredInt(obj, "schemaVersion") is not (1 or 2)
                    || !obj.TryGetProperty("decision", out var decision)
                    || decision.ValueKind != JsonValueKind.Object
                    || OptionalString(decision, "operationId") != operationId)
                    continue;
                return RequiredString(obj, "id") != workbenchId;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                SilentCatch.Note(ex, "Workbench operation-id ownership scan skipped an invalid descriptor.");
            }
        }
        return false;
    }

    /// <summary>
    /// Generic Wiki classification must not stand in for the reasoned,
    /// explicitly confirmed Workbench archive decision. Both descriptor schemas
    /// own their folder: the sidecar archive bug is a property of the
    /// <c>workbench.json</c> descriptor, not of its version, and schema v1 still
    /// carries the large majority of the repository's Workbenches - gating on
    /// v2 alone would have left every one of them exposed (AGT-2375).
    /// </summary>
    public bool OwnsCanonicalPath(string projectName, string relPath)
    {
        var root = ResolveRoot(projectName);
        var docsRoot = root == null ? null : ContainedPath(root, "docs");
        if (root == null || docsRoot == null || !Directory.Exists(docsRoot)) return false;
        var normalized = relPath.Replace('\\', '/').TrimStart('/');
        foreach (var descriptorPath in EnumerateWorkbenchDescriptors(docsRoot))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptorPath));
                var schema = RequiredInt(json.RootElement, "schemaVersion");
                if (schema is not (1 or 2)) continue;
                // pageKind is a schema v2 field; a v1 descriptor is a Workbench
                // by the presence of workbench.json alone.
                if (schema >= 2 && RequiredString(json.RootElement, "pageKind") != "workbench")
                    continue;
                var folder = Path.GetRelativePath(root, Path.GetDirectoryName(descriptorPath)!)
                    .Replace('\\', '/').TrimEnd('/');
                var docsRelativeFolder = Path.GetRelativePath(
                        docsRoot, Path.GetDirectoryName(descriptorPath)!)
                    .Replace('\\', '/').TrimEnd('/');
                if (PathIsWithin(normalized, folder)
                    || PathIsWithin(normalized, docsRelativeFolder))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                SilentCatch.Note(ex, "Workbench Wiki-classification ownership scan skipped an invalid descriptor.");
            }
        }
        return false;
    }

    private static bool PathIsWithin(string candidate, string folder) =>
        candidate.Equals(folder, PathComparison)
        || candidate.StartsWith(folder + "/", PathComparison);

    private List<WorkbenchListItem> DiscoverCanonical(string root)
    {
        var result = new List<WorkbenchListItem>();
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return result;
        foreach (var found in EnumerateWorkbenchDescriptors(docsRoot))
        {
            var dir = Path.GetDirectoryName(found)!;
            var folder = Path.GetFileName(dir);
            var descriptorRel = Path.GetRelativePath(root, found).Replace('\\', '/');
            var safeDir = ContainedPath(root, Path.GetRelativePath(root, dir));
            if (safeDir == null)
            {
                result.Add(Invalid(folder, "Workbench folder is a symbolic link or reparse point.", descriptorRel));
                continue;
            }
            var descriptor = ContainedPath(root,
                Path.GetRelativePath(root, Path.Combine(safeDir, "workbench.json")));
            if (descriptor == null || !File.Exists(descriptor))
            {
                result.Add(Invalid(folder, descriptor == null
                    ? "workbench.json is a symbolic link or reparse point."
                    : "Missing workbench.json.", descriptorRel));
                continue;
            }
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptor));
                var obj = json.RootElement;
                var schema = RequiredInt(obj, "schemaVersion");
                if (schema is not (1 or 2)) throw new InvalidDataException("schemaVersion must be 1 or 2.");
                var id = RequiredString(obj, "id");
                var title = RequiredString(obj, "title");
                var summary = RequiredString(obj, "summary");
                var entrypoint = RequiredString(obj, "entrypoint");
                var lifecycleState = schema >= 2 ? RequiredString(obj, "lifecycleState") : null;
                if (schema >= 2 && !AllowedLifecycleStates.Contains(lifecycleState!))
                    throw new InvalidDataException($"Unsupported lifecycleState '{lifecycleState}'.");
                var updatedText = schema >= 2 ? RequiredString(obj, "editedAt") : RequiredString(obj, "updatedAt");
                var editedBy = schema >= 2 ? RequiredString(obj, "editedBy") : OptionalString(obj, "editedBy");
                var lifecycleHistory = schema >= 2
                    ? RequiredLifecycleHistory(obj, lifecycleState!, editedBy!, updatedText)
                    : [];
                if (schema >= 2 && RequiredString(obj, "pageKind") != "workbench")
                    throw new InvalidDataException("pageKind must be workbench.");
                if (schema >= 2 && (obj.TryGetProperty("status", out _) || obj.TryGetProperty("updatedAt", out _)))
                    throw new InvalidDataException("schemaVersion 2 must not store legacy status or updatedAt fields.");
                // Both schemas store the receipt; only v2 couples it to
                // lifecycleState (null below = the reduced v1 projection).
                var decision = ReadDecision(obj, lifecycleState);
                var status = schema >= 2
                    ? StatusFromDecision(lifecycleState!, decision)
                    : RequiredString(obj, "status");
                var phase = OptionalString(obj, "phase");
                if (!SafeId(id) || id != folder) throw new InvalidDataException("id must match the containing folder.");
                if (!AllowedStatuses.Contains(status)) throw new InvalidDataException($"Unsupported status '{status}'.");
                if (phase != null && !AllowedPhases.Contains(phase)) throw new InvalidDataException($"Unsupported phase '{phase}'.");
                if (!IsUtcLifecycleTimestamp(updatedText, out var updated))
                    throw new InvalidDataException($"{(schema >= 2 ? "editedAt" : "updatedAt")} must be an ISO UTC timestamp ending in Z.");
                var extension = Path.GetExtension(entrypoint);
                if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("entrypoint must be HTML.");
                var full = ContainedPath(safeDir, entrypoint);
                if (full == null || !File.Exists(full)) throw new InvalidDataException("entrypoint is missing or escapes its Workbench folder.");
                if (!IsHtmlWithinLimit(full))
                    throw new InvalidDataException($"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Workbench limit.");
                var repoRel = Path.GetRelativePath(root, full).Replace('\\', '/');
                result.Add(new WorkbenchListItem(id, title, summary, status, phase,
                    updated.UtcDateTime, repoRel, true, null, StringArray(obj, "sourceTaskKeys"))
                {
                    LifecycleState = lifecycleState ?? LifecycleFromStatus(status, phase),
                    EditedBy = editedBy,
                    LifecycleHistory = lifecycleHistory,
                    Decision = decision,
                    DecisionStage = DecisionStage(decision),
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                result.Add(Invalid(folder, ex.Message, descriptorRel, descriptor));
            }
        }
        return result;
    }

    /// <summary>
    /// Recursively yields every <c>workbench.json</c> descriptor under docs/,
    /// skipping dot-directories, node_modules-like folders, the shared assets
    /// tree, and reparse points so a Workbench can live with its own theme.
    /// </summary>
    private static IEnumerable<string> EnumerateWorkbenchDescriptors(string docsRoot)
    {
        var stack = new Stack<string>();
        stack.Push(docsRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files = Directory.GetFiles(dir, "workbench.json");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("assets", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isReparse;
                try { isReparse = (new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (isReparse)
                {
                    // A symlinked / junctioned Workbench folder must be surfaced
                    // (and later refused by the ContainedPath guard) rather than
                    // silently skipped - never recurse through the reparse point.
                    var linkedDescriptor = Path.Combine(sub, "workbench.json");
                    if (File.Exists(linkedDescriptor)) yield return linkedDescriptor;
                    continue;
                }
                stack.Push(sub);
            }
        }
    }

    private static WorkbenchListItem Invalid(string folder, string error, string entryPath, string? safePath = null) =>
        new(SafeId(folder) ? folder : "invalid-workbench", folder, "Descriptor needs repair.",
            "invalid", null, safePath != null && File.Exists(safePath)
                ? File.GetLastWriteTimeUtc(safePath)
                : DateTime.UtcNow,
            entryPath, false, error, []);

    private string? ResolveRoot(string projectName) =>
        ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);

    private static string? ContainedPath(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
        try
        {
            var rootFull = Path.GetFullPath(root);
            var rootPrefix = Path.EndsInDirectorySeparator(rootFull)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, PathComparison)) return null;

            var current = rootFull;
            var segments = Path.GetRelativePath(rootFull, full)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsHtmlWithinLimit(string path)
    {
        try { return new FileInfo(path).Length <= MaxHtmlBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ReadHtmlWithinLimit(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxHtmlBytes) return null;
            using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxHtmlBytes));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxHtmlBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ChangeTouchesPath(string changedPath, string targetPath)
    {
        var normalizedTarget = targetPath.Replace('\\', '/').TrimStart('/');
        var normalizedChange = changedPath.Replace('\\', '/').Trim().Trim('"');
        var renameSeparator = normalizedChange.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            normalizedChange = normalizedChange[(renameSeparator + 4)..].Trim().Trim('"');
        return normalizedChange.Equals(normalizedTarget, PathComparison)
            || normalizedChange.EndsWith('/')
                && normalizedTarget.StartsWith(normalizedChange, PathComparison);
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 80
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string RequiredString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"{name} is required.");
    private static int RequiredInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : throw new InvalidDataException($"{name} is required.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] StringArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Projects a stored decision receipt. <paramref name="lifecycleState"/> is
    /// null for schema v1 descriptors, which have no lifecycle field: the
    /// receipt is then validated on its own terms and simply carries its
    /// provenance (above all the <c>operationId</c> the decision service needs
    /// to answer a retry idempotently). Everything else - shape, outcome,
    /// settled-state invariants - is the same contract for both schemas, so a
    /// receipt accepted on write is never rejected on the next read.
    /// </summary>
    private static WorkbenchDecisionProjection? ReadDecision(JsonElement obj, string? lifecycleState)
    {
        if (!obj.TryGetProperty("decision", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (lifecycleState is "decided" or "done")
                throw new InvalidDataException("Settled lifecycleState requires a decision receipt.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("decision must be an object or null.");

        var outcome = RequiredString(value, "outcome");
        if (outcome is not ("feature-spawn" or "archive"))
            throw new InvalidDataException($"Unsupported decision outcome '{outcome}'.");
        var state = RequiredString(value, "state");
        if (state is not ("pending" or "failed" or "succeeded"))
            throw new InvalidDataException($"Unsupported decision state '{state}'.");
        var operationId = RequiredString(value, "operationId");
        if (!WorkbenchDecisionContracts.SafeOperationId(operationId))
            throw new InvalidDataException("decision operationId is malformed.");
        var preparedAt = RequiredString(value, "preparedAt");
        var preparedBy = RequiredString(value, "preparedBy");
        if (!IsUtcLifecycleTimestamp(preparedAt, out _))
            throw new InvalidDataException("decision preparedAt must be an ISO UTC timestamp ending in Z.");
        var sourceRevision = OptionalString(value, "sourceRevision");
        var sourceFingerprint = OptionalString(value, "sourceFingerprint");
        if (string.IsNullOrWhiteSpace(sourceRevision) && string.IsNullOrWhiteSpace(sourceFingerprint))
            throw new InvalidDataException("decision needs sourceRevision or sourceFingerprint.");
        if (sourceRevision != null
            && (sourceRevision.Length is < 7 or > 64 || !sourceRevision.All(Uri.IsHexDigit)))
            throw new InvalidDataException("decision sourceRevision is malformed.");
        if (sourceFingerprint != null
            && (sourceFingerprint.Length != 64 || !sourceFingerprint.All(Uri.IsHexDigit)))
            throw new InvalidDataException("decision sourceFingerprint is malformed.");
        var confirmedAt = OptionalString(value, "confirmedAt");
        var confirmedBy = OptionalString(value, "confirmedBy");
        if ((confirmedAt == null) != (confirmedBy == null)
            || confirmedAt != null && !IsUtcLifecycleTimestamp(confirmedAt, out _))
            throw new InvalidDataException("decision confirmation provenance is malformed.");
        var decidedAt = OptionalString(value, "decidedAt");
        if (decidedAt != null && !IsUtcLifecycleTimestamp(decidedAt, out _))
            throw new InvalidDataException("decision decidedAt must be an ISO UTC timestamp ending in Z.");
        var reason = OptionalString(value, "reason");
        var failure = OptionalString(value, "failure");
        var spawned = StringArray(value, "spawnedTaskKeys");

        if (outcome == "archive" && string.IsNullOrWhiteSpace(reason))
            throw new InvalidDataException("Archive decision reason is required.");
        if (outcome == "archive" && value.TryGetProperty("taskDraft", out _))
            throw new InvalidDataException("Archive decisions cannot carry a task draft.");
        if (outcome == "feature-spawn")
        {
            if (value.TryGetProperty("reason", out _))
                throw new InvalidDataException("Feature decisions cannot carry an archive reason.");
            if (!value.TryGetProperty("taskDraft", out var taskDraft)
                || taskDraft.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Feature decision taskDraft is required.");
            WorkbenchTaskDraft? parsedDraft;
            try
            {
                parsedDraft = taskDraft.Deserialize<WorkbenchTaskDraft>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Feature decision taskDraft is malformed.", ex);
            }
            var draftError = parsedDraft == null
                ? "task draft is missing."
                : WorkbenchDecisionContracts.ValidateTaskDraft(parsedDraft);
            if (draftError != null)
                throw new InvalidDataException($"Feature decision {draftError}");
        }
        if (state == "failed" && (confirmedAt == null || string.IsNullOrWhiteSpace(failure)))
            throw new InvalidDataException("Failed decision needs confirmation provenance and failure.");
        if (state == "succeeded")
        {
            if (confirmedAt == null || decidedAt == null)
                throw new InvalidDataException("Succeeded decision needs confirmation and decidedAt provenance.");
            // A settled feature decision may legitimately carry no spawned key:
            // the backend does not create cards, the client does it through the
            // existing task API and reports the keys back opportunistically
            // (AGT-2375). The receipt records the decision, not the card.
            if (outcome == "archive" && spawned.Length != 0)
                throw new InvalidDataException("Archive decisions cannot carry spawned task receipts.");
            var expectedLifecycle = outcome == "archive" ? "done" : "decided";
            if (lifecycleState != null && lifecycleState != expectedLifecycle)
                throw new InvalidDataException($"Succeeded {outcome} decision requires lifecycleState '{expectedLifecycle}'.");
        }
        else if (lifecycleState is "decided" or "done")
        {
            throw new InvalidDataException("Pending or failed decisions must remain in a current lifecycle state.");
        }
        if (state != "succeeded" && (decidedAt != null || spawned.Length != 0))
            throw new InvalidDataException("Unsettled decisions cannot carry a settled receipt.");

        return new WorkbenchDecisionProjection(
            outcome, state, operationId, sourceRevision, sourceFingerprint,
            preparedAt, preparedBy, confirmedAt, confirmedBy, decidedAt,
            reason, failure, spawned);
    }

    private static string StatusFromDecision(
        string lifecycleState, WorkbenchDecisionProjection? decision)
    {
        if (decision == null) return StatusFromLifecycle(lifecycleState);
        if (decision.State is "pending" or "failed") return "decision-pending";
        return decision.Outcome == "archive" ? "archived" : "decided";
    }

    private static string? DecisionStage(WorkbenchDecisionProjection? decision) =>
        decision switch
        {
            null => null,
            { State: "pending", ConfirmedAt: null } => "prepared",
            { State: "pending" } => "pending",
            { State: "failed" } => "failed",
            { State: "succeeded", Outcome: "archive" } => "archived",
            { State: "succeeded" } => "succeeded",
            _ => null,
        };

    internal static string ComputeDescriptorFingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static string? ComputeWorkbenchFingerprint(string descriptorPath, string entryPath)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(descriptorPath));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(entryPath));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> AllowedLifecycleStates = new(StringComparer.Ordinal)
        { "in-progress", "review-requested", "decided", "done" };
    private static string StatusFromLifecycle(string state) => state switch
    {
        "in-progress" or "review-requested" => "active",
        "decided" => "decided",
        "done" => "archived",
        _ => "invalid",
    };
    private static string LifecycleFromStatus(string status, string? phase) => status switch
    {
        "decision-pending" => "review-requested",
        "decided" => "decided",
        "archived" => "done",
        _ when phase == "decision-ready" => "review-requested",
        _ => "in-progress",
    };
    private static List<WikiLifecycleHistoryEntry> RequiredLifecycleHistory(
        JsonElement obj, string currentState, string currentEditor, string currentEditedAt)
    {
        if (!obj.TryGetProperty("lifecycleHistory", out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() == 0)
            throw new InvalidDataException("lifecycleHistory needs at least one entry.");
        var result = new List<WikiLifecycleHistoryEntry>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every lifecycleHistory entry must be an object.");
            var state = RequiredString(entry, "state");
            var editedBy = RequiredString(entry, "editedBy");
            var editedAt = RequiredString(entry, "editedAt");
            if (!AllowedLifecycleStates.Contains(state))
                throw new InvalidDataException($"Unsupported lifecycleHistory state '{state}'.");
            if (!IsUtcLifecycleTimestamp(editedAt, out _))
                throw new InvalidDataException("lifecycleHistory editedAt must be an ISO UTC timestamp ending in Z.");
            result.Add(new(state, editedBy, editedAt, OptionalString(entry, "note")));
        }
        var latest = result[^1];
        if (latest.State != currentState || latest.EditedBy != currentEditor || latest.EditedAtUtc != currentEditedAt)
            throw new InvalidDataException("The latest lifecycleHistory entry must match lifecycleState, editedBy, and editedAt.");
        return result;
    }

    private static bool IsUtcLifecycleTimestamp(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return value.EndsWith('Z')
            && DateTimeOffset.TryParse(value, out parsed)
            && parsed.Offset == TimeSpan.Zero;
    }
}

public record WorkbenchCatalogue(string ProjectName, bool IncludesHistory, int Count, List<WorkbenchListItem> Items);
public record WorkbenchListItem(string Id, string Title, string Summary, string Status, string? Phase,
    DateTime UpdatedAtUtc, string EntryPath, bool Valid, string? Error, string[] SourceTaskKeys)
{
    public string? LifecycleState { get; init; }
    public string? EditedBy { get; init; }
    public List<WikiLifecycleHistoryEntry>? LifecycleHistory { get; init; }
    public WorkbenchDecisionProjection? Decision { get; init; }
    public string? DecisionStage { get; init; }
}
public record WorkbenchDocument(WorkbenchListItem Workbench, string Html, string? Branch, string? Revision,
    bool WorkingTreeModified, string? Fingerprint);

public sealed record WorkbenchDecisionProjection(
    string Outcome,
    string State,
    string OperationId,
    string? SourceRevision,
    string? SourceFingerprint,
    string PreparedAt,
    string PreparedBy,
    string? ConfirmedAt,
    string? ConfirmedBy,
    string? DecidedAt,
    string? Reason,
    string? Failure,
    string[] SpawnedTaskKeys);

internal sealed record WorkbenchMutationSnapshot(
    string Root,
    string DescriptorPath,
    string DescriptorRelPath,
    string EntryPath,
    string EntryRelPath,
    JsonObject Descriptor,
    int SchemaVersion,
    string DescriptorFingerprint,
    string? Fingerprint,
    string? Revision,
    bool Dirty,
    WorkbenchListItem Item);
