using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Fresh, disposable exact-subject workspace for one fenced ReviewAttempt.
/// It never consults or reuses the coding checkout. All writable paths and
/// process namespaces are rooted under the attempt directory.
/// </summary>
public sealed class RemoteReviewWorkspace
{
    private readonly RunnerOptions _options;
    private readonly ReviewSubjectDto _subject;
    private readonly ReviewLeaseDto _lease;
    private readonly Action<string> _log;
    private string? _initialTree;
    private bool _dirtyBefore;

    public RemoteReviewWorkspace(
        RunnerOptions options,
        ReviewSubjectDto subject,
        ReviewLeaseDto lease,
        Action<string> log)
    {
        _options = options;
        _subject = subject;
        _lease = lease;
        _log = log;
        var root = Path.GetFullPath(options.ReviewWorkDir);
        AttemptRoot = Path.Combine(root, SafeSegment(lease.AttemptId));
        RepositoryPath = Path.Combine(AttemptRoot, "repository");
        ArtifactPath = Path.Combine(AttemptRoot, "artifacts");
        CachePath = Path.Combine(AttemptRoot, "cache");
        TempPath = Path.Combine(AttemptRoot, "tmp");
        HomePath = Path.Combine(AttemptRoot, "home");
    }

    public string AttemptRoot { get; }
    public string RepositoryPath { get; }
    public string ArtifactPath { get; }
    public string CachePath { get; }
    public string TempPath { get; }
    public string HomePath { get; }

