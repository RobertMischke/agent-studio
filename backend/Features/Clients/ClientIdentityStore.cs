using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Clients;

/// <summary>
/// Single writer for client identity files under
/// <c>&lt;TaskRepository&gt;/identities/&lt;id&gt;.json</c>. Loads every file
/// on first use into an in-memory dictionary keyed by id and serves
/// register / get / list / soft-delete + an authoritative
/// <see cref="IsRegistered"/> check used by the X-Client-Id middleware.
///
/// Concurrency: single-process backend, all access goes through one
/// instance; a single lock around the dictionary is sufficient. The disk
/// reflection is the source of truth on cold start.
/// </summary>
public class ClientIdentityStore
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex SlugAllowed = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex SlugSanitiser = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly IConfiguration _config;
    private readonly ILogger<ClientIdentityStore> _logger;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly object _lock = new();
    private readonly Dictionary<string, ClientIdentity> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClientIdentity> _persistedById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClientIdentityDiagnostic> _diagnosticsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdentityFileFingerprint> _lastReadById = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ClientIdentityStore(
        IConfiguration config,
        ILogger<ClientIdentityStore> logger,
        IAtomicJsonFileWriter? fileWriter = null)
    {
        _config = config;
        _logger = logger;
        _fileWriter = fileWriter ?? new AtomicJsonFileWriter();
    }

    public string IdentitiesFolder
    {
        get
        {
            var repo = _config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(repo))
            {
                // Fall back to a backend-local folder when no central repository
                // is configured. Keeps the dev backend bootable without a workspace.
                repo = Path.Combine(AppContext.BaseDirectory, "workspace");
            }
            return Path.Combine(repo, "identities");
        }
    }

    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            LoadFromDiskLocked();
            EnsureBootstrapDefaultLocked();
            _loaded = true;
        }
    }

    private void LoadFromDiskLocked()
    {
        var dir = IdentitiesFolder;
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create identities folder {Dir}; identities will live in memory only this run", dir);
                return;
            }
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            TryLoadIdentityFileLocked(file, logFailure: true);

        _logger.LogInformation(
            "Loaded {Count} client identities from {Dir}; corrupt={CorruptCount}",
            _byId.Count,
            dir,
            _diagnosticsById.Count);
    }

    private bool TryLoadIdentityFileLocked(string file, bool logFailure)
    {
        var identityId = Path.GetFileNameWithoutExtension(file);
        try
        {
            var json = File.ReadAllText(file);
            var record = JsonSerializer.Deserialize<ClientIdentity>(json, ReadOpts);
            if (record is null || string.IsNullOrWhiteSpace(record.Id))
                throw new JsonException("The identity record is null or has no id.");

            _byId[record.Id] = record;
            _persistedById[record.Id] = record;
            _lastReadById[identityId] = Fingerprint(file);
            var recovered = _diagnosticsById.Remove(identityId);
            if (!string.Equals(identityId, record.Id, StringComparison.OrdinalIgnoreCase))
                _diagnosticsById.Remove(record.Id);
            if (recovered)
            {
                _logger.LogInformation(
                    "Client identity corruption recovered: {File} loaded as {IdentityId}",
                    Path.GetFileName(file),
                    record.Id);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var fingerprint = FingerprintOrDefault(file);
            _lastReadById[identityId] = fingerprint;
            var restoreHint = RestoreHint(file, identityId);
            var diagnostic = new ClientIdentityDiagnostic
            {
                IdentityId = identityId,
                FileName = Path.GetFileName(file),
                DetectedAt = _diagnosticsById.GetValueOrDefault(identityId)?.DetectedAt ?? DateTime.UtcNow,
                ModifiedAt = fingerprint.Exists ? fingerprint.LastWriteTimeUtc : null,
                Message = $"identity file corrupt: {Path.GetFileName(file)}",
                RestoreHint = restoreHint,
            };
            _diagnosticsById[identityId] = diagnostic;
            if (logFailure)
            {
                _logger.LogError(
                    ex,
                    "Client identity file corrupt: {File}. {RestoreHint}",
                    file,
                    restoreHint);
            }
            return false;
        }
    }

    /// <summary>
    /// A missing in-memory identity gets one bounded disk re-check whenever the
    /// target file's length or write timestamp changes. This lets a restored
    /// file become live without a backend restart and avoids watcher complexity.
    /// </summary>
    private void TryReloadMissingIdentityLocked(string id)
    {
        if (_byId.ContainsKey(id) || !SlugAllowed.IsMatch(id)) return;
        var file = Path.Combine(IdentitiesFolder, id + ".json");
        if (!File.Exists(file)) return;
        var fingerprint = FingerprintOrDefault(file);
        if (_lastReadById.TryGetValue(id, out var previous) && previous == fingerprint) return;
        TryLoadIdentityFileLocked(file, logFailure: true);
    }

    /// <summary>
    /// The normal clients-list refresh is an explicit, bounded re-scan of only
    /// files already known to be corrupt. It is intentionally not a watcher.
    /// </summary>
    private void RescanCorruptIdentitiesLocked()
    {
        foreach (var diagnostic in _diagnosticsById.Values.ToList())
        {
            var file = Path.Combine(IdentitiesFolder, diagnostic.FileName);
            var current = FingerprintOrDefault(file);
            var changed = !_lastReadById.TryGetValue(diagnostic.IdentityId, out var previous)
                          || previous != current;
            TryLoadIdentityFileLocked(file, logFailure: changed);
        }
    }

    private static string RestoreHint(string file, string identityId)
    {
        var backup = file + ".bak";
        var backupHint = File.Exists(backup)
            ? $"Restore the known-good sibling '{Path.GetFileName(backup)}' over this file, then reload the clients list. "
            : "Restore the file from a known-good backup or Git revision, then reload the clients list. ";
        return backupHint
               + $"The running server will re-scan the repaired identity '{identityId}'. "
               + "Alternatively, POST /api/clients/register with the original displayName; registration is idempotent on displayName and replaces the corrupt slug.";
    }

    private static IdentityFileFingerprint Fingerprint(string file)
    {
        var info = new FileInfo(file);
        return new IdentityFileFingerprint(true, info.Length, info.LastWriteTimeUtc);
    }

    private static IdentityFileFingerprint FingerprintOrDefault(string file)
    {
        try { return File.Exists(file) ? Fingerprint(file) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private void EnsureBootstrapDefaultLocked()
    {
        if (_byId.ContainsKey(DefaultClientIdentity.Id)) return;
        if (_diagnosticsById.ContainsKey(DefaultClientIdentity.Id))
        {
            _logger.LogError(
                "Bootstrap identity file is corrupt; refusing to overwrite it automatically. {RestoreHint}",
                _diagnosticsById[DefaultClientIdentity.Id].RestoreHint);
            return;
        }

        var displayName = _config["Environment:DefaultIdentityName"];
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Local Default";
        var emoji = _config["Environment:DefaultIdentityEmoji"];
        if (string.IsNullOrWhiteSpace(emoji)) emoji = "🦊";

        var bootstrap = new ClientIdentity
        {
            Id = DefaultClientIdentity.Id,
            DisplayName = displayName,
            Emoji = emoji,
            Kind = ClientIdentityKind.Human,
            RegisteredAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            Notes = "Bootstrap identity created on first boot. Every existing job inherits this id on first migration."
        };

        WriteLocked(bootstrap);
        _byId[bootstrap.Id] = bootstrap;
        _logger.LogInformation("Bootstrap identity '{Id}' ({Name}) created", bootstrap.Id, bootstrap.DisplayName);
    }

    public bool IsRegistered(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        EnsureLoaded();
        lock (_lock)
        {
            TryReloadMissingIdentityLocked(clientId);
            return _byId.TryGetValue(clientId, out var rec) && rec.Kind != ClientIdentityKind.Retired;
        }
    }

    public ClientIdentity? Find(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            TryReloadMissingIdentityLocked(id);
            return _byId.TryGetValue(id, out var rec) ? rec : null;
        }
    }

    public List<ClientIdentity> ListAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            RescanCorruptIdentitiesLocked();
            return _byId.Values.OrderBy(c => c.RegisteredAt).ToList();
        }
    }

    /// <summary>
    /// Returns every corrupt identity file discovered at boot that has not
    /// subsequently been restored. The clients list projects these as explicit
    /// diagnostic entries instead of silently dropping them.
    /// </summary>
    public List<ClientIdentityDiagnostic> ListDiagnostics()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _diagnosticsById.Values
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public ClientIdentityDiagnostic? FindDiagnostic(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            TryReloadMissingIdentityLocked(id);
            return _diagnosticsById.GetValueOrDefault(id);
        }
    }

    public ClientIdentity Register(RegisterClientRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("displayName is required");

        EnsureLoaded();

        lock (_lock)
        {
            // Idempotent on displayName: re-registering the same name returns
            // the same id. Match case-insensitively to absorb casual variations.
            var existing = _byId.Values.FirstOrDefault(c =>
                string.Equals(c.DisplayName, request.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // Allow re-registration to refresh emoji / colour / notes / budget /
                // kind (except retired -> live, which requires explicit revival).
                var revived = existing.Kind == ClientIdentityKind.Retired
                    ? existing with { Kind = ClientIdentityKind.Human, LastSeenAt = DateTime.UtcNow }
                    : existing;
                var refreshed = revived with
                {
                    Emoji = request.Emoji ?? existing.Emoji,
                    Colour = request.Colour ?? existing.Colour,
                    Kind = !string.IsNullOrWhiteSpace(request.Kind) ? ClientIdentityKinds.Parse(request.Kind) : revived.Kind,
                    TokenBudgetMonthly = request.TokenBudgetMonthly ?? existing.TokenBudgetMonthly,
                    Notes = request.Notes ?? existing.Notes,
                    LastSeenAt = DateTime.UtcNow
                };
                WriteLocked(refreshed);
                _byId[refreshed.Id] = refreshed;
                return refreshed;
            }

            var id = AssignSlug(request.DisplayName);
            var record = new ClientIdentity
            {
                Id = id,
                DisplayName = request.DisplayName,
                Emoji = request.Emoji,
                Colour = request.Colour,
                Kind = ClientIdentityKinds.Parse(request.Kind),
                RegisteredAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                TokenBudgetMonthly = request.TokenBudgetMonthly,
                Notes = request.Notes
            };
            WriteLocked(record);
            _byId[id] = record;
            _logger.LogInformation("Registered client identity '{Id}' ({Name}, kind={Kind})", id, record.DisplayName, record.Kind);
            return record;
        }
    }

    /// <summary>
    /// Soft-delete: flips kind to retired so the record stays for historical
    /// task attribution. Returns true if a record was modified.
    /// </summary>
    public bool SoftDelete(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return false;
            if (existing.Kind == ClientIdentityKind.Retired) return false;
            var retired = existing with { Kind = ClientIdentityKind.Retired };
            WriteLocked(retired);
            _byId[id] = retired;
            _logger.LogInformation("Soft-deleted client identity '{Id}'", id);
            return true;
        }
    }

    public ClientIdentity? RequestDrain(string id, bool retireAfterDrain)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing) || existing.Kind == ClientIdentityKind.Retired) return null;
            var now = DateTime.UtcNow;
            var updated = existing with
            {
                DrainRequestedAt = existing.DrainRequestedAt ?? now,
                RetireRequestedAt = retireAfterDrain ? existing.RetireRequestedAt ?? now : existing.RetireRequestedAt,
                Kind = retireAfterDrain && existing.RunnerActiveSlots.GetValueOrDefault() == 0
                    ? ClientIdentityKind.Retired : existing.Kind
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    public ClientIdentity? Revive(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing) || existing.Kind != ClientIdentityKind.Retired) return null;
            var updated = existing with
            {
                Kind = ClientIdentityKind.Service,
                DrainRequestedAt = null,
                RetireRequestedAt = null,
                RunnerDaemonState = "stopped",
                RunnerActiveSlots = 0,
                RunnerAvailableSlots = 0
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    public bool PermanentlyDelete(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing) || existing.Kind != ClientIdentityKind.Retired) return false;
            var path = Path.Combine(IdentitiesFolder, existing.Id + ".json");
            if (File.Exists(path)) File.Delete(path);
            _byId.Remove(id);
            _persistedById.Remove(id);
            _lastReadById.Remove(id);
            return true;
        }
    }

    /// <summary>
    /// Project daemon polls into the identity and finish graceful retirement at
    /// zero active slots.
    ///
    /// <para>
    /// The slot ledger is derived here, once: free slots are the host ceiling
    /// minus the active count, never the daemon's own breathing observation.
    /// A ledger that echoed the daemon showed "active + 1" as its total and so
    /// never described a capacity (AGT-2302).
    /// </para>
    ///
    /// <para>
    /// <paramref name="seedMaxParallelism"/> only bootstraps the central
    /// ceiling on first contact; once a value is persisted the operator owns it
    /// and no daemon report can move it.
    /// </para>
    /// </summary>
    public ClientIdentity? RecordRunnerActivity(
        string id,
        int? activeSlots,
        int availableSlots,
        bool claimed,
        int? seedMaxParallelism = null,
        int? effectiveMaxParallelism = null,
        DateTime? effectiveMaxParallelismAppliedAt = null)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            // Telemetry is sampled less often than claim polling. A poll
            // without a sample must preserve the last known active count;
            // treating "not reported" as zero could complete a pending
            // retirement while work is still running.
            var projectedActiveSlots = activeSlots is null
                ? existing.RunnerActiveSlots
                : Math.Max(0, activeSlots.Value);
            // Seeded only when the ceiling is actually known (an operator target,
            // the daemon's RUNNER_MAX_PARALLELISM, or a migrating project opt-in).
            // A host that reports nothing keeps a null ceiling and the ledger says
            // "capacity not reported" rather than showing an invented total.
            var ceiling = existing.RunnerDesiredMaxParallelism
                ?? PositiveCeiling(seedMaxParallelism);
            var updated = existing with
            {
                // The startup probe covers only the configured fallback remote.
                // Project delivery is admitted by RunnerProjectPreflight, so a
                // fallback failure does not make the daemon itself read-only.
                RunnerDaemonState = "running",
                RunnerActiveSlots = projectedActiveSlots,
                RunnerAvailableSlots = ceiling is > 0
                    ? HostCapacityPolicy.FreeSlots(ceiling.Value, projectedActiveSlots ?? 0)
                    : Math.Max(0, availableSlots),
                RunnerDesiredMaxParallelism = ceiling,
                RunnerTargetLoadPercent = ceiling is null
                    ? existing.RunnerTargetLoadPercent
                    : existing.RunnerTargetLoadPercent ?? HostCapacityPolicy.DefaultTargetLoadPercent,
                RunnerRampStrategy = ceiling is null
                    ? existing.RunnerRampStrategy
                    : RunnerRampStrategies.Normalize(existing.RunnerRampStrategy),
                RunnerEffectiveMaxParallelism = PositiveCeiling(effectiveMaxParallelism)
                    ?? existing.RunnerEffectiveMaxParallelism,
                RunnerEffectiveMaxParallelismAppliedAt = effectiveMaxParallelism is > 0
                    ? (effectiveMaxParallelismAppliedAt ?? DateTime.UtcNow).ToUniversalTime()
                    : existing.RunnerEffectiveMaxParallelismAppliedAt,
                RunnerLastClaimAt = claimed ? DateTime.UtcNow : existing.RunnerLastClaimAt,
                Kind = existing.RetireRequestedAt is not null
                    && activeSlots is not null
                    && activeSlots.Value <= 0
                    ? ClientIdentityKind.Retired : existing.Kind
            };
            WriteHighFrequencyStateIfDueLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    /// <summary>
    /// Operator write for the central host targets. Null fields keep their
    /// current value; the free-slot ledger is recomputed from the new ceiling
    /// so the UI reflects the change before the next daemon poll.
    ///
    /// <para>
    /// An omitted <paramref name="maxParallelism"/> keeps whatever ceiling the
    /// host has - including none. Falling back to a default here would let a
    /// caller that only changes the ramp strategy invent a ceiling of 2 for a
    /// host that never declared one, which is the silent throttle the whole
    /// policy avoids elsewhere.
    /// </para>
    /// </summary>
    public ClientIdentity? SetRunnerCapacity(
        string id,
        int? maxParallelism,
        int? targetLoadPercent,
        string? rampStrategy)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)
                || existing.Kind == ClientIdentityKind.Retired) return null;

            var ceiling = PositiveCeiling(maxParallelism ?? existing.RunnerDesiredMaxParallelism);
            var updated = existing with
            {
                RunnerDesiredMaxParallelism = ceiling,
                RunnerTargetLoadPercent = HostCapacityPolicy.ClampTargetLoad(
                    targetLoadPercent
                    ?? existing.RunnerTargetLoadPercent
                    ?? HostCapacityPolicy.DefaultTargetLoadPercent),
                RunnerRampStrategy = RunnerRampStrategies.Normalize(
                    rampStrategy ?? existing.RunnerRampStrategy),
                RunnerCapacityUpdatedAt = DateTime.UtcNow,
                RunnerAvailableSlots = ceiling is { } known
                    ? HostCapacityPolicy.FreeSlots(known, existing.RunnerActiveSlots ?? 0)
                    : existing.RunnerAvailableSlots
            };
            WriteLocked(updated);
            _byId[id] = updated;
            _logger.LogInformation(
                "runner-capacity-updated client={ClientId} ceiling={Ceiling} targetLoad={TargetLoad} ramp={Ramp}",
                id, updated.RunnerDesiredMaxParallelism, updated.RunnerTargetLoadPercent, updated.RunnerRampStrategy);
            return updated;
        }
    }

    private static int? PositiveCeiling(int? value)
        => value is > 0 ? HostCapacityPolicy.ClampCeiling(value.Value) : null;

    /// <summary>
    /// Persist the user's preferred CLI + model for new-task creation.
    /// Either argument may be null to clear that side without touching the
    /// other; passing both as null clears both. Returns the updated record,
    /// or null if the identity does not exist.
    /// </summary>
    public ClientIdentity? SetDefaults(string id, string? defaultCliType, string? defaultModel, bool clearCli = false, bool clearModel = false, string? defaultThinkingLevel = null, bool clearThinkingLevel = false)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            var updated = existing with
            {
                DefaultCliType = clearCli ? null : (defaultCliType ?? existing.DefaultCliType),
                DefaultModel = clearModel ? null : (defaultModel ?? existing.DefaultModel),
                DefaultThinkingLevel = clearThinkingLevel ? null : (defaultThinkingLevel ?? existing.DefaultThinkingLevel)
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    public ClientIdentity? SetRunnerGitCapability(string id, string status, string? detail, DateTime checkedAt)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            var updated = existing with
            {
                RunnerGitStatus = status,
                RunnerGitDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
                RunnerGitCheckedAt = checkedAt.ToUniversalTime(),
                // A daemon restart may carry repaired per-repository
                // credentials even when the unrelated fallback probe fails.
                // Keep successful proofs and retry every failed project.
                RunnerProjectPreflights = existing.RunnerProjectPreflights
                    .Where(preflight => string.Equals(preflight.Status, "ready", StringComparison.OrdinalIgnoreCase))
                    .ToList()
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    public RunnerProjectPreflight? FindRunnerProjectPreflight(string id, string projectId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byId.TryGetValue(id, out var existing)
                ? existing.RunnerProjectPreflights.FirstOrDefault(item =>
                    string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    public ClientIdentity? SetRunnerProjectPreflight(string id, RunnerProjectPreflight preflight)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            var next = existing.RunnerProjectPreflights
                .Where(item => !string.Equals(item.ProjectId, preflight.ProjectId, StringComparison.OrdinalIgnoreCase))
                .Append(preflight)
                .OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var updated = existing with { RunnerProjectPreflights = next };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

    /// <summary>Invalidate every host's cached proof after repository registration changes.</summary>
    public void InvalidateRunnerProjectPreflights(string projectId)
    {
        EnsureLoaded();
        lock (_lock)
        {
            foreach (var (id, existing) in _byId.ToList())
            {
                var next = existing.RunnerProjectPreflights
                    .Where(item => !string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (next.Count == existing.RunnerProjectPreflights.Count) continue;
                var updated = existing with { RunnerProjectPreflights = next };
                WriteLocked(updated);
                _byId[id] = updated;
            }
        }
    }

    public ClientIdentity? InvalidateRunnerProjectPreflightsForHost(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            if (existing.RunnerProjectPreflights.Count == 0) return existing;
            var updated = existing with { RunnerProjectPreflights = [] };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }
    /// <summary>
    /// Updates <c>lastSeenAt</c> on the identity. Called by the access-log
    /// middleware on every authenticated read or write so the GET listing
    /// can show who has been talking to the API and when.
    /// </summary>
    public void RecordSeen(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return;
            // The in-memory copy always carries the freshest value. The durable
            // comparison is tracked separately so continuous requests cannot
            // postpone the thirty-second flush forever.
            var stamped = existing with { LastSeenAt = DateTime.UtcNow };
            _byId[id] = stamped;
            try { WriteHighFrequencyStateIfDueLocked(stamped); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to persist last-seen for '{Id}'", id); }
        }
    }

    private string AssignSlug(string displayName)
    {
        var seed = displayName.Trim().ToLowerInvariant();
        var slug = SlugSanitiser.Replace(seed, "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "client";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (!SlugAllowed.IsMatch(slug)) slug = "client";

        var candidate = slug;
        var n = 2;
        while (_byId.ContainsKey(candidate))
        {
            candidate = $"{slug}-{n}";
            n++;
        }
        return candidate;
    }

    private void WriteLocked(ClientIdentity record)
    {
        var dir = IdentitiesFolder;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, record.Id + ".json");
        _fileWriter.Write(path, JsonSerializer.Serialize(record, WriteOpts));
        _persistedById[record.Id] = record;
        _lastReadById[record.Id] = FingerprintOrDefault(path);
        if (_diagnosticsById.Remove(record.Id))
            _logger.LogInformation("Client identity corruption recovered by write: {File}", Path.GetFileName(path));
    }

    /// <summary>
    /// High-frequency request and claim telemetry only reaches disk when a
    /// material runner field changed or the durable last-seen stamp is at least
    /// thirty seconds old. In-memory LastSeenAt remains current per request.
    /// </summary>
    private void WriteHighFrequencyStateIfDueLocked(ClientIdentity record)
    {
        if (!_persistedById.TryGetValue(record.Id, out var persisted))
        {
            WriteLocked(record);
            return;
        }

        var durableComparison = record with { LastSeenAt = persisted.LastSeenAt };
        var materialChange = durableComparison != persisted;
        var seenDue = record.LastSeenAt is { } currentSeen
                      && (persisted.LastSeenAt is null
                          || currentSeen - persisted.LastSeenAt.Value >= TimeSpan.FromSeconds(30));
        if (materialChange || seenDue) WriteLocked(record);
    }

    private readonly record struct IdentityFileFingerprint(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc);
}
