using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.ProjectChat;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Per-project conversation log between the user and the (global) orchestrator
/// session. Lives next to the per-project orchestrator log as
/// <c>&lt;watchPath&gt;/.orchestrator/orchestrator-chat.jsonl</c>: one JSONL
/// turn per line, oldest first, tolerant to torn writes.
///
/// <para>
/// This is the storage layer for Phase 3 of the side-sheet chat. Phase 2
/// re-used the existing override endpoint to steer the *agent* on an
/// already-running task; Phase 3 introduces a real bidirectional chat with
/// the *orchestrator itself*: the user asks "where do you stand?", the
/// orchestrator replies, the dialogue accumulates. Conversation memory
/// lives in the global Claude session (resumed via <c>-r &lt;sessionId&gt;</c>
/// on each turn), so the only on-disk state we need is the audit log of
/// what was said when.
/// </para>
/// </summary>
public class OrchestratorChat
{
    private readonly ILogger<OrchestratorChat> _logger;
    private readonly ProjectChatStore? _projectStore;
    private readonly ProjectChatIndex? _projectIndex;
    private readonly JobScannerService? _scanner;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorChat(
        ILogger<OrchestratorChat> logger,
        ProjectChatStore? projectStore = null,
        ProjectChatIndex? projectIndex = null,
        JobScannerService? scanner = null)
    {
        _logger = logger;
        _projectStore = projectStore;
        _projectIndex = projectIndex;
        _scanner = scanner;
    }

    public bool Append(string watchPath, OrchestratorChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return false;
        try
        {
            var path = ResolvePath(watchPath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(turn, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);

            // Slice D mirror: also write the per-turn markdown file so the
            // new file-tree + FTS index stay current as turns are appended.
            // Best-effort; legacy JSONL remains the fallback if this fails.
            MirrorToProjectChat(watchPath, turn);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append orchestrator chat turn under {WatchPath}", watchPath);
            return false;
        }
    }

    private void MirrorToProjectChat(string watchPath, OrchestratorChatTurn turn)
    {
        if (_projectStore == null || _scanner == null) return;
        try
        {
            var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.RootPath, watchPath, StringComparison.OrdinalIgnoreCase));
            var projectFolder = entry?.Path;
            if (string.IsNullOrWhiteSpace(projectFolder)) return;

            var author = turn.Role switch
            {
                "user" => ProjectChatTurnAuthors.User,
                "orchestrator" => ProjectChatTurnAuthors.Orchestrator,
                _ => ProjectChatTurnAuthors.Orchestrator
            };
            var body = turn.Text ?? "";
            if (!string.IsNullOrWhiteSpace(turn.ErrorMessage))
            {
                body = string.IsNullOrEmpty(body)
                    ? "_error:_ " + turn.ErrorMessage
                    : body + "\n\n_error:_ " + turn.ErrorMessage;
            }

            var pTurn = new ProjectChatTurn
            {
                TurnId = turn.Id,
                Author = author,
                Kind = ProjectChatTurnKinds.Turn,
                Ts = DateTime.SpecifyKind(turn.Ts, DateTimeKind.Utc),
                Body = body
            };
            var written = _projectStore.Write(projectFolder, pTurn);
            _projectIndex?.Upsert(projectFolder, pTurn, written);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mirror to project chat tree failed for {WatchPath}", watchPath);
        }
    }

    public List<OrchestratorChatTurn> Read(string watchPath)
    {
        var result = new List<OrchestratorChatTurn>();
        if (string.IsNullOrWhiteSpace(watchPath)) return result;
        var path = ResolvePath(watchPath);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var turn = JsonSerializer.Deserialize<OrchestratorChatTurn>(line, ReadOpts);
                if (turn != null) result.Add(turn);
            }
            catch
            {
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    private static string ResolvePath(string watchPath) =>
        Path.Combine(watchPath, ".orchestrator", "orchestrator-chat.jsonl");

    /// <summary>
    /// Persist a chat-composer image under
    /// <c>&lt;watchPath&gt;/.orchestrator/chat-attachments/&lt;id&gt;.&lt;ext&gt;</c>.
    /// Mirrors the per-job <c>attachments/</c> conventions (10 MB cap,
    /// PNG / JPG / GIF / WEBP only) so the user's chat drafts and task
    /// drafts behave the same way.
    /// </summary>
    public (string? FileName, string? RelativePath, string? Error) SaveAttachment(
        string watchPath,
        byte[] content,
        string? originalFileName,
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return (null, null, "Missing watch path");
        if (content.Length == 0) return (null, null, "Empty file");
        if (content.Length > 10 * 1024 * 1024) return (null, null, "File too large (max 10 MB)");

        var ext = ResolveImageExtension(originalFileName, contentType);
        if (ext == null) return (null, null, "Unsupported file type - only png, jpg, gif, webp allowed");

        var dir = Path.Combine(watchPath, ".orchestrator", "chat-attachments");
        Directory.CreateDirectory(dir);

        string fileName;
        string fullPath;
        do
        {
            fileName = $"{Guid.NewGuid():N}"[..8] + ext;
            fullPath = Path.Combine(dir, fileName);
        } while (File.Exists(fullPath));

        File.WriteAllBytes(fullPath, content);
        return (fileName, $"chat-attachments/{fileName}", null);
    }

    /// <summary>
    /// Resolve a previously-saved chat attachment for serving back to the
    /// frontend. Returns null if the file is gone or escapes the chat
    /// attachments directory.
    /// </summary>
    public (string? Path, string? ContentType) ResolveAttachment(string watchPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(fileName)) return (null, null);
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return (null, null);

        var dir = Path.Combine(watchPath, ".orchestrator", "chat-attachments");
        var full = Path.Combine(dir, fileName);
        if (!File.Exists(full)) return (null, null);
        var ct = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        return (full, ct);
    }

    private static string? ResolveImageExtension(string? originalFileName, string? contentType)
    {
        var ext = string.IsNullOrWhiteSpace(originalFileName)
            ? null
            : Path.GetExtension(originalFileName).ToLowerInvariant();

        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp") return ext == ".jpeg" ? ".jpg" : ext;

        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };
    }
}