    public IReadOnlyDictionary<string, string?> ProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
            ["LC_ALL"] = Environment.GetEnvironmentVariable("LC_ALL") ?? "C.UTF-8",
            ["SSL_CERT_FILE"] = Environment.GetEnvironmentVariable("SSL_CERT_FILE"),
            ["SSL_CERT_DIR"] = Environment.GetEnvironmentVariable("SSL_CERT_DIR"),
            ["HOME"] = HomePath,
            ["TMPDIR"] = TempPath,
            ["TMP"] = TempPath,
            ["TEMP"] = TempPath,
            ["XDG_CACHE_HOME"] = CachePath,
            ["NUGET_PACKAGES"] = Path.Combine(CachePath, "nuget"),
            ["npm_config_cache"] = Path.Combine(CachePath, "npm"),
            ["PIP_CACHE_DIR"] = Path.Combine(CachePath, "pip"),
            ["CARGO_HOME"] = Path.Combine(CachePath, "cargo"),
            ["GRADLE_USER_HOME"] = Path.Combine(CachePath, "gradle"),
            ["DOTNET_CLI_HOME"] = Path.Combine(CachePath, "dotnet"),
            ["COMPOSE_PROJECT_NAME"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_NAMESPACE"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_DATABASE_NAMESPACE"] = _lease.ResourceNamespace,
            ["AGENT_REVIEW_PORT_BASE"] = _lease.PortBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PORT"] = _lease.PortBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        foreach (var name in _options.ReviewCredentialEnvironment)
        {
            if (!SafeEnvironmentName(name))
                throw new InvalidOperationException($"Invalid review credential environment name '{name}'.");
            environment[name] = Environment.GetEnvironmentVariable(name);
        }
        return environment;
    }

    public async Task<ReviewWorkspaceProofDto> PrepareAsync(
        TaskServerClient client,
        CancellationToken ct)
    {
        if (Directory.Exists(AttemptRoot))
            throw new ReviewInfrastructureException(
                "DirtyBefore",
                $"Review attempt workspace already exists: {AttemptRoot}");
        Directory.CreateDirectory(AttemptRoot);
        Directory.CreateDirectory(ArtifactPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(HomePath);

        if (!string.IsNullOrWhiteSpace(_subject.RepositoryUrl))
            await MaterializeGitAsync(_subject.RepositoryUrl!, ct);
        else if (!string.IsNullOrWhiteSpace(_subject.SourceBundleArtifactId))
            await MaterializeBundleAsync(client, ct);
        else
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                "Review subject has neither an immutable result ref nor a source bundle.");

        var repositoryId = !string.IsNullOrWhiteSpace(_subject.RepositoryUrl)
            ? TaskServerClient.RepositoryIdentity(_subject.RepositoryUrl)
            : _subject.RepositoryId;
        if (!string.Equals(repositoryId, _subject.RepositoryId, StringComparison.Ordinal))
            throw new ReviewInfrastructureException(
                "RepositoryMismatch",
                $"Materialized repository identity '{repositoryId}' does not match '{_subject.RepositoryId}'.");

        var head = await GitValueAsync("rev-parse", "HEAD", ct);
        if (!string.Equals(head, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "ShaMismatch",
                $"Materialized HEAD '{head}' does not match expected Result-SHA '{_subject.ExpectedResultSha}'.");
        _initialTree = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
        _dirtyBefore = (await GitValueAsync("status", "--porcelain", "--untracked-files=all", ct)).Length > 0;
        if (_dirtyBefore)
            throw new ReviewInfrastructureException("DirtyBefore", "Fresh review workspace is dirty before review.");
        _log($"review subject ready repository={repositoryId} head={head} tree={_initialTree} dirty=false");
        return Proof(repositoryId!, head, _initialTree, false);
    }

    public async Task<ReviewExecutionEvidence> ExecutePlanAsync(CancellationToken ct)
    {
        var commands = new List<ReviewCommandEvidenceDto>();
        var verdicts = new List<ReviewVerdictDto>();
        var artifacts = new List<ReviewArtifactEvidenceDto>();
        foreach (var command in _subject.Plan.Commands)
        {
            var headBefore = await GitValueAsync("rev-parse", "HEAD", ct);
            var treeBefore = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
            if (!string.Equals(headBefore, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
                throw new ReviewInfrastructureException(
                    "CommandSubjectMismatch",
                    $"Step '{command.StepId}' would run at '{headBefore}', not '{_subject.ExpectedResultSha}'.");
            var started = DateTime.UtcNow;
            ProcessResult process;
            string? signal = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 1, 7200)));
                process = await ProcessRunner.RunAsync(
                    command.FileName,
                    command.Arguments,
                    RepositoryPath,
                    environment: ProcessEnvironment(),
                    clearEnvironment: true,
                    ct: timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process = new ProcessResult(-1, string.Empty, "Review command timed out.");
                signal = "timeout";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ReviewInfrastructureException(
                    "ToolUnavailable",
                    $"Review step '{command.StepId}' could not start: {exception.Message}",
                    exception);
            }
            var finished = DateTime.UtcNow;
            var stdoutPath = Path.Combine(ArtifactPath, $"{SafeSegment(command.StepId)}.stdout.log");
            var stderrPath = Path.Combine(ArtifactPath, $"{SafeSegment(command.StepId)}.stderr.log");
            await File.WriteAllTextAsync(stdoutPath, process.StdOut, ct);
            await File.WriteAllTextAsync(stderrPath, process.StdErr, ct);
            var stdout = HashText(process.StdOut);
            var stderr = HashText(process.StdErr);
            commands.Add(new ReviewCommandEvidenceDto(
                command.StepId,
                command.Aspect,
                command.FileName,
                command.Arguments,
                _subject.ExpectedResultSha,
                headBefore,
                treeBefore,
                started,
                finished,
                process.ExitCode,
                signal,
                stdout,
                stderr));
            artifacts.Add(new ReviewArtifactEvidenceDto(
                Path.GetFileName(stdoutPath), "text/plain", stdout, new FileInfo(stdoutPath).Length));
            artifacts.Add(new ReviewArtifactEvidenceDto(
                Path.GetFileName(stderrPath), "text/plain", stderr, new FileInfo(stderrPath).Length));
            verdicts.Add(ParseVerdict(command, process));
        }

        var finalHead = await GitValueAsync("rev-parse", "HEAD", ct);
        var finalTree = await GitValueAsync("rev-parse", "HEAD^{tree}", ct);
        var status = await GitValueAsync("status", "--porcelain", "--untracked-files=all", ct);
        var dirtyAfter = status.Length > 0
                         || !string.Equals(finalHead, _subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(finalTree, _initialTree, StringComparison.OrdinalIgnoreCase);
        var proof = Proof(_subject.RepositoryId, finalHead, finalTree, dirtyAfter);
        var outcome = verdicts.Any(verdict =>
            verdict.Status is "block" or "concerns" or "fail")
            ? "ProductFailure"
            : "Pass";
        return new ReviewExecutionEvidence(outcome, proof, commands, artifacts, verdicts);
    }

    public ReviewEnvironmentDto EnvironmentEvidence()
        => new(
            _lease.HostId,
            _lease.ExecutorId,
            _lease.InstanceId,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            new Dictionary<string, string>
            {
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["git"] = "captured-by-materialization",
            },
            new Dictionary<string, string>
            {
                ["serviceRole"] = "remote-review-executor",
                ["workspace"] = AttemptRoot,
                ["cache"] = CachePath,
                ["temp"] = TempPath,
                ["ports"] = $"{_lease.PortBase}-{_lease.PortBase + 7}",
                ["containers"] = _lease.ResourceNamespace,
                ["databases"] = _lease.ResourceNamespace,
                ["credentials"] = "review-read-only",
            });

    public Task<bool> CleanupAsync()
    {
        if (!Directory.Exists(AttemptRoot)) return Task.FromResult(true);
        var expectedRoot = Path.GetFullPath(_options.ReviewWorkDir)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(AttemptRoot);
        if (!target.StartsWith(expectedRoot, StringComparison.Ordinal)
            || string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                expectedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing review cleanup outside the configured attempt root.");
        Directory.Delete(target, recursive: true);
        return Task.FromResult(!Directory.Exists(target));
    }

    private async Task MaterializeGitAsync(string repositoryUrl, CancellationToken ct)
    {
        var clone = await ProcessRunner.RunAsync(
            "git",
            ["-c", "credential.helper=", "clone", "--no-checkout", "--filter=blob:none", repositoryUrl, RepositoryPath],
            AttemptRoot,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!clone.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Repository clone failed: {clone.StdErr.Trim()}");
        var resultRef = string.IsNullOrWhiteSpace(_subject.ResultRef)
            ? _subject.ExpectedResultSha
            : _subject.ResultRef!;
        var fetch = await ProcessRunner.RunAsync(
            "git",
            ["-c", "credential.helper=", "fetch", "--no-tags", "--depth=1", "origin", resultRef],
            RepositoryPath,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!fetch.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Immutable result ref '{resultRef}' could not be fetched: {fetch.StdErr.Trim()}");
        await GitRequiredAsync(["checkout", "--detach", "FETCH_HEAD"], ct);
    }

    private async Task MaterializeBundleAsync(TaskServerClient client, CancellationToken ct)
    {
        var artifact = await client.GetArtifactContentAsync(
            _subject.SourceRunId,
            _subject.SourceBundleArtifactId!,
            ct);
        if (artifact is null)
            throw new ReviewInfrastructureException("SnapshotUnavailable", "Immutable source bundle is unavailable.");
        var bytes = Convert.FromBase64String(artifact.ContentBase64);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, _subject.SourceBundleSha256, StringComparison.OrdinalIgnoreCase))
            throw new ReviewInfrastructureException(
                "SourceBundleDigestMismatch",
                $"Source bundle digest '{digest}' does not match '{_subject.SourceBundleSha256}'.");
        var bundlePath = Path.Combine(AttemptRoot, "source.bundle");
        await File.WriteAllBytesAsync(bundlePath, bytes, ct);
        var clone = await ProcessRunner.RunAsync(
            "git",
            ["clone", "--no-checkout", bundlePath, RepositoryPath],
            AttemptRoot,
            environment: ProcessEnvironment(),
            clearEnvironment: true,
            ct: ct);
        if (!clone.Success)
            throw new ReviewInfrastructureException(
                "SnapshotUnavailable",
                $"Source bundle could not be materialized: {clone.StdErr.Trim()}");
        await GitRequiredAsync(["checkout", "--detach", _subject.ExpectedResultSha], ct);
    }

    private async Task<string> GitValueAsync(string first, string second, CancellationToken ct)
        => await GitValueAsync([first, second], ct);

    private async Task<string> GitValueAsync(string first, string second, string third, CancellationToken ct)
        => await GitValueAsync([first, second, third], ct);

    private async Task<string> GitValueAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", arguments, RepositoryPath,
            environment: ProcessEnvironment(), clearEnvironment: true, ct: ct);
        if (!result.Success)
            throw new ReviewInfrastructureException(
                "WorkspaceProofFailed",
                $"git {string.Join(' ', arguments)} failed: {result.StdErr.Trim()}");
        return result.StdOut.Trim();
    }

    private async Task GitRequiredAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        => _ = await GitValueAsync(arguments, ct);

    private ReviewWorkspaceProofDto Proof(
        string repositoryId,
        string head,
        string tree,
        bool dirtyAfter)
        => new(
            repositoryId,
            _subject.ExpectedResultSha,
            head,
            tree,
            _dirtyBefore,
            dirtyAfter,
            HashText(Path.GetFullPath(AttemptRoot)),
            _lease.ResourceNamespace);

    private static ReviewVerdictDto ParseVerdict(ReviewCommandDto command, ProcessResult result)
    {
        var marker = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.Contains("[[ASPECT_VERDICT:", StringComparison.Ordinal));
        if (marker is not null)
        {
            var status = Field(marker, "status") ?? (result.Success ? "pass" : "block");
            return new ReviewVerdictDto(
                command.Aspect,
                status,
                Field(marker, "classification") ?? "RemoteAspectVerdict",
                Field(marker, "summary") ?? $"Remote aspect '{command.Aspect}' returned {status}.");
        }
        return new ReviewVerdictDto(
            command.Aspect,
            result.Success ? "pass" : "block",
            result.Success ? "CommandPassed" : "CommandFailed",
            result.Success
                ? $"Review command '{command.StepId}' passed."
                : $"Review command '{command.StepId}' exited {result.ExitCode}.");
    }

    private static string? Field(string marker, string key)
    {
        var content = marker[(marker.IndexOf(':') + 1)..].Replace("]]", string.Empty, StringComparison.Ordinal);
        foreach (var field in content.Split(';', StringSplitOptions.TrimEntries))
        {
            var split = field.IndexOf('=');
            if (split > 0 && string.Equals(field[..split].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return field[(split + 1)..].Trim();
        }
        return null;
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool SafeEnvironmentName(string name)
        => name.Length is > 0 and <= 128
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    internal static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
}

public sealed record ReviewExecutionEvidence(
    string Outcome,
    ReviewWorkspaceProofDto Workspace,
    IReadOnlyList<ReviewCommandEvidenceDto> Commands,
    IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts,
    IReadOnlyList<ReviewVerdictDto> Verdicts);

public sealed class ReviewInfrastructureException : Exception
{
    public ReviewInfrastructureException(string classification, string message, Exception? inner = null)
        : base(message, inner)
    {
        Classification = classification;
    }

    public string Classification { get; }
}
