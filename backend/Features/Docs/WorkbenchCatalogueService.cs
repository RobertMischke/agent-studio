using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

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
    private const long MaxDescriptorBytes = 256L * 1024;
    private const long MaxBriefBytes = 64L * 1024;
    internal const int MaxAttachmentTextChars = 12_000;
    internal const int MaxAttachmentTaskReferences = 64;

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
        new("decoupled-lifecycles", "Decoupled lifecycles",
            "Understand and separate task, run, pipeline, and delivery lifecycles.",
            "docs/concepts/mockups/decoupled-lifecycles.html", "shaping", ["AGT-2091", "AGT-2122"]),
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
                    legacy.SourceTaskKeys)
                {
                    RelatedTaskKeys = [],
                });
                continue;
            }
            items.Add(new WorkbenchListItem(
                legacy.Id, legacy.Title, legacy.Summary, "active", legacy.Phase,
                File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, true, null,
                legacy.SourceTaskKeys)
            {
                RelatedTaskKeys = [],
            });
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
        var descriptorPath = item.DescriptorPath;
        var descriptorText = descriptorPath == null
            ? null
            : ReadTextWithinLimit(ContainedPath(root, descriptorPath), MaxDescriptorBytes);
        if (descriptorPath != null && descriptorText == null) return null;
        var workbenchDir = Path.GetDirectoryName(full);
        var briefFull = workbenchDir == null ? null : ContainedPath(workbenchDir, "brief.md");
        var briefCandidatePath = briefFull == null
            ? null
            : Path.GetRelativePath(root, briefFull).Replace('\\', '/');
        var briefPath = briefFull != null && File.Exists(briefFull)
            ? briefCandidatePath
            : null;
        var briefText = briefFull != null && File.Exists(briefFull)
            ? ReadTextWithinLimit(briefFull, MaxBriefBytes)
            : null;
        var status = _git.GetStatusForRepoRoot(root);
        var provenancePaths = new List<string> { item.EntryPath };
        if (descriptorPath != null) provenancePaths.Add(descriptorPath);
        if (briefCandidatePath != null) provenancePaths.Add(briefCandidatePath);
        var workingTreeModified = status.IsRepo && status.Files.Any(change =>
            provenancePaths.Any(path => ChangeTouchesPath(change.Path, path)));
        var revision = status.IsRepo && status.Error == null && !workingTreeModified
            ? _git.GetHeadShaCached(root)
            : null;
        var provenanceState = workingTreeModified
            ? WorkbenchProvenanceStates.Dirty
            : revision != null
                ? WorkbenchProvenanceStates.ExactRevision
                : WorkbenchProvenanceStates.Unavailable;
        var fingerprint = Fingerprint(item.EntryPath, html, descriptorPath, descriptorText, briefPath, briefText);
        return new WorkbenchDocument(item, html, status.Branch, revision, workingTreeModified)
        {
            DescriptorPath = descriptorPath,
            BriefPath = briefPath,
            ContentFingerprint = fingerprint,
            ProvenanceState = provenanceState,
            FreshnessFailures = provenanceState switch
            {
                WorkbenchProvenanceStates.Dirty =>
                    ["Workbench content has uncommitted changes; HEAD revision is withheld."],
                WorkbenchProvenanceStates.Unavailable =>
                    [string.IsNullOrWhiteSpace(status.Error)
                        ? "An exact Git revision is unavailable."
                        : $"An exact Git revision is unavailable: {Compact(status.Error, 240)}"],
                _ => [],
            },
        };
    }

    /// <summary>
    /// Resolve the model-facing Workbench attachment from the project and id.
    /// Client provenance is used only as an optimistic freshness assertion;
    /// paths, repository text, branch, revision, and task facts are rebuilt
    /// from the project repository on every call.
    /// </summary>
    public WorkbenchContextAttachment ResolveAttachment(
        string projectName,
        WorkbenchAttachmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SafeId(request.Id))
            throw WorkbenchAttachmentException.Invalid("Workbench id is invalid.");
        ValidateExpectedProvenance(request);
        ValidateSelection(request.Selection);

        var document = Read(projectName, request.Id)
            ?? throw WorkbenchAttachmentException.NotFound(
                $"Workbench '{request.Id}' was not found or is invalid in project '{projectName}'.");
        var item = document.Workbench;
        if (string.IsNullOrWhiteSpace(document.DescriptorPath))
            throw WorkbenchAttachmentException.Invalid(
                $"Workbench '{request.Id}' has no canonical workbench.json descriptor.");

        if (!string.IsNullOrWhiteSpace(request.ExpectedRevision)
            && !string.Equals(request.ExpectedRevision.Trim(), document.Revision, StringComparison.OrdinalIgnoreCase))
            throw WorkbenchAttachmentException.Stale(
                $"Workbench '{request.Id}' no longer matches the observed revision.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedContentFingerprint)
            && !string.Equals(request.ExpectedContentFingerprint.Trim(), document.ContentFingerprint, StringComparison.Ordinal))
            throw WorkbenchAttachmentException.Stale(
                $"Workbench '{request.Id}' no longer matches the observed content fingerprint.");

        var root = ResolveRoot(projectName)
            ?? throw WorkbenchAttachmentException.NotFound($"Unknown project '{projectName}'.");
        var validationFailures = new List<string>();
        string contextText;
        string contextSourcePath;
        if (document.BriefPath != null)
        {
            var brief = ReadTextWithinLimit(ContainedPath(root, document.BriefPath), MaxBriefBytes);
            if (brief == null)
                throw WorkbenchAttachmentException.Invalid(
                    $"Workbench '{request.Id}' brief.md is unavailable or exceeds {MaxBriefBytes / 1024} KiB.");
            contextSourcePath = document.BriefPath;
            contextText = SafeRepositoryText(brief, validationFailures, "brief.md");
        }
        else
        {
            contextSourcePath = document.DescriptorPath!;
            contextText = SafeRepositoryText(
                $"Title: {item.Title}\nSummary: {item.Summary}\nLifecycle: {item.LifecycleState ?? item.Status}\nPhase: {item.Phase ?? "(none)"}",
                validationFailures,
                "workbench.json");
        }

        if (contextText.Length > MaxAttachmentTextChars)
        {
            contextText = contextText[..MaxAttachmentTextChars];
            validationFailures.Add(
                $"Workbench context was truncated to {MaxAttachmentTextChars} characters.");
        }

        var taskReferences = ResolveTaskReferences(
            projectName,
            _scanner.ScanAllJobsWithArchive(),
            item.SourceTaskKeys,
            item.RelatedTaskKeys,
            validationFailures);

        return new WorkbenchContextAttachment(
            projectName,
            item.Id,
            item.Title,
            document.DescriptorPath!,
            item.EntryPath,
            document.BriefPath,
            document.Branch,
            document.Revision,
            document.ContentFingerprint!,
            document.ProvenanceState!,
            item.Status,
            item.LifecycleState ?? LifecycleFromStatus(item.Status, item.Phase),
            item.Phase,
            contextSourcePath,
            contextText,
            request.Selection,
            taskReferences,
            validationFailures,
            document.FreshnessFailures ?? []);
    }

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
                var descriptorText = ReadTextWithinLimit(descriptor, MaxDescriptorBytes);
                if (descriptorText == null)
                    throw new InvalidDataException($"workbench.json exceeds {MaxDescriptorBytes / 1024} KiB.");
                using var json = JsonDocument.Parse(descriptorText);
                var obj = json.RootElement;
                var schema = RequiredInt(obj, "schemaVersion");
                if (schema is not (1 or 2)) throw new InvalidDataException("schemaVersion must be 1 or 2.");
                var id = RequiredString(obj, "id");
                var title = RequiredString(obj, "title");
                var summary = RequiredString(obj, "summary");
                var entrypoint = RequiredString(obj, "entrypoint");
                if (title.Length > 200) throw new InvalidDataException("title exceeds 200 characters.");
                if (summary.Length > 2_000) throw new InvalidDataException("summary exceeds 2000 characters.");
                if (entrypoint.Length > 500) throw new InvalidDataException("entrypoint exceeds 500 characters.");
                var lifecycleState = schema >= 2 ? RequiredString(obj, "lifecycleState") : null;
                if (schema >= 2 && !AllowedLifecycleStates.Contains(lifecycleState!))
                    throw new InvalidDataException($"Unsupported lifecycleState '{lifecycleState}'.");
                var status = schema >= 2 ? StatusFromLifecycle(lifecycleState!) : RequiredString(obj, "status");
                var updatedText = schema >= 2 ? RequiredString(obj, "editedAt") : RequiredString(obj, "updatedAt");
                var editedBy = schema >= 2 ? RequiredString(obj, "editedBy") : OptionalString(obj, "editedBy");
                var lifecycleHistory = schema >= 2
                    ? RequiredLifecycleHistory(obj, lifecycleState!, editedBy!, updatedText)
                    : [];
                var phase = OptionalString(obj, "phase");
                if (!SafeId(id) || id != folder) throw new InvalidDataException("id must match the containing folder.");
                if (!AllowedStatuses.Contains(status)) throw new InvalidDataException($"Unsupported status '{status}'.");
                if (schema >= 2 && RequiredString(obj, "pageKind") != "workbench")
                    throw new InvalidDataException("pageKind must be workbench.");
                if (schema >= 2 && (obj.TryGetProperty("status", out _) || obj.TryGetProperty("updatedAt", out _)))
                    throw new InvalidDataException("schemaVersion 2 must not store legacy status or updatedAt fields.");
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
                var sourceTaskKeys = TaskKeyArray(obj, "sourceTaskKeys");
                var relatedTaskKeys = TaskKeyArray(obj, "relatedTaskKeys");
                result.Add(new WorkbenchListItem(id, title, summary, status, phase,
                    updated.UtcDateTime, repoRel, true, null, sourceTaskKeys)
                {
                    DescriptorPath = descriptorRel,
                    RelatedTaskKeys = relatedTaskKeys,
                    LifecycleState = lifecycleState ?? LifecycleFromStatus(status, phase),
                    EditedBy = editedBy,
                    LifecycleHistory = lifecycleHistory,
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

    private static string? ReadTextWithinLimit(string? path, long maxBytes)
    {
        if (path == null) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Fingerprint(
        string entryPath,
        string html,
        string? descriptorPath,
        string? descriptorText,
        string? briefPath,
        string? briefText)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(entryPath, html);
        Add(descriptorPath, descriptorText);
        Add(briefPath, briefText);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Add(string? path, string? content)
        {
            if (path == null || content == null) return;
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(content));
            hash.AppendData([0]);
        }
    }

    private static readonly Regex SensitiveRepositoryText = new(
        @"(?im)(-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:password|passwd|secret|api[_-]?key|access[_-]?token|authorization)\s*[:=]|(?:ghp|github_pat|sk)-[A-Za-z0-9_-]{12,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string SafeRepositoryText(
        string value,
        List<string> validationFailures,
        string source)
    {
        if (!SensitiveRepositoryText.IsMatch(value)) return value;
        validationFailures.Add($"{source} text was omitted because it appears to contain credential material.");
        return "[Repository context omitted because it appears to contain credential material.]";
    }

    private static void ValidateSelection(WorkbenchPresentationSelection? selection)
    {
        if (selection == null) return;
        if (!BoundedPlain(selection.Key, 64)
            || !BoundedPlain(selection.Value, 256)
            || selection.Label != null && !BoundedPlain(selection.Label, 120))
            throw WorkbenchAttachmentException.Invalid(
                "Workbench presentation selection contains invalid or oversized fields.");
    }

    private static void ValidateExpectedProvenance(WorkbenchAttachmentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedRevision)
            && !Regex.IsMatch(request.ExpectedRevision.Trim(), "^[0-9a-fA-F]{40,64}$"))
            throw WorkbenchAttachmentException.Invalid(
                "Expected Workbench revision must be a full hexadecimal Git object id.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedContentFingerprint)
            && !Regex.IsMatch(
                request.ExpectedContentFingerprint.Trim(),
                "^sha256:[0-9a-fA-F]{64}$"))
            throw WorkbenchAttachmentException.Invalid(
                "Expected Workbench content fingerprint must be a sha256 value.");
    }

    private static bool BoundedPlain(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= max
        && value.All(c => !char.IsControl(c));

    internal static IReadOnlyList<WorkbenchTaskReference> ResolveTaskReferences(
        string projectName,
        IEnumerable<TaskInfo> allTasks,
        IReadOnlyCollection<string> sourceKeys,
        IReadOnlyCollection<string> relatedKeys,
        List<string> validationFailures)
    {
        var requested = sourceKeys
            .Select(key => (Key: key, Role: "source"))
            .Concat(relatedKeys.Select(key => (Key: key, Role: "related")))
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Roles = group.Select(row => row.Role).Distinct(StringComparer.Ordinal).ToArray(),
            })
            .ToList();
        if (requested.Count > MaxAttachmentTaskReferences)
        {
            validationFailures.Add(
                $"Task references were capped at {MaxAttachmentTaskReferences} entries.");
            requested = requested.Take(MaxAttachmentTaskReferences).ToList();
        }

        // Materialize exactly once, then resolve the complete reference set from
        // one project-filtered lookup. Facts from other projects never enter it.
        var scopedTasks = allTasks
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var byKey = scopedTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Key))
            .GroupBy(task => task.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return requested.Select(reference =>
        {
            if (!byKey.TryGetValue(reference.Key, out var task))
            {
                validationFailures.Add(
                    $"Referenced task '{reference.Key}' is unavailable in project '{projectName}'.");
                return new WorkbenchTaskReference(
                    reference.Key, reference.Roles, "unavailable", null, null, null);
            }
            return new WorkbenchTaskReference(
                reference.Key,
                reference.Roles,
                "resolved",
                string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key,
                Compact(task.Title, 160),
                task.State);
        }).ToList();
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

    private static bool SafeId(string? value) => value is { Length: > 0 and <= 80 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string RequiredString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"{name} is required.");
    private static int RequiredInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : throw new InvalidDataException($"{name} is required.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] TaskKeyArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{name} must be an array.");
        var keys = value.EnumerateArray()
            .Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() : null)
            .ToArray();
        if (keys.Length > 200 || keys.Any(key => !BoundedPlain(key ?? "", 80)))
            throw new InvalidDataException($"{name} contains invalid or too many task keys.");
        return keys.Select(key => key!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Compact(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var oneLine = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..Math.Max(0, max - 3)] + "...";
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
    public string? DescriptorPath { get; init; }
    public string[] RelatedTaskKeys { get; init; } = [];
    public string? LifecycleState { get; init; }
    public string? EditedBy { get; init; }
    public List<WikiLifecycleHistoryEntry>? LifecycleHistory { get; init; }
}
public record WorkbenchDocument(WorkbenchListItem Workbench, string Html, string? Branch, string? Revision,
    bool WorkingTreeModified)
{
    public string? DescriptorPath { get; init; }
    public string? BriefPath { get; init; }
    public string? ContentFingerprint { get; init; }
    public string? ProvenanceState { get; init; }
    public IReadOnlyList<string>? FreshnessFailures { get; init; }
}