/// <summary>
/// One turn in the per-project orchestrator chat. <see cref="Role"/> is
/// "user" for the human's messages and "orchestrator" for the model's
/// replies; <see cref="ErrorMessage"/> is set on a failed turn so the
/// frontend can surface what went wrong without losing the user's text.
/// </summary>
public record OrchestratorChatTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Ts { get; init; } = DateTime.UtcNow;
    public string Role { get; init; } = OrchestratorChatRoles.User;
    public string Text { get; init; } = "";
    public string? Model { get; init; }
    public OrchestratorTokenUsage? TokenUsage { get; init; }
    public string? ErrorMessage { get; init; }
    public List<OrchestratorChatAttachment>? Attachments { get; init; }
}

/// <summary>
/// Reference to a file attachment that was part of the user's message.
/// Today the only carrier is an image stored in the watch path's
/// <c>.orchestrator/attachments/</c> folder; <see cref="RelativePath"/> is
/// the path the frontend resolves through the existing image-serving route.
/// </summary>
public record OrchestratorChatAttachment
{
    public string Alt { get; init; } = "";
    public string RelativePath { get; init; } = "";
}

public static class OrchestratorChatRoles
{
    public const string User = "user";
    public const string Orchestrator = "orchestrator";
}

public sealed record SendOrchestratorChatRequest(
    string Text,
    List<OrchestratorChatAttachment>? Attachments);

