using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>
/// Classification of a missing npm-backed coding CLI. The package directory is
/// checked independently from the executable shim so a torn global install can
/// be repaired without turning an intentionally uninstalled CLI into a network
/// side effect.
/// </summary>
internal sealed record NpmCliInstallInspection(
    string CliType,
    string PackageName,
    string Prefix,
    string PackageDirectory,
    bool PackagePresent,
    string? PackageVersion,
    string ExpectedShim,
    bool ShimPresent,
    IReadOnlyList<string> Activity)
{
    public bool MissingShimWithPackagePresent => PackagePresent && !ShimPresent;
}

internal sealed record CliRepairJournalEntry(
    DateTimeOffset OccurredAt,
    string CliType,
    string PackageName,
    string Outcome,
    string Binary,
    string Prefix,
    string? VersionBefore,
    string? VersionAfter,
    DateTimeOffset NextAttemptAt,
    IReadOnlyList<string> ActivityBefore,
    IReadOnlyList<string> ActivityAfter,
    string? NpmOutput,
    string? Error);

internal delegate Task<ProcessResult> NpmCliRepairLauncher(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken ct);

/// <summary>
/// Repairs a missing Windows npm shim when, and only when, its global package
/// is still installed. Attempts are journaled durably and limited to one per
/// CLI per hour across process restarts.
/// </summary>
internal sealed class NpmCliSelfRepair
{
    internal static readonly TimeSpan AttemptInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SecretShaped = new(
        @"(?i)(?:_authToken|token|password)=([^\s&]+)|\b(?:sk-|npm_)[A-Za-z0-9_\-]{6,}\b|\b[A-Za-z0-9_\-]{40,}\b",
        RegexOptions.Compiled);

    private readonly string _journalPath;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly NpmCliRepairLauncher _launcher;
    private readonly Func<string, bool> _executableExists;
    private readonly Func<bool> _isWindows;
    private readonly Func<string, string?> _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CliRepairJournalEntry> _latestAttempts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityDetails =
        new(StringComparer.Ordinal);

    public NpmCliSelfRepair(
        string stateDirectory,
        Action<string> log,
        Func<DateTimeOffset>? clock = null,
        NpmCliRepairLauncher? launcher = null,
        Func<string, bool>? executableExists = null,
        Func<bool>? isWindows = null,
        Func<string, string?>? environment = null)
    {
        _journalPath = Path.Combine(stateDirectory, "cli-repairs.jsonl");
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _launcher = launcher ?? ((fileName, arguments, ct) =>
            ProcessRunner.RunAsync(fileName, arguments, ct: ct));
        _executableExists = executableExists ?? ProviderAuthProbe.ExecutableExists;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        LoadJournal();
    }