public static class WorkbenchProvenanceStates
{
    public const string ExactRevision = "exact-revision";
    public const string Dirty = "dirty";
    public const string Unavailable = "unavailable";
}

public sealed record WorkbenchPresentationSelection(string Key, string Value, string? Label = null);

public sealed record WorkbenchAttachmentRequest(
    string Id,
    string? ExpectedRevision = null,
    string? ExpectedContentFingerprint = null,
    WorkbenchPresentationSelection? Selection = null);

public sealed record WorkbenchTaskReference(
    string Key,
    IReadOnlyList<string> Roles,
    string Status,
    string? TaskKey,
    string? Title,
    string? Lane);

public sealed record WorkbenchContextAttachment(
    string ProjectName,
    string Id,
    string Title,
    string DescriptorPath,
    string EntrypointPath,
    string? BriefPath,
    string? Branch,
    string? Revision,
    string ContentFingerprint,
    string ProvenanceState,
    string Status,
    string LifecycleState,
    string? Phase,
    string ContextSourcePath,
    string ContextText,
    WorkbenchPresentationSelection? PresentationSelection,
    IReadOnlyList<WorkbenchTaskReference> TaskReferences,
    IReadOnlyList<string> ValidationFailures,
    IReadOnlyList<string> FreshnessFailures);

public sealed class WorkbenchAttachmentException : InvalidOperationException
{
    private WorkbenchAttachmentException(string code, string message) : base(message) => Code = code;
    public string Code { get; }

    public static WorkbenchAttachmentException Invalid(string message) => new("invalid", message);
    public static WorkbenchAttachmentException NotFound(string message) => new("not-found", message);
    public static WorkbenchAttachmentException Stale(string message) => new("stale", message);
}