/// <summary>
/// Service that turns a user message into an orchestrator reply by resuming
/// the singleton global Claude session, persisting both turns to the
/// per-project chat log, and accumulating the call into the global session
/// usage record.
///
/// <para>
/// Why we resume the global session and not boot a per-project one:
/// the global orchestrator already knows the whole watched-project
/// landscape and is the one source the user actively talks to. Spinning
/// up another session per project would 1) duplicate the boot cost,
/// 2) split the orchestrator's memory across N sessions, 3) make
/// "what does the orchestrator know about my project?" depend on which
/// project tab the user happened to open. A single session that the
/// per-project chats prefix with project context keeps the mental model
/// simple.
/// </para>
/// </summary>
public class OrchestratorChatService
{
    private readonly OrchestratorChat _chat;
    private readonly OrchestratorRunner _runner;
    private readonly GlobalOrchestratorSessionStore _sessionStore;
    private readonly JobScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<OrchestratorChatService> _logger;

    /// <summary>
    /// Serializes concurrent <see cref="SendAsync"/> calls because the
    /// underlying Claude session is a singleton: the resume id, the
    /// on-disk session usage record, and the CLI's session memory are
    /// shared. Two parallel <c>claude -r &lt;sessionId&gt;</c> invocations
    /// race on session state and on the JSON write at
    /// <see cref="UpdateSessionUsage"/>; the user-visible failure mode is
    /// "sent a message in tab B while tab A was still thinking, neither
    /// reply arrived cleanly". The semaphore makes the queueing explicit
    /// and bounded - the user still sees their pending turn in the UI.
    ///
    /// <para>
    /// This is a pragmatic correctness guard, not a parallelism win:
    /// requests across all projects serialize on a single global session.
    /// Real cross-project parallelism would need per-project (or
    /// per-conversation) sessions and is out of scope for this fix.
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    public OrchestratorChatService(
        OrchestratorChat chat,
        OrchestratorRunner runner,
        GlobalOrchestratorSessionStore sessionStore,
        JobScannerService scanner,
        IConfiguration config,
        ILogger<OrchestratorChatService> logger)
    {
        _chat = chat;
        _runner = runner;
        _sessionStore = sessionStore;
        _scanner = scanner;
        _config = config;
        _logger = logger;
    }

    public List<OrchestratorChatTurn> Read(string watchPath) => _chat.Read(watchPath);

    public async Task<OrchestratorChatTurn> SendAsync(
        string projectName,
        string watchPath,
        SendOrchestratorChatRequest req,
        CancellationToken ct)
    {
        // Append the user turn outside the gate so the audit log records
        // the inbound message even if the user cancels while queued.
        var userTurn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.User,
            Text = req.Text,
            Attachments = req.Attachments
        };
        _chat.Append(watchPath, userTurn);

        // Serialize on the singleton-session gate. Two concurrent resumes
        // race on the session id, the on-disk usage record, and Claude's
        // own session memory; the gate is the simplest correctness fix
        // until per-conversation sessions land. The wait counter tells us
        // when chats are actually queueing in the wild.
        var queuedAt = DateTime.UtcNow;
        await SessionGate.WaitAsync(ct);
        var queueWaitMs = (DateTime.UtcNow - queuedAt).TotalMilliseconds;
        if (queueWaitMs > 250)
        {
            _logger.LogInformation(
                "[orchestrator-chat] queued {WaitMs:F0}ms behind another in-flight chat for project {Project}",
                queueWaitMs, projectName);
        }
        try
        {
            var session = _sessionStore.Read();
            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
            {
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = "",
                    ErrorMessage = "Global orchestrator session has not booted yet. Try again in a moment, or check the backend logs."
                };
                _chat.Append(watchPath, failure);
                return failure;
            }

            var prompt = BuildPrompt(projectName, watchPath, req);
            var modelId = _config["GlobalOrchestrator:Model"] ?? OrchestratorRunner.DefaultModel;
            var workingDirectory = ResolveWorkingDirectory(watchPath);