    public IReadOnlyDictionary<string, string> CapabilityDetails
    {
        get
        {
            lock (_capabilityDetails)
                return new Dictionary<string, string>(_capabilityDetails, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Checks all configured card CLIs and repairs qualifying torn installs.
    /// Returns the binaries repaired in this pass so provider-auth can be
    /// refreshed immediately instead of retaining a cached binary-missing state.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProbeAsync(
        IReadOnlyList<(string CliType, string Binary)> binaries,
        CancellationToken ct)
    {
        if (!_isWindows()) return [];
        await _gate.WaitAsync(ct);
        try
        {
            var repaired = new List<string>();
            foreach (var (cliType, binary) in binaries
                         .DistinctBy(item => item.CliType, StringComparer.Ordinal))
            {
                if (_executableExists(binary)) continue;
                var definition = Definition(cliType);
                if (definition is null) continue;

                var prefix = await ResolvePrefixAsync(ct);
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    SetDetail(cliType,
                        $"CLI binary '{binary}' was not found and the npm global prefix could not be resolved; no repair was attempted.");
                    continue;
                }

                var inspection = Inspect(cliType, binary, prefix);
                if (!inspection.PackagePresent)
                {
                    SetDetail(cliType,
                        $"CLI binary '{binary}' was not found; npm package '{definition.Value.PackageName}' is not installed at '{inspection.PackageDirectory}', so automatic repair was not attempted.");
                    continue;
                }
                if (!inspection.MissingShimWithPackagePresent)
                {
                    SetDetail(cliType,
                        $"CLI binary '{binary}' was not found, but the expected npm shim '{inspection.ExpectedShim}' still exists; check PATH or the configured binary path.");
                    continue;
                }

                var now = _clock();
                if (_latestAttempts.TryGetValue(cliType, out var previous)
                    && previous.NextAttemptAt > now)
                {
                    SetDetail(cliType,
                        $"CLI repair last attempted at {previous.OccurredAt:u} and remains unavailable; next automatic attempt after {previous.NextAttemptAt:u}. {previous.Error}".Trim());
                    continue;
                }

                var entry = await RepairAsync(binary, inspection, now, ct);
                _latestAttempts[cliType] = entry;
                AppendJournal(entry);
                if (entry.Outcome == "repaired")
                {
                    repaired.Add(binary);
                    SetDetail(cliType,
                        $"CLI repaired at {entry.OccurredAt:u}; version before {entry.VersionBefore ?? "unknown"}, after {entry.VersionAfter ?? "unknown"}.");
                }
                else
                {
                    SetDetail(cliType,
                        $"CLI repair failed at {entry.OccurredAt:u}; version before {entry.VersionBefore ?? "unknown"}. {entry.Error}".Trim());
                }
            }
            return repaired;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static NpmCliInstallInspection Inspect(
        string cliType,
        string binary,
        string prefix,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, DateTime>? lastWriteTimeUtc = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        lastWriteTimeUtc ??= path => File.GetLastWriteTimeUtc(path);
        var definition = Definition(cliType)
            ?? throw new ArgumentException($"Unsupported npm CLI '{cliType}'.", nameof(cliType));
        var packageDirectory = Path.Combine(
            [prefix, "node_modules", .. definition.PackageName.Split('/')]);
        var packageJson = Path.Combine(packageDirectory, "package.json");
        var expectedShim = Path.Combine(prefix, definition.ShimName + ".cmd");
        var activity = new List<string>();
        AddActivity(activity, packageJson, fileExists, lastWriteTimeUtc);
        AddActivity(activity, expectedShim, fileExists, lastWriteTimeUtc);
        AddActivity(activity, Path.Combine(prefix, definition.ShimName), fileExists, lastWriteTimeUtc);

        try
        {
            if (directoryExists(prefix))
            {
                foreach (var orphan in Directory.GetFiles(
                             prefix,
                             "." + definition.ShimName + "*"))
                {
                    AddActivity(activity, orphan, fileExists, lastWriteTimeUtc);
                }
            }
        }
        catch
        {
            // Evidence collection is best effort and never changes classification.
        }

        return new NpmCliInstallInspection(
            cliType,
            definition.PackageName,
            prefix,
            packageDirectory,
            directoryExists(packageDirectory),
            ReadPackageVersion(packageJson, fileExists),
            expectedShim,
            fileExists(expectedShim),
            activity);
    }

    private async Task<CliRepairJournalEntry> RepairAsync(
        string binary,
        NpmCliInstallInspection before,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var npm = ResolveExecutable("npm", before.Prefix);
        var activityBefore = AppendNpmActivity(before.Activity);
        ProcessResult result;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(RepairTimeout);
            result = await _launcher(
                npm,
                ["install", "--global", before.PackageName],
                bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("npm install -g timed out after five minutes");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed($"npm install -g could not start: {exception.GetType().Name}: {exception.Message}");
        }

        var after = Inspect(before.CliType, binary, before.Prefix);
        var executable = after.ShimPresent || _executableExists(binary);
        var versionAfter = executable
            ? await ProbeVersionAsync(after.ExpectedShim, after.PackageVersion, ct)
            : after.PackageVersion;
        var output = Excerpt($"{result.StdOut}\n{result.StdErr}");
        var error = result.Success && executable
            ? null
            : !result.Success
                ? $"npm install -g exited {result.ExitCode}: {output}"
                : $"npm install -g completed but shim '{after.ExpectedShim}' is still missing";
        var entry = new CliRepairJournalEntry(
            now,
            before.CliType,
            before.PackageName,
            error is null ? "repaired" : "failed",
            binary,
            before.Prefix,
            before.PackageVersion,
            versionAfter,
            now + AttemptInterval,
            activityBefore,
            AppendNpmActivity(after.Activity),
            output,
            error);
        _log(
            $"cli-self-repair cli={before.CliType} outcome={entry.Outcome} "
            + $"before={entry.VersionBefore ?? "unknown"} after={entry.VersionAfter ?? "unknown"} "
            + $"nextAttemptAt={entry.NextAttemptAt:u} error={entry.Error ?? "none"}");
        return entry;

        CliRepairJournalEntry Failed(string error)
        {
            var entry = new CliRepairJournalEntry(
                now,
                before.CliType,
                before.PackageName,
                "failed",
                binary,
                before.Prefix,
                before.PackageVersion,
                null,
                now + AttemptInterval,
                activityBefore,
                [],
                null,
                error);
            _log(
                $"cli-self-repair cli={before.CliType} outcome=failed "
                + $"before={entry.VersionBefore ?? "unknown"} nextAttemptAt={entry.NextAttemptAt:u} error={error}");
            return entry;
        }
    }

    private async Task<string?> ProbeVersionAsync(
        string expectedShim,
        string? packageVersion,
        CancellationToken ct)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _launcher(expectedShim, ["--version"], bounded.Token);
            if (result.Success)
            {
                return result.StdOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault() ?? packageVersion;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"cli-self-repair version-probe-failed shim={expectedShim} error={exception.Message}");
        }
        return packageVersion;
    }

    private async Task<string?> ResolvePrefixAsync(CancellationToken ct)
    {
        var appData = _environment("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
            return Path.Combine(appData, "npm");
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _launcher(ResolveExecutable("npm", null), ["prefix", "--global"], bounded.Token);
            return result.Success
                ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"cli-self-repair npm-prefix-probe-failed error={exception.Message}");
            return null;
        }
    }

