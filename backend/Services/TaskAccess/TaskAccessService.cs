using System.Collections.Concurrent;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.TaskAccess;

/// <summary>
/// Phase 2-4 implementation of <see cref="ITaskAccess"/> and
/// <see cref="ITaskAccessHost"/>. ADR-0024.
///
/// <para>The service is a typed façade in front of the existing
/// <see cref="TaskScannerService"/>, <see cref="TaskMutationService"/>,
/// <see cref="TaskStateMachine"/>, and <see cref="TaskTransitionService"/>:
/// reads bottom out in <see cref="TaskIndexCache"/> (the in-memory index
/// from Cycle 1), writes route through the single-state-machine
/// authority. Outside callers see only the typed surface; this class
/// (and the rest of <c>Services/TaskAccess/</c>) is the one place
/// allowed to construct lane folder paths.</para>
///
/// <para>Subscribe semantics: callers register a per-project callback
/// that fires on every mutation / transition / create / delete. The
/// dispatch is synchronous on the calling thread of the mutation,
/// because subscribers (runner reconcile, SignalR hub) are
/// load-bearingly cheap and must run before the next read. A throwing
/// subscriber is logged and swallowed so one bad listener cannot wedge
/// the layer.</para>
/// </summary>
public sealed class TaskAccessService : ITaskAccess, ITaskAccessHost
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TaskIndexCache _indexCache;
    private readonly LaneMutexRegistry _laneMutex;
    private readonly ILogger<TaskAccessService> _logger;

    private long _snapshotVersion;

    // Per-job optimistic-concurrency counter. Bumped on every successful
    // write. The token consumers see combines this with the on-disk
    // job.json mtime so a writer that picked up a stale snapshot is
    // rejected with Conflict.
    private readonly ConcurrentDictionary<string, long> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-project subscribers. The list is replaced on subscribe /
    // unsubscribe so dispatch can iterate without locking.
    private readonly ConcurrentDictionary<string, ImmutableSubscriberList> _subscribers =
        new(StringComparer.OrdinalIgnoreCase);

    public TaskAccessService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TaskTransitionService transitions,
        TaskIndexCache indexCache,
        ILogger<TaskAccessService> logger,
        LaneMutexRegistry? laneMutex = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _transitions = transitions;
        _indexCache = indexCache;
        _logger = logger;
        // F21: tolerate a missing registry so existing tests that
        // build TaskAccessService directly keep compiling.
        _laneMutex = laneMutex ?? LaneMutexRegistry.NullSingleton;

        // Wire the transition event so lane moves performed via the
        // existing TaskTransitionService (e.g. through the /api/jobs/{id}/move
        // endpoint) are visible to TaskAccess subscribers too. Without
        // this, callers that subscribe to the layer would only see
        // moves that came through TransitionLaneAsync; moves through
        // the older endpoint code path would be invisible.
        _transitions.OnJobMoved += (projectName, jobId, fromLane, toLane) =>
        {
            var info = _scanner.FindJob(jobId);
            var version = BumpVersion(jobId, info);
            DispatchChange(projectName, new TaskChange
            {
                At = DateTime.UtcNow,
                ProjectName = projectName,
                JobId = jobId,
                Kind = TaskChangeKind.Transitioned,
                FromLane = fromLane,
                ToLane = toLane,
                Version = version,
            });
        };
    }

    // ---------- ITaskAccessHost ----------

    /// <summary>
    /// Boot the index. The existing <see cref="TaskIndexCache"/> hydrates
    /// lazily on first read, so the boot is a single forced refresh that
    /// surfaces config / disk problems early instead of on the first
    /// HTTP request.
    /// </summary>
    public Task BootAsync(CancellationToken ct = default)
    {
        try
        {
            _indexCache.ForceRefresh();
            _logger.LogInformation("TaskAccess: index booted with {Count} job(s)", _indexCache.GetSnapshot().Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAccess: boot refresh failed; will lazy-init on first read");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Force a reload. The index is project-agnostic today; reloading one
    /// project still bumps the whole snapshot. Cheap (a single disk walk)
    /// and the API stays per-project so a future implementation can
    /// shard the cache without callers changing.
    /// </summary>
    public Task ReloadProjectAsync(string projectName, CancellationToken ct = default)
    {
        _indexCache.Invalidate(TaskIndexCache.InvalidationSource.External);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ---------- ITaskAccess: reads ----------

    public JobInfo? FindJob(string jobId, string? watchPath = null)
        => _scanner.FindJob(jobId, watchPath);

    public JobDetail? GetJobDetail(string jobId, string? watchPath = null)
        => _scanner.GetJobDetail(jobId, watchPath);

    public IReadOnlyList<JobInfo> ListByLane(string projectName, string lane)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(lane)) return [];
        return _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(j.State, lane, StringComparison.Ordinal))
            .OrderBy(j => j.Order)
            .ToList();
    }

    public IReadOnlyList<JobInfo> ListByLaneInWorkspace(string watchPath, string lane)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(lane)) return [];
        return _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(j.State, lane, StringComparison.Ordinal))
            .OrderBy(j => j.Order)
            .ToList();
    }

    public IReadOnlyList<JobInfo> ListByProject(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return [];
        return _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public TaskAccessSnapshot Snapshot()
    {
        var jobs = _scanner.ScanAllJobs();
        return new TaskAccessSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            Version = Interlocked.Read(ref _snapshotVersion),
            Jobs = jobs,
        };
    }

    // ---------- ITaskAccess: mutations ----------

    public async Task<TaskMutationResult> MutateAsync(TaskMutationRequest request, CancellationToken ct = default)
    {
        if (request == null) return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "request is null" };

        switch (request.Kind)
        {
            case TaskMutationKind.Create:
                return CreateJob(request);

            case TaskMutationKind.UpdateField:
                return UpdateField(request);

            case TaskMutationKind.AttachPrompt:
                return AttachPrompt(request);

            case TaskMutationKind.AppendLogLine:
                return await AppendLogLineAsync(request, ct);

            default:
                return new TaskMutationResult
                {
                    Status = TaskMutationStatus.Rejected,
                    Message = $"Unknown mutation kind: {request.Kind}"
                };
        }
    }

    public async Task<TaskMutationResult> TransitionLaneAsync(TaskTransitionRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.JobId))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "jobId is required" };
        if (string.IsNullOrWhiteSpace(request.TargetLane))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "targetLane is required" };

        var before = _scanner.FindJob(request.JobId, request.WatchPath);
        if (before == null)
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Job '{request.JobId}' not found" };

        if (!IsVersionCurrent(request.JobId, before, request.ExpectedVersion))
        {
            return new TaskMutationResult
            {
                Status = TaskMutationStatus.Conflict,
                Job = before,
                Version = CurrentVersion(request.JobId, before),
                Message = "Stale version; re-read and retry."
            };
        }

        var outcome = await _transitions.MoveAsync(request.JobId, request.TargetLane, request.WatchPath, ct);
        if (outcome.Status == MoveJobStatus.NotFound)
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = outcome.Message };
        if (outcome.Status == MoveJobStatus.TargetFolderExists)
            return new TaskMutationResult { Status = TaskMutationStatus.Conflict, Message = outcome.Message };
        if (outcome.Status != MoveJobStatus.Success)
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = outcome.Message };

        // The transition event fired from TaskTransitionService is wired
        // into our subscriber dispatch in the ctor, so no manual
        // DispatchChange here.
        var after = _scanner.FindJob(request.JobId, request.WatchPath);
        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Job = after,
            Version = CurrentVersion(request.JobId, after),
        };
    }

    private TaskMutationResult CreateJob(TaskMutationRequest request)
    {
        if (request.CreateRequest == null)
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "CreateRequest is required for Create" };

        var jobId = _mutations.CreateJob(request.CreateRequest);
        if (string.IsNullOrEmpty(jobId))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "Create refused (duplicate slug or unknown watchPath)" };

        var info = _scanner.FindJob(jobId, request.CreateRequest.WatchPath);
        var version = BumpVersion(jobId, info);
        if (info != null)
        {
            DispatchChange(info.ProjectName, new TaskChange
            {
                At = DateTime.UtcNow,
                ProjectName = info.ProjectName,
                JobId = jobId,
                Kind = TaskChangeKind.Created,
                ToLane = info.State,
                Version = version,
            });
        }

        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Job = info,
            Version = version,
        };
    }

    private TaskMutationResult UpdateField(TaskMutationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "jobId is required" };
        if (string.IsNullOrWhiteSpace(request.FieldName))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "fieldName is required" };

        var before = _scanner.FindJob(request.JobId, request.WatchPath);
        if (before == null)
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Job '{request.JobId}' not found" };

        if (!IsVersionCurrent(request.JobId, before, request.ExpectedVersion))
        {
            return new TaskMutationResult
            {
                Status = TaskMutationStatus.Conflict,
                Job = before,
                Version = CurrentVersion(request.JobId, before),
                Message = "Stale version; re-read and retry."
            };
        }

        // Route through the typed TaskMutationService method that matches
        // the requested field. Field names are intentionally narrow.
        bool applied;
        switch (request.FieldName)
        {
            case "title": applied = _mutations.SetJobTitle(request.JobId, request.FieldValue ?? "", request.WatchPath); break;
            case "model": applied = _mutations.SetJobModel(request.JobId, request.FieldValue, request.WatchPath); break;
            case "cliType": applied = _mutations.SetJobCliType(request.JobId, request.FieldValue ?? "", request.WatchPath); break;
            case "taskType": applied = _mutations.SetJobTaskType(request.JobId, request.FieldValue ?? "", request.WatchPath); break;
            case "useOwnSession":
                applied = bool.TryParse(request.FieldValue, out var useOwn)
                    && _mutations.SetJobUseOwnSession(request.JobId, useOwn, request.WatchPath);
                break;
            default:
                return new TaskMutationResult
                {
                    Status = TaskMutationStatus.Rejected,
                    Message = $"Field '{request.FieldName}' is not supported via the typed mutation API."
                };
        }

        if (!applied)
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "Mutation refused by storage layer." };

        var after = _scanner.FindJob(request.JobId, request.WatchPath);
        var version = BumpVersion(request.JobId, after);
        if (after != null)
        {
            DispatchChange(after.ProjectName, new TaskChange
            {
                At = DateTime.UtcNow,
                ProjectName = after.ProjectName,
                JobId = request.JobId,
                Kind = TaskChangeKind.Updated,
                Version = version,
            });
        }
        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Job = after,
            Version = version,
        };
    }

    private TaskMutationResult AttachPrompt(TaskMutationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "jobId is required" };

        var before = _scanner.FindJob(request.JobId, request.WatchPath);
        if (before == null)
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Job '{request.JobId}' not found" };

        if (!IsVersionCurrent(request.JobId, before, request.ExpectedVersion))
        {
            return new TaskMutationResult
            {
                Status = TaskMutationStatus.Conflict,
                Job = before,
                Version = CurrentVersion(request.JobId, before),
                Message = "Stale version; re-read and retry."
            };
        }

        if (!_mutations.UpdateJobFile(request.JobId, "prompt.md", request.PromptMarkdown ?? "", request.WatchPath))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "Could not write prompt.md" };

        var after = _scanner.FindJob(request.JobId, request.WatchPath);
        var version = BumpVersion(request.JobId, after);
        if (after != null)
        {
            DispatchChange(after.ProjectName, new TaskChange
            {
                At = DateTime.UtcNow,
                ProjectName = after.ProjectName,
                JobId = request.JobId,
                Kind = TaskChangeKind.Updated,
                Version = version,
            });
        }
        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Job = after,
            Version = version,
        };
    }

    private Task<TaskMutationResult> AppendLogLineAsync(TaskMutationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
            return Task.FromResult(new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "jobId is required" });
        if (string.IsNullOrWhiteSpace(request.LogLine))
            return Task.FromResult(new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "logLine is required" });

        var info = _scanner.FindJob(request.JobId, request.WatchPath);
        if (info == null)
            return Task.FromResult(new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Job '{request.JobId}' not found" });

        try
        {
            var logsDir = Path.Combine(info.FolderPath, "logs");
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, "cli-output.log");
            File.AppendAllText(logPath, request.LogLine!.EndsWith('\n') ? request.LogLine : request.LogLine + "\n");
            // No JobInfo field changes; skip cache invalidation and
            // version bump. Subscribers don't need a log-line tick.
            return Task.FromResult(new TaskMutationResult
            {
                Status = TaskMutationStatus.Applied,
                Job = info,
                Version = CurrentVersion(request.JobId, info),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAccess: AppendLogLine failed for {JobId}", request.JobId);
            return Task.FromResult(new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = ex.Message });
        }
    }

    // ---------- ITaskAccess: subscribe ----------

    public IDisposable Subscribe(string projectName, Action<TaskChange> callback)
    {
        if (string.IsNullOrWhiteSpace(projectName) || callback == null)
            return new NoopDisposable();

        _subscribers.AddOrUpdate(
            projectName,
            _ => new ImmutableSubscriberList([callback]),
            (_, current) => current.With(callback));

        return new SubscriberHandle(this, projectName, callback);
    }

    private void Unsubscribe(string projectName, Action<TaskChange> callback)
    {
        if (_subscribers.TryGetValue(projectName, out var list))
        {
            var updated = list.Without(callback);
            if (updated.Callbacks.Count == 0) _subscribers.TryRemove(projectName, out _);
            else _subscribers[projectName] = updated;
        }
    }

    private void DispatchChange(string projectName, TaskChange change)
    {
        Interlocked.Increment(ref _snapshotVersion);
        if (!_subscribers.TryGetValue(projectName, out var list)) return;
        foreach (var cb in list.Callbacks)
        {
            try { cb(change); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TaskAccess subscriber threw for {Project} / {JobId}", projectName, change.JobId);
            }
        }
    }

    // ---------- ITaskAccess: lane-folder escape hatches ----------

    public bool SlugExistsInLane(string watchPath, string lane, string slug)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(slug)) return false;
        if (!TaskStates.All.Contains(lane)) return false;
        var path = Path.Combine(watchPath, lane, slug);
        return Directory.Exists(path);
    }

    public IReadOnlyList<string> ListLaneFolderNames(string watchPath, string lane)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(lane)) return [];
        if (!TaskStates.All.Contains(lane)) return [];
        var laneDir = Path.Combine(watchPath, lane);
        if (!Directory.Exists(laneDir)) return [];
        try
        {
            return Directory.EnumerateDirectories(laneDir).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAccess.ListLaneFolderNames failed for {Path}/{Lane}", watchPath, lane);
            return [];
        }
    }

    public IReadOnlyList<LaneFolderRef> ListLaneFolders(string watchPath, string lane)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(lane)) return [];
        if (!TaskStates.All.Contains(lane)) return [];
        var laneDir = Path.Combine(watchPath, lane);
        if (!Directory.Exists(laneDir)) return [];
        try
        {
            return Directory.EnumerateDirectories(laneDir)
                .Select(folder => new LaneFolderRef
                {
                    WatchPath = watchPath,
                    Lane = lane,
                    Slug = Path.GetFileName(folder) ?? "",
                    FolderPath = folder,
                })
                .Where(r => !string.IsNullOrEmpty(r.Slug))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAccess.ListLaneFolders failed for {Path}/{Lane}", watchPath, lane);
            return [];
        }
    }

    public IReadOnlyList<LaneFolderEntry> ListAllLaneFolders(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return [];
        var results = new List<LaneFolderEntry>();
        foreach (var lane in TaskStates.All)
        {
            var laneDir = Path.Combine(watchPath, lane);
            if (!Directory.Exists(laneDir)) continue;
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(laneDir); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TaskAccess.ListAllLaneFolders: enumeration failed for {Lane}", lane);
                continue;
            }
            foreach (var folder in folders)
            {
                var slug = Path.GetFileName(folder);
                if (string.IsNullOrEmpty(slug)) continue;
                var jobJson = Path.Combine(folder, "job.json");
                var hasJobJson = File.Exists(jobJson);
                string? stateInJobJson = null;
                if (hasJobJson)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(jobJson));
                        if (doc.RootElement.TryGetProperty("state", out var stateEl))
                        {
                            stateInJobJson = stateEl.GetString();
                        }
                    }
                    catch
                    {
                        stateInJobJson = "(unreadable)";
                    }
                }
                results.Add(new LaneFolderEntry
                {
                    WatchPath = watchPath,
                    Lane = lane,
                    Slug = slug,
                    FolderPath = folder,
                    HasJobJson = hasJobJson,
                    StateInJobJson = stateInJobJson,
                });
            }
        }
        return results;
    }

    public string? GetJobFolderPath(string jobId, string? watchPath = null)
        => _scanner.FindJob(jobId, watchPath)?.FolderPath;

    public TaskMutationResult MoveOrphanToFailedPickup(
        string watchPath,
        string sourceLane,
        string sourceSlug,
        string destinationSlug,
        string? reasonMarkdown)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(sourceLane) || string.IsNullOrWhiteSpace(sourceSlug) || string.IsNullOrWhiteSpace(destinationSlug))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "watchPath, sourceLane, sourceSlug, destinationSlug are required" };
        if (!TaskStates.All.Contains(sourceLane))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = $"Unknown source lane '{sourceLane}'" };

        var sourceFolder = Path.Combine(watchPath, sourceLane, sourceSlug);
        if (!Directory.Exists(sourceFolder))
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Source folder '{sourceLane}/{sourceSlug}' not found" };

        var outcome = _states.MoveFolderToFailedPickup(sourceFolder, destinationSlug);
        if (outcome.Status == MoveJobStatus.TargetFolderExists)
            return new TaskMutationResult { Status = TaskMutationStatus.Conflict, Message = outcome.Message };
        if (outcome.Status == MoveJobStatus.NotFound)
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = outcome.Message };
        if (outcome.Status != MoveJobStatus.Success)
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = outcome.Message };

        if (!string.IsNullOrEmpty(reasonMarkdown))
        {
            try
            {
                var reasonPath = Path.Combine(watchPath, TaskStates.FailedPickup, destinationSlug, "failed-pickup-reason.md");
                File.WriteAllText(reasonPath, reasonMarkdown);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TaskAccess.MoveOrphanToFailedPickup: failed to write reason for {Slug}", destinationSlug);
            }
        }

        // Intentionally skip a post-move scanner.FindJob lookup here.
        // Forcing a fresh ScanAllJobsRaw on every orphan move has a
        // load-bearing side effect: ScanJobFolder runs the lazy
        // ownerClientId migration, which calls TaskJsonFile.UpdateField
        // on every legacy job.json it touches. That mtime bump on
        // unrelated sibling folders (e.g. another 3-progress folder
        // still queued for the same sweep) made them look freshly
        // active to the next MeasureFolder call and broke the
        // boot-time sweep's idempotency. The DispatchChange below
        // raises a typed event with a synthetic Version - subscribers
        // that need the JobInfo can call FindJob explicitly.
        var version = BumpVersion(destinationSlug, info: null);
        // Resolve a project name from the watch path so subscribers
        // wired by project still see the change. Avoid scanner.FindJob
        // (see above); the watch-path entry list is config, not a
        // job-folder read.
        var projectName = _scanner.GetWatchPaths()
            .FirstOrDefault(w => string.Equals(w.Path, watchPath, StringComparison.OrdinalIgnoreCase))?.Name ?? string.Empty;
        if (!string.IsNullOrEmpty(projectName))
        {
            DispatchChange(projectName, new TaskChange
            {
                At = DateTime.UtcNow,
                ProjectName = projectName,
                JobId = destinationSlug,
                Kind = TaskChangeKind.Transitioned,
                FromLane = sourceLane,
                ToLane = TaskStates.FailedPickup,
                Version = version,
            });
        }
        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Job = null,
            Version = version,
        };
    }

    public TaskMutationResult DeleteLaneFolder(string watchPath, string lane, string slug)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(slug))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = "watchPath, lane, slug are required" };
        if (!TaskStates.All.Contains(lane))
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = $"Unknown lane '{lane}'" };

        // F21: serialise with the lane-tree writer chain (TaskStateMachine
        // and friends). The post-move skeleton cleanup in ProjectRunner is
        // the canonical caller; without the mutex, the runner could try to
        // delete a folder that StaleProgressArchiver or a manual API move
        // is in the middle of renaming.
        using var _ = _laneMutex.Acquire(watchPath);

        var folder = Path.Combine(watchPath, lane, slug);
        if (!Directory.Exists(folder))
            return new TaskMutationResult { Status = TaskMutationStatus.NotFound, Message = $"Folder '{lane}/{slug}' not found" };

        try
        {
            Directory.Delete(folder, recursive: true);
            _scanner.InvalidateCache();
        }
        catch (IOException ioex)
        {
            // Windows file-handle race: leave the folder and let the caller decide whether to retry.
            return new TaskMutationResult { Status = TaskMutationStatus.Conflict, Message = ioex.Message };
        }
        catch (UnauthorizedAccessException uaex)
        {
            return new TaskMutationResult { Status = TaskMutationStatus.Rejected, Message = uaex.Message };
        }

        return new TaskMutationResult
        {
            Status = TaskMutationStatus.Applied,
            Message = $"Deleted {lane}/{slug}",
        };
    }

    public bool WriteJobTextFile(string jobId, string? watchPath, string fileName, string content)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(fileName)) return false;
        // Reject any path-traversal or separator characters; this is a
        // typed write of one file inside the job folder, not arbitrary
        // file IO.
        if (fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        try
        {
            File.WriteAllText(Path.Combine(info.FolderPath, fileName), content);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAccess.WriteJobTextFile failed for {JobId}/{File}", jobId, fileName);
            return false;
        }
    }

    // ---------- versioning ----------

    private TaskAccessVersion CurrentVersion(string jobId, JobInfo? info)
    {
        var v = _versions.GetOrAdd(jobId, _ => 0);
        var mtime = info?.LastActivity ?? DateTime.MinValue;
        return new TaskAccessVersion(v, mtime);
    }

    private TaskAccessVersion BumpVersion(string jobId, JobInfo? info)
    {
        var v = _versions.AddOrUpdate(jobId, 1, (_, current) => current + 1);
        var mtime = info?.LastActivity ?? DateTime.UtcNow;
        return new TaskAccessVersion(v, mtime);
    }

    private bool IsVersionCurrent(string jobId, JobInfo? info, TaskAccessVersion? expected)
    {
        if (expected == null) return true; // caller did not opt into optimistic concurrency
        var current = CurrentVersion(jobId, info);
        return current.Version == expected.Version;
    }

    // ---------- subscriber bookkeeping ----------

    private sealed record ImmutableSubscriberList(IReadOnlyList<Action<TaskChange>> Callbacks)
    {
        public ImmutableSubscriberList With(Action<TaskChange> callback)
        {
            var next = new List<Action<TaskChange>>(Callbacks.Count + 1);
            next.AddRange(Callbacks);
            next.Add(callback);
            return new ImmutableSubscriberList(next);
        }

        public ImmutableSubscriberList Without(Action<TaskChange> callback)
        {
            var next = new List<Action<TaskChange>>(Callbacks.Count);
            foreach (var cb in Callbacks)
            {
                if (!ReferenceEquals(cb, callback)) next.Add(cb);
            }
            return new ImmutableSubscriberList(next);
        }
    }

    private sealed class SubscriberHandle : IDisposable
    {
        private readonly TaskAccessService _owner;
        private readonly string _projectName;
        private readonly Action<TaskChange> _callback;
        private int _disposed;

        public SubscriberHandle(TaskAccessService owner, string projectName, Action<TaskChange> callback)
        {
            _owner = owner;
            _projectName = projectName;
            _callback = callback;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Unsubscribe(_projectName, _callback);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