            OrchestratorDecisionResult result;
            try
            {
                result = await _runner.ResumeAsync(session.SessionId, prompt, modelId, workingDirectory, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orchestrator chat resume failed for project {Project}", projectName);
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = "",
                    ErrorMessage = ex.Message
                };
                _chat.Append(watchPath, failure);
                return failure;
            }

            if (!result.Success)
            {
                var failure = new OrchestratorChatTurn
                {
                    Role = OrchestratorChatRoles.Orchestrator,
                    Text = result.ReplyText ?? "",
                    Model = result.Model,
                    TokenUsage = result.TokenUsage,
                    ErrorMessage = result.ErrorMessage ?? "Orchestrator reply was empty."
                };
                _chat.Append(watchPath, failure);
                UpdateSessionUsage(session, result);
                return failure;
            }

            var reply = new OrchestratorChatTurn
            {
                Role = OrchestratorChatRoles.Orchestrator,
                Text = result.ReplyText,
                Model = result.Model,
                TokenUsage = result.TokenUsage
            };
            _chat.Append(watchPath, reply);
            UpdateSessionUsage(session, result);
            return reply;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private void UpdateSessionUsage(GlobalOrchestratorSession previous, OrchestratorDecisionResult result)
    {
        try
        {
            var next = GlobalOrchestratorSessionStore.AccumulateUsage(previous, result.TokenUsage, result.ErrorMessage);
            _sessionStore.Write(next);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update global orchestrator session usage after chat turn");
        }
    }

    private string BuildPrompt(string projectName, string watchPath, SendOrchestratorChatRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ACTIVE PROJECT CONTEXT ===");
        sb.AppendLine($"The user is currently looking at project: \"{projectName}\"");
        sb.AppendLine("This may be a different project than the one discussed earlier in this session.");
        sb.AppendLine("Answer ONLY about \"" + projectName + "\". Do not refer to other projects unless the user asks.");
        sb.AppendLine();

        // Project-state snapshot so the orchestrator answers "where do you
        // stand on this project?" against current reality, not stale memory.
        // The global session is shared across projects, so prior turns may
        // have cached counts for a different project; mark this snapshot
        // authoritative so the model uses it instead of recalling stale data.
        try
        {
            var tasks = _scanner.ScanAllJobs()
                .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sb.AppendLine($"AUTHORITATIVE current state of \"{projectName}\" ({tasks.Count} tasks total):");
            if (tasks.Count == 0)
            {
                sb.AppendLine("  (no tasks)");
            }
            else
            {
                foreach (var sg in tasks.GroupBy(j => j.State).OrderBy(g => g.Key))
                {
                    sb.AppendLine($"  {sg.Key}: {sg.Count()}");
                }
            }
            sb.AppendLine("Use these exact numbers. Any counts you remember from earlier in this session are stale and must be ignored.");
            sb.AppendLine("These items are called \"tasks\" (not \"jobs\") in the user-facing vocabulary.");
            sb.AppendLine();
        }
        catch
        {
            // Best-effort: missing snapshot is fine; the orchestrator can
            // still answer general questions from session memory.
        }

        sb.AppendLine("=== USER MESSAGE ===");
        sb.AppendLine(req.Text);
        sb.AppendLine();

        if (req.Attachments != null && req.Attachments.Count > 0)
        {
            sb.AppendLine($"User attached {req.Attachments.Count} image(s):");
            foreach (var a in req.Attachments)
            {
                sb.AppendLine($"- {a.Alt} ({a.RelativePath})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Reply directly to the user about \"" + projectName + "\". Be concrete and specific.");
        sb.AppendLine("Use the word \"tasks\", not \"jobs\".");
        sb.AppendLine("Use Markdown for structure when helpful (lists, bold, code).");
        sb.AppendLine("Keep it short unless the user asked for depth.");
        return sb.ToString();
    }

    private string ResolveWorkingDirectory(string watchPath)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(entry?.RootPath)
            ? entry!.RootPath
            : Path.GetTempPath();
    }
}