    private IReadOnlyList<string> AppendNpmActivity(IReadOnlyList<string> activity)
    {
        var result = activity.ToList();
        var localAppData = _environment("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData)) return result;
        var logs = Path.Combine(localAppData, "npm-cache", "_logs");
        try
        {
            if (Directory.Exists(logs))
            {
                result.AddRange(Directory.GetFiles(logs, "*.log")
                    .Select(path => (Path: path, At: File.GetLastWriteTimeUtc(path)))
                    .OrderByDescending(item => item.At)
                    .Take(5)
                    .Select(item =>
                        $"{item.Path}|lastWriteUtc={item.At:o}|tail={ReadLogTail(item.Path)}"));
            }
        }
        catch
        {
            // Npm diagnostic logs are optional evidence.
        }
        return result;
    }

    private static string ReadLogTail(string path)
    {
        try
        {
            return Excerpt(
                string.Join('\n', File.ReadLines(path).TakeLast(20)),
                maxLength: 1200);
        }
        catch
        {
            return "unavailable";
        }
    }

    private static (string PackageName, string ShimName)? Definition(string cliType)
        => cliType.Trim().ToLowerInvariant() switch
        {
            "claude" => ("@anthropic-ai/claude-code", "claude"),
            "codex" => ("@openai/codex", "codex"),
            _ => null,
        };

    private static string ResolveExecutable(string executable, string? preferredDirectory)
    {
        if (!OperatingSystem.IsWindows()) return executable;
        var names = new[] { executable + ".cmd", executable + ".exe", executable + ".bat" };
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            var preferred = names
                .Select(name => Path.Combine(preferredDirectory, name))
                .FirstOrDefault(File.Exists);
            if (preferred is not null) return preferred;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = names.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
            if (match is not null) return match;
        }
        return executable + ".cmd";
    }

    private static string? ReadPackageVersion(string packageJson, Func<string, bool> fileExists)
    {
        if (!fileExists(packageJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddActivity(
        ICollection<string> activity,
        string path,
        Func<string, bool> fileExists,
        Func<string, DateTime> lastWriteTimeUtc)
    {
        if (!fileExists(path)) return;
        try { activity.Add($"{path}|lastWriteUtc={lastWriteTimeUtc(path):o}"); }
        catch { activity.Add($"{path}|lastWriteUtc=unavailable"); }
    }

    private static string Excerpt(string value, int maxLength = 800)
    {
        var compact = string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var redacted = SecretShaped.Replace(compact, match =>
            match.Value.Contains('=')
                ? match.Value[..(match.Value.IndexOf('=') + 1)] + "[redacted]"
                : "[redacted]");
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "...";
    }

    private void LoadJournal()
    {
        try
        {
            if (!File.Exists(_journalPath)) return;
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CliRepairJournalEntry? entry;
                try { entry = JsonSerializer.Deserialize<CliRepairJournalEntry>(line, Json); }
                catch (JsonException)
                {
                    _log($"cli-self-repair journal-row-skipped path={_journalPath} reason=invalid-json");
                    continue;
                }
                if (entry is null) continue;
                _latestAttempts[entry.CliType] = entry;
                if (entry.Outcome == "repaired")
                {
                    SetDetail(entry.CliType,
                        $"CLI repaired at {entry.OccurredAt:u}; version before {entry.VersionBefore ?? "unknown"}, after {entry.VersionAfter ?? "unknown"}.");
                }
            }
        }
        catch (Exception exception)
        {
            _log($"cli-self-repair journal-read-failed path={_journalPath} error={exception.Message}");
        }
    }

    private void AppendJournal(CliRepairJournalEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            using var stream = new FileStream(
                _journalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(entry, Json));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception)
        {
            _log($"cli-self-repair journal-write-failed path={_journalPath} error={exception.Message}");
        }
    }

    private void SetDetail(string cliType, string detail)
    {
        lock (_capabilityDetails) _capabilityDetails[cliType] = detail;
    }
}
