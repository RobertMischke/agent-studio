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
    private readonly object _lock = new();
    private readonly Dictionary<string, ClientIdentity> _byId = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ClientIdentityStore(IConfiguration config, ILogger<ClientIdentityStore> logger)
    {
        _config = config;
        _logger = logger;
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
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<ClientIdentity>(json, ReadOpts);
                if (record == null || string.IsNullOrWhiteSpace(record.Id))
                {
                    _logger.LogWarning("Skipping malformed identity file {File}", file);
                    continue;
                }
                _byId[record.Id] = record;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read identity file {File}", file);
            }
        }
        _logger.LogInformation("Loaded {Count} client identities from {Dir}", _byId.Count, dir);
    }

    private void EnsureBootstrapDefaultLocked()
    {
        if (_byId.ContainsKey(DefaultClientIdentity.Id)) return;

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
            return _byId.TryGetValue(clientId, out var rec) && rec.Kind != ClientIdentityKind.Retired;
        }
    }

    public ClientIdentity? Find(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byId.TryGetValue(id, out var rec) ? rec : null;
        }
    }

    public List<ClientIdentity> ListAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byId.Values.OrderBy(c => c.RegisteredAt).ToList();
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
            return true;
        }
    }

    /// <summary>Project daemon polls into the identity and finish graceful retirement at zero active slots.</summary>
    public ClientIdentity? RecordRunnerActivity(string id, int activeSlots, int availableSlots, bool claimed)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return null;
            var updated = existing with
            {
                RunnerDaemonState = string.Equals(existing.RunnerGitStatus, "read-only", StringComparison.OrdinalIgnoreCase)
                    ? "read-only" : "running",
                RunnerActiveSlots = Math.Max(0, activeSlots),
                RunnerAvailableSlots = Math.Max(0, availableSlots),
                RunnerLastClaimAt = claimed ? DateTime.UtcNow : existing.RunnerLastClaimAt,
                Kind = existing.RetireRequestedAt is not null && activeSlots <= 0
                    ? ClientIdentityKind.Retired : existing.Kind
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }

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
                RunnerGitCheckedAt = checkedAt.ToUniversalTime()
            };
            WriteLocked(updated);
            _byId[id] = updated;
            return updated;
        }
    }
 <c>lastSeenAt</c> on the identity. Called by the access-log
    /// middleware on every authenticated read or write so the GET listing
    /// can show who has been talking to the API and when.
    /// </summary>
    public void RecordSeen(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var existing)) return;
            // Throttle disk writes: only flush when last-seen is older than 30s.
            // The in-memory copy always carries the freshest value.
            var stamped = existing with { LastSeenAt = DateTime.UtcNow };
            _byId[id] = stamped;
            if (existing.LastSeenAt is null
                || (stamped.LastSeenAt!.Value - existing.LastSeenAt.Value).TotalSeconds > 30)
            {
                try { WriteLocked(stamped); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to persist last-seen for '{Id}'", id); }
            }
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
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, WriteOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "Failed to remove identity temp file {Path}", tmp); }
            throw;
        }
    }
}
