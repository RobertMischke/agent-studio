using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentStudio.TaskServer.Contracts;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Thin typed HTTP client over the Task Server's Runner API surface: the fenced
/// lease (RM-3), log + artifact ingestion (RM-4), fenced run completion, and the
/// task-file read used to fetch prompt.md. The runner talks to the server only
/// through these routes - it never writes the task store directly.
/// </summary>
public sealed class TaskServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();
    private static readonly JsonSerializerOptions TaskServerContractJson =
        new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string? _configuredClientId;
    private readonly RunnerOptions? _options;
    // Per-run caches, evicted on completion/release so the long-lived daemon does
    // not retain every claimed task's lease and full prompt body for its lifetime.
    private readonly ConcurrentDictionary<string, (string RunId, RunLeaseInfoDto Lease, string InstanceId)> _v1Leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _v1TaskBodies = new(StringComparer.OrdinalIgnoreCase);
    private bool _useV1;
    private readonly bool _usesServiceCredential;
    private int? _centralHostMaxParallelism;
    private DateTime? _centralHostMaxParallelismAppliedAt;

    public TaskServerClient(RunnerOptions options)
    {
        _options = options;
        HttpMessageHandler handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(options.TlsServerCertificateSha256))
        {
            var expectedFingerprint = options.TlsServerCertificateSha256;
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null
                    && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0
                    && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                    && certificate.NotAfter.ToUniversalTime() >= DateTime.UtcNow
                    && string.Equals(
                        Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase),
            };
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(options.ServerUrl), Timeout = TimeSpan.FromSeconds(60) };
        _configuredClientId = options.ClientId;
        _usesServiceCredential = !string.IsNullOrWhiteSpace(options.AuthToken);
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        // X-Client-Id is attribution only. Seed it with the configured label or
        // Runner id; authentication is supplied independently by the service
        // credential in the networked profile.
        SetClientId(options.ClientId ?? options.RunnerId);
        _http.DefaultRequestHeaders.Add(Contract.TaskServerProtocol.HeaderName, RunnerOptions.ProtocolVersion.ToString());
        _http.DefaultRequestHeaders.Add(Contract.TaskServerProtocol.ClientVersionHeaderName, typeof(TaskServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0");
    }

    /// <summary>
    /// Test seam: drive the client against an already-configured <see cref="HttpClient"/>
    /// such as one produced by the backend's in-memory WebApplicationFactory, so the
    /// runner's real HTTP + WireModels round-trip is exercised end-to-end against the
    /// live server endpoints without a socket. Not for production use; the production
    /// path is the <see cref="RunnerOptions"/> constructor above.
    /// </summary>
    internal TaskServerClient(
        HttpClient http,
        string runnerId,
        string? configuredClientId = null,
        string? authToken = null,
        bool usesDurableTaskServer = false,
        RunnerOptions? options = null)
    {
        _http = http;
        _options = options;
        _configuredClientId = configuredClientId;
        _usesServiceCredential = !string.IsNullOrWhiteSpace(authToken);
        if (_usesServiceCredential)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        SetClientId(configuredClientId ?? runnerId);
        _useV1 = usesDurableTaskServer;
    }

    /// <summary>
    /// Negotiates the published version range before registration or claim. A
    /// 404 keeps the local legacy profile working against the co-hosted backend;
    /// a separated Task Server that answers the endpoint is authoritative and
    /// rejects an unsupported runner before any work can be claimed.
    /// </summary>
    public async Task EnsureCompatibleAsync(CancellationToken ct)
    {
        var clientVersion = typeof(TaskServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        using var response = await _http.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new Contract.ProtocolCompatibilityRequest("runner", clientVersion, RunnerOptions.ProtocolVersion),
            Json,
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _useV1 = false;
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new TaskServerException((int)response.StatusCode, $"Task Server protocol negotiation failed: {Trim(detail)}");
        var compatibility = JsonSerializer.Deserialize<Contract.ProtocolCompatibilityResponse>(detail, Json);
        if (compatibility?.Supported != true)
            throw new TaskServerException(426, compatibility?.Reason ?? "Task Server protocol is not compatible.");
        _useV1 = true;
    }

    /// <summary>
    /// Register the runner as a client identity and adopt the server-assigned id as
    /// the X-Client-Id for every subsequent write. Idempotent on the display name;
    /// the registration route is an open path so it works with the provisional id.
    /// Returns the adopted client id (falls back to the provisional id on an empty
    /// reply so a misconfigured open-auth server still gets a stable header).
    /// </summary>
    public async Task<string> RegisterAsync(string displayName, string kind, CancellationToken ct)
    {
        if (_useV1)
        {
            var options = _options ?? throw new InvalidOperationException("Runner options are unavailable for v1 registration.");
            var runnerId = options.RunnerId;
            var capabilities = options.Role == "review"
                ? new[]
                {
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SourceBundleMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                    Contract.ReviewCapabilities.VisionReview,
                    Contract.ReviewCapabilities.BaselineComparison,
                }
                : new[]
                {
                    Contract.ReviewCapabilities.CodingExecutor,
                    "claim",
                    "events",
                    "artifacts",
                    "fenced-completion",
                    "durable-result-handoff",
                    "host-outbox-replay",
                };
            var request = new Contract.RegisterRunnerRequest(
                displayName,
                options.Hostname,
                RunnerInstanceId,
                typeof(TaskServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                RunnerOptions.ProtocolVersion,
                capabilities,
                BootstrapMaxParallelism: options.HostMaxParallelism);
            try
            {
                var registered = await SendJsonAsync<Contract.RegisterRunnerRequest, Contract.RunnerDto>(
                    HttpMethod.Put,
                    $"/api/v1/runners/{Uri.EscapeDataString(runnerId)}",
                    request,
                    ct);
                AdoptRuntimeCapacity(registered?.RuntimeCapacity);
                SetClientId(runnerId);
                return runnerId;
            }
            catch (TaskServerException ex) when (ex.StatusCode == 409 && options.Role != "review")
            {
                // Coding-fallback-guard: the monolith V1 mount admits only the
                // review-executor identity ("runner-role-conflict"). A coding
                // runner that negotiated V1 (the protocol endpoint exists in the
                // monolith) must fall back to the legacy claim plane instead of
                // crash-looping on the role conflict. Review runners keep V1.
                _useV1 = false;
                // fall through to the legacy registration path below.
            }
        }

        // Networked-profile runners are enrolled by an owner. Their bearer
        // credential is the authentication boundary; X-Client-Id remains an
        // optional attribution label and open self-registration is forbidden.
        if (_usesServiceCredential)
            return ClientId;

        if (!string.IsNullOrWhiteSpace(_configuredClientId))
        {
            var escapedClientId = Uri.EscapeDataString(_configuredClientId);
            using var verify = await _http.GetAsync(
                $"/api/clients/{escapedClientId}", ct);
            var detail = await verify.Content.ReadAsStringAsync(ct);
            if (!verify.IsSuccessStatusCode)
            {
                throw new TaskServerException(
                    (int)verify.StatusCode,
                    $"Configured RUNNER_CLIENT_ID '{_configuredClientId}' was not accepted by the Task Server " +
                    $"(GET /api/clients/{escapedClientId} -> {(int)verify.StatusCode}: {Trim(detail)}). " +
                    "Choose the registered host identity shown in Remote Hosts; startup will not create a replacement identity.");
            }

            ClientIdentityDetail? identityDetail;
            try
            {
                identityDetail = JsonSerializer.Deserialize<ClientIdentityDetail>(detail, Json);
            }
            catch (JsonException ex)
            {
                throw InvalidConfiguredIdentity(
                    $"the response was not a client identity ({ex.Message})", detail);
            }

            var identity = identityDetail?.Identity;
            if (identity is null
                || string.IsNullOrWhiteSpace(identity.Id)
                || !string.Equals(identity.Id, _configuredClientId, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidConfiguredIdentity("the response named a different or empty identity", detail);
            }

            if (string.Equals(identity.Kind, "retired", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidConfiguredIdentity("the identity is retired", detail);
            }

            SetClientId(identity.Id);
            return identity.Id;
        }

        var resp = await PostJsonAsync<ClientRegisterRequest, ClientRegisterResponse>(
            "/api/clients/register", new ClientRegisterRequest(displayName, kind), ct);
        if (!string.IsNullOrWhiteSpace(resp?.Id))
            SetClientId(resp!.Id);
        return resp?.Id ?? string.Empty;
    }

    private TaskServerException InvalidConfiguredIdentity(string reason, string detail)
        => new(
            (int)HttpStatusCode.Conflict,
            $"Configured RUNNER_CLIENT_ID '{_configuredClientId}' was not accepted by the Task Server: {reason}. " +
            $"Response: {Trim(detail)}. Choose an active registered host identity; startup will not create a replacement identity.");

    /// <summary>The client id this runner presents as X-Client-Id (server-assigned after registration).</summary>
    public string ClientId { get; private set; } = string.Empty;

    public bool UsesDurableTaskServer => _useV1;
    internal string RunnerInstanceId => $"{_options?.Hostname ?? Environment.MachineName}:{Environment.ProcessId}";
    internal int HostMaxParallelism => Math.Clamp(
        _centralHostMaxParallelism ?? _options?.HostMaxParallelism ?? 1,
        1,
        256);

    internal RunOutboxAuthority OutboxAuthority(string taskKey)
    {
        var authority = V1Authority(taskKey);
        return new RunOutboxAuthority(
            authority.RunId,
            taskKey,
            authority.Lease.RunnerId,
            authority.InstanceId,
            authority.Lease.LeaseId,
            authority.Lease.FencingToken);
    }

    /// <summary>How long the liveness probe waits before it calls the server unreachable.</summary>
    private const int HealthProbeTimeoutSeconds = 10;

    /// <summary>
    /// Liveness probe against the Task Server's open <c>/healthz</c> route. Returns
    /// <c>null</c> when the server answers 200; otherwise a short human-readable
    /// reason (HTTP status, timeout, or transport error). It never throws for an
    /// unreachable server - a dropped reverse tunnel is an expected, recoverable
    /// state, so the caller can report "connection lost" cleanly instead of letting
    /// a raw transport exception cascade through register/lease/launch. The probe
    /// uses its own short timeout so a black-holed tunnel fails fast rather than
    /// blocking on the 60 s request timeout.
    /// </summary>
    public async Task<string?> ProbeHealthAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HealthProbeTimeoutSeconds));
        try
        {
            using var resp = await _http.GetAsync("/healthz", timeout.Token);
            return resp.IsSuccessStatusCode
                ? null
                : $"server answered /healthz with HTTP {(int)resp.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a real shutdown, not a health failure - let the caller unwind
        }
        catch (OperationCanceledException)
        {
            return $"no response within {HealthProbeTimeoutSeconds}s (reverse tunnel down?)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void SetClientId(string clientId)
    {
        ClientId = clientId;
        _http.DefaultRequestHeaders.Remove("X-Client-Id");
        if (!string.IsNullOrWhiteSpace(clientId))
            _http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
    }

    public async Task<RunLeaseResponse> AcquireLeaseAsync(RunLeaseAcquireRequest req, CancellationToken ct)
    {
        if (_useV1)
            throw new TaskServerException(409, "Protocol v1 admits work through server-side claims; direct task-key acquisition is not supported.");
        return await PostJsonAsync<RunLeaseAcquireRequest, RunLeaseResponse>("/api/runner/lease/acquire", req, ct)
               ?? new RunLeaseResponse("Invalid", false, null, "Empty lease response.");
    }

    public async Task<RunnerClaimResponse> ClaimAsync(RunnerClaimRequest req, CancellationToken ct)
    {
        if (!_useV1)
        {
            var stamped = req with
            {
                EffectiveMaxParallelism = req.EffectiveMaxParallelism ?? HostMaxParallelism,
                EffectiveMaxParallelismAppliedAt =
                    req.EffectiveMaxParallelismAppliedAt ?? _centralHostMaxParallelismAppliedAt,
                BootstrapMaxParallelism = req.BootstrapMaxParallelism ?? _options?.HostMaxParallelism,
            };
            var legacy = await PostJsonAsync<RunnerClaimRequest, RunnerClaimResponse>(
                             "/api/runner/claim", stamped, ct)
                         ?? new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: "Empty claim response.");
            // The legacy claim plane carries the same central host policy as the
            // v1 plane, so a ceiling changed in Studio takes effect on the next
            // poll without restarting the daemon.
            AdoptCentralMaxParallelism(legacy.DesiredMaxParallelism);
            return legacy;
        }

        var claim = await PostJsonAsync<Contract.ClaimRequest, Contract.ClaimResponse>(
            $"/api/v1/runners/{Uri.EscapeDataString(req.RunnerId)}/claims",
            new Contract.ClaimRequest(
                req.RunnerId,
                RunnerInstanceId,
                req.RequestedTtlSeconds ?? 120,
                req.AvailableSlots,
                ToContract(req.Inventory),
                _options is null ? null : RunnerCapabilityProbe.CodingRequirements(_options),
                req.EffectiveMaxParallelism ?? HostMaxParallelism),
            ct);
        AdoptRuntimeCapacity(claim?.RuntimeCapacity);
        if (claim is null || !string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase)
            || claim.Task is null || claim.Run is null || claim.Lease is null)
            return new RunnerClaimResponse(
                RunnerClaimStatus.Empty,
                Message: claim?.Message ?? "No task available.",
                ReconciliationActions: FromContract(claim?.ReconciliationActions));

        var legacyLease = new RunLeaseInfoDto(
            claim.Task.TaskKey,
            claim.Lease.RunnerId,
            _options?.RunnerName ?? req.RunnerName,
            _options?.Hostname ?? req.Hostname,
            Environment.ProcessId,
            _options?.BackendName ?? req.BackendName,
            claim.Lease.LeaseId,
            claim.Lease.Fence,
            claim.Lease.AcquiredAt,
            claim.Lease.ExpiresAt,
            claim.Run.RunId);
        _v1Leases[claim.Task.TaskKey] = (claim.Run.RunId, legacyLease, RunnerInstanceId);
        if (!string.IsNullOrWhiteSpace(claim.Task.Body)) _v1TaskBodies[claim.Task.TaskKey] = claim.Task.Body;
        return new RunnerClaimResponse(
            RunnerClaimStatus.Claimed,
            claim.Task.TaskKey,
            claim.Task.TaskId,
            ProjectName: claim.Task.ProjectId,
            Lease: legacyLease,
            // The separated v1 resource contract currently carries task-project
            // identity but no repository registration. Use the runner's
            // configured fallback URL plus its stable repository identity for
            // the isolated compatibility profile. Reusing the Task Server
            // project id here would leave the durable result context unbound to
            // a repository; omitting it would release the claim before launch.
            ProjectId: RepositoryIdentity(_options?.GitRemote),
            RepositoryUrl: _options?.GitRemote,
            DefaultBranch: _options?.BaseBranch,
            RunId: claim.Run.RunId,
            LeaseInstanceId: RunnerInstanceId,
            ReconciliationActions: FromContract(claim.ReconciliationActions));
    }

    private void AdoptRuntimeCapacity(Contract.RuntimeCapacitySettingsDto? capacity)
        => AdoptCentralMaxParallelism(capacity?.MaxParallelism);

    /// <summary>
    /// Adopt a server-owned ceiling and remember when it took effect, so the
    /// host row can show whether the daemon is already running the central
    /// value or is still draining down to it.
    /// </summary>
    private void AdoptCentralMaxParallelism(int? maxParallelism)
    {
        if (maxParallelism is not (>= 1 and <= 256)) return;
        if (_centralHostMaxParallelism == maxParallelism) return;
        _centralHostMaxParallelism = maxParallelism;
        _centralHostMaxParallelismAppliedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Restore the v1 run-id and original attempt instance after a daemon restart.
    /// The Task Server fences attempts by this stable instance id; the replacement
    /// daemon process must not invent a new attempt while reattaching execution.
    /// </summary>
    public void RestoreRunAuthority(
        string taskKey,
        string? runId,
        string? leaseInstanceId,
        RunLeaseInfoDto lease)
    {
        if (!_useV1) return;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(leaseInstanceId))
            throw new InvalidDataException($"Persisted v1 authority for task '{taskKey}' is incomplete.");
        _v1Leases[taskKey] = (runId, lease, leaseInstanceId);
    }

    public async Task<Contract.ReviewClaimResponse> ClaimReviewAsync(
        Contract.ReviewClaimRequest request,
        CancellationToken ct)
    {
        if (!_useV1)
            throw new TaskServerException(409, "Remote review execution requires the versioned Task Server.");
        return await PostJsonAsync<Contract.ReviewClaimRequest, Contract.ReviewClaimResponse>(
                   $"/api/v1/runners/{Uri.EscapeDataString(request.ExecutorId)}/review-claims",
                   request with
                   {
                       RequiredCapabilities = request.RequiredCapabilities
                           ?? (_options is null
                               ? null
                               : RunnerCapabilityProbe.ReviewRequirements(_options)),
                   },
                   ct)
                ?? new Contract.ReviewClaimResponse("empty", Message: "Empty review claim response.");
    }

    public async Task<Contract.ReviewLeaseDto> RenewReviewLeaseAsync(
        string attemptId,
        Contract.ReviewLeaseRenewRequest request,
        CancellationToken ct)
        => await PostJsonAsync<Contract.ReviewLeaseRenewRequest, Contract.ReviewLeaseDto>(
               $"/api/v1/reviews/attempts/{Uri.EscapeDataString(attemptId)}/lease/renew",
               request,
               ct)
           ?? throw new TaskServerException(500, "Empty review lease renewal response.");

    public async Task<Contract.ReviewReportDto> ReportReviewAsync(
        string attemptId,
        Contract.ReviewReportRequest request,
        CancellationToken ct)
        => await PostJsonAsync<Contract.ReviewReportRequest, Contract.ReviewReportDto>(
               $"/api/v1/reviews/attempts/{Uri.EscapeDataString(attemptId)}/report",
               request,
               ct)
           ?? throw new TaskServerException(500, "Empty review report response.");

    public async Task<Contract.ReviewCleanupResponse> CleanupReviewAsync(
        string attemptId,
        Contract.ReviewCleanupRequest request,
        CancellationToken ct)
        => await PostJsonAsync<Contract.ReviewCleanupRequest, Contract.ReviewCleanupResponse>(
               $"/api/v1/reviews/attempts/{Uri.EscapeDataString(attemptId)}/cleanup",
               request,
               ct)
           ?? throw new TaskServerException(500, "Empty review cleanup response.");

    public async Task<Contract.ArtifactContentDto?> GetArtifactContentAsync(
        string runId,
        string artifactId,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"/api/v1/runs/{Uri.EscapeDataString(runId)}/artifacts/{Uri.EscapeDataString(artifactId)}/content",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new TaskServerException((int)response.StatusCode, $"Artifact fetch failed: {Trim(detail)}");
        return JsonSerializer.Deserialize<Contract.ArtifactContentDto>(detail, Json);
    }

    public async Task<RemoteChatWorkClaimResponse> ClaimProjectChatWorkAsync(
        RemoteChatWorkClaimRequest request,
        CancellationToken ct)
    {
        // Project chat is still a legacy monolith surface. A Runner connected
        // to the standalone v1 owner must not probe an endpoint that the Task
        // Server deliberately does not own.
        if (_useV1)
            return new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty);
        return await PostJsonAsync<RemoteChatWorkClaimRequest, RemoteChatWorkClaimResponse>(
                   "/api/runner/project-chat/claim", request, ct)
               ?? new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty);
    }

    public async Task<bool> RenewProjectChatWorkAsync(
        RemoteChatWorkRenewRequest request,
        CancellationToken ct)
    {
        try
        {
            _ = await PostJsonAsync<RemoteChatWorkRenewRequest, object>(
                "/api/runner/project-chat/renew", request, ct);
            return true;
        }
        catch (TaskServerException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<bool> CompleteProjectChatWorkAsync(
        RemoteChatWorkCompletionRequest request,
        CancellationToken ct)
    {
        try
        {
            _ = await PostJsonAsync<RemoteChatWorkCompletionRequest, object>(
                "/api/runner/project-chat/complete", request, ct);
            return true;
        }
        catch (TaskServerException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task ReportGitCapabilityAsync(string clientId, RunnerGitCapabilityRequest request, CancellationToken ct)
    {
        if (_useV1) return;
        // This endpoint belongs to the localhost client registry. Networked
        // Runner identities prove git capability locally before claiming work;
        // they do not gain access to legacy host administration routes.
        if (_usesServiceCredential)
            return;

        _ = await PostJsonAsync<RunnerGitCapabilityRequest, object>(
            $"/api/clients/{Uri.EscapeDataString(clientId)}/runner-git-capability", request, ct);
    }

    public async Task AdvertiseCapabilitiesAsync(
        IReadOnlyList<Contract.AdvertisedCapabilityDto> capabilities,
        Contract.HostTelemetrySnapshotDto? telemetry,
        long generation,
        CancellationToken ct)
    {
        if (!_useV1) return;
        var options = _options ?? throw new InvalidOperationException("Runner options are unavailable.");
        var request = new Contract.CapabilityAdvertisementRequest(
            options.RunnerId,
            RunnerInstanceId,
            Contract.CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow,
            180,
            generation,
            capabilities,
            telemetry);
        _ = await SendJsonAsync<Contract.CapabilityAdvertisementRequest, Contract.RunnerCapabilitySnapshotDto>(
            HttpMethod.Put,
            $"/api/v1/runners/{Uri.EscapeDataString(options.RunnerId)}/capabilities",
            request,
            ct);
    }

    public async Task ReportCapabilityFailureAsync(
        string capabilityKey,
        string classification,
        string reason,
        string idempotencyKey,
        string? claimKind,
        string? claimId,
        long? fence,
        CancellationToken ct)
    {
        if (!_useV1) return;
        var options = _options ?? throw new InvalidOperationException("Runner options are unavailable.");
        var request = new Contract.CapabilityFailureRequest(
            options.RunnerId,
            RunnerInstanceId,
            capabilityKey,
            classification,
            reason,
            DateTime.UtcNow,
            idempotencyKey,
            claimKind,
            claimId,
            fence);
        _ = await PostJsonAsync<Contract.CapabilityFailureRequest, Contract.CapabilityFailureResponse>(
            $"/api/v1/runners/{Uri.EscapeDataString(options.RunnerId)}/capability-failures",
            request,
            ct);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public async Task<RunLeaseResponse> RenewLeaseAsync(RunLeaseHeartbeatRequest req, CancellationToken ct)
    {
        if (!_useV1)
            return await PostJsonAsync<RunLeaseHeartbeatRequest, RunLeaseResponse>("/api/runner/lease/renew", req, ct)
                   ?? new RunLeaseResponse("Invalid", false, null, "Empty renew response.");
        var authority = V1Authority(req.TaskKey);
        var response = await PostJsonAsync<Contract.LeaseRenewRequest, Contract.LeaseResponse>(
            $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/lease/renew",
            new Contract.LeaseRenewRequest(
                req.RunnerId,
                authority.InstanceId,
                req.LeaseId,
                req.FencingToken,
                req.RequestedTtlSeconds ?? 120,
                ToContract(req.Inventory)),
            ct);
        if (response?.Lease is not null)
        {
            var updated = authority.Lease with { ExpiresAt = response.Lease.ExpiresAt };
            _v1Leases[req.TaskKey] = (authority.RunId, updated, authority.InstanceId);
            return new RunLeaseResponse(
                "Renewed",
                true,
                updated,
                ReconciliationActions: FromContract(response.ReconciliationActions));
        }
        return new RunLeaseResponse(
            response?.Status ?? "Invalid",
            false,
            authority.Lease,
            response?.Message,
            FromContract(response?.ReconciliationActions));
    }

    private static Contract.RunnerProcessInventory? ToContract(RunnerProcessInventory? inventory)
        => inventory is null
            ? null
            : new Contract.RunnerProcessInventory(
                inventory.ObservedAt,
                inventory.Processes.Select(process => new Contract.RunnerProcessInfo(
                    process.RunId,
                    process.TaskKey,
                    process.Pid,
                    process.Cwd,
                    process.StartedAt)).ToArray(),
                inventory.Reports?.Select(report => new Contract.RunnerInvariantReport(
                    report.ReportId,
                    report.Category,
                    report.DetectedAt,
                    report.Action,
                    report.Detail,
                    report.RunId,
                    report.TaskKey,
                    report.Pid)).ToArray(),
                inventory.AcknowledgedActionIds);

    private static IReadOnlyList<RunnerReconciliationAction>? FromContract(
        IReadOnlyList<Contract.RunnerReconciliationAction>? actions)
        => actions?.Select(action => new RunnerReconciliationAction(
            action.ActionId,
            action.Category,
            action.Action,
            action.Detail,
            action.Pid,
            action.RunId,
            action.TaskKey)).ToArray();

    public async Task<RunLeaseResponse> ReleaseLeaseAsync(RunLeaseReleaseRequest req, CancellationToken ct)
    {
        if (!_useV1)
            return await PostJsonAsync<RunLeaseReleaseRequest, RunLeaseResponse>("/api/runner/lease/release", req, ct)
                   ?? new RunLeaseResponse("Invalid", false, null, "Empty release response.");
        if (!_v1Leases.TryGetValue(req.TaskKey, out var cached))
        {
            _v1TaskBodies.TryRemove(req.TaskKey, out _);
            return new RunLeaseResponse("Released", false, null, "Run already completed and closed by Task Server.");
        }
        var authority = cached;
        var response = await PostJsonAsync<Contract.LeaseReleaseRequest, Contract.LeaseResponse>(
            $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/lease/release",
            new Contract.LeaseReleaseRequest(req.RunnerId, authority.InstanceId, req.LeaseId, req.FencingToken, "runner-process-missing"),
            ct);
        _v1Leases.TryRemove(req.TaskKey, out _);
        _v1TaskBodies.TryRemove(req.TaskKey, out _);
        return new RunLeaseResponse(response?.Status ?? "Released", false, authority.Lease, response?.Message);
    }

    public async Task<LogIngestResponse?> IngestLogsAsync(LogIngestRequest req, CancellationToken ct)
    {
        if (!_useV1) return await PostJsonAsync<LogIngestRequest, LogIngestResponse>("/api/runner/logs", req, ct);
        var authority = V1Authority(req.TaskKey);
        var appended = 0;
        foreach (var line in req.Lines)
        {
            var key = $"runner-log:{authority.RunId}:{line.Timestamp:O}:{appended}:{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line.Text)))[..12]}";
            var kind = ClassifyV1Event(line);
            await PostJsonAsync<Contract.EventIngestRequest, Contract.EventDto>(
                $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/events",
                new Contract.EventIngestRequest(
                    $"evt_{Guid.NewGuid():N}",
                    kind,
                    JsonSerializer.Serialize(line, Json),
                    key,
                    authority.Lease.FencingToken,
                    line.Timestamp),
                ct);
            appended++;
        }
        return new LogIngestResponse(req.TaskKey, appended);
    }

    private static string ClassifyV1Event(CliOutputLine line)
    {
        if (string.Equals(line.Stream, "system", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Text.Contains("runner transport disconnected", StringComparison.Ordinal))
                return Contract.LifecycleEventKinds.RunnerDisconnected;
            if (line.Text.Contains("runner transport reconnected", StringComparison.Ordinal))
                return Contract.LifecycleEventKinds.RunnerReconnected;
            return Contract.LifecycleEventKinds.RunnerTrace;
        }
        if (!string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase))
            return "runner.diagnostic";

        try
        {
            using var document = JsonDocument.Parse(line.Text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                if (type is not null
                    && (type.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || type.Contains("command", StringComparison.OrdinalIgnoreCase)))
                    return Contract.LifecycleEventKinds.ToolTrace;
            }
        }
        catch (JsonException)
        {
            // Plain agent text is still a typed agent-message event. The raw
            // line remains inside the bounded event payload for canonical replay.
        }

        return Contract.LifecycleEventKinds.AgentMessage;
    }

    public async Task<ArtifactIngestResponse?> UploadArtifactsAsync(ArtifactIngestRequest req, CancellationToken ct)
    {
        if (!_useV1) return await PostJsonAsync<ArtifactIngestRequest, ArtifactIngestResponse>("/api/runner/artifacts", req, ct);
        var authority = V1Authority(req.TaskKey);
        var files = new List<string>();
        foreach (var artifact in req.Artifacts)
        {
            var bytes = Convert.FromBase64String(artifact.ContentBase64);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var key = $"runner-artifact:{authority.RunId}:{artifact.Path}:{sha}";
            await PostJsonAsync<Contract.ArtifactIngestRequest, Contract.ArtifactDto>(
                $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/artifacts",
                new Contract.ArtifactIngestRequest(
                    $"art_{Guid.NewGuid():N}",
                    artifact.Path,
                    MediaType(artifact.Path),
                    artifact.ContentBase64,
                    sha,
                    key,
                    authority.Lease.FencingToken),
                ct);
            files.Add(artifact.Path);
        }
        return new ArtifactIngestResponse(req.TaskKey, files.Count, files);
    }

    public async Task<RemoteRunCompletionResponse?> CompleteRunAsync(RemoteRunCompletionRequest req, CancellationToken ct)
    {
        if (!_useV1) return await PostJsonAsync<RemoteRunCompletionRequest, RemoteRunCompletionResponse>("/api/runner/completion", req, ct);
        var authority = V1Authority(req.TaskKey);
        var typedOutcome = req.OutcomeDecision?.Outcome.ToString() ?? req.Outcome;
        _ = await PostJsonAsync<Contract.CompleteRunRequest, Contract.RunDto>(
            $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/completion",
            new Contract.CompleteRunRequest(
                req.RunnerId,
                authority.InstanceId,
                req.LeaseId,
                req.FencingToken,
                typedOutcome,
                req.Reason,
                IdempotencyKey: req.IdempotencyKey,
                OutcomeDecision: req.OutcomeDecision),
            ct);
        _v1Leases.TryRemove(req.TaskKey, out _);
        _v1TaskBodies.TryRemove(req.TaskKey, out _);
        return new RemoteRunCompletionResponse(req.TaskKey, typedOutcome, "4-auto-review");
    }

    public async Task<ResultHandoffAck> AcknowledgeResultHandoffAsync(
        RunOutboxAuthority authority,
        RunOutboxItem item,
        ImmutableResultEnvelope envelope,
        CancellationToken ct)
    {
        var digest = ResultEnvelopeDigest.Compute(envelope);
        return await SendJsonAsync<ResultHandoffRequest, ResultHandoffAck>(
                   HttpMethod.Put,
                   $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/result-handoff",
                   new ResultHandoffRequest(
                       authority.RunnerId,
                       authority.InstanceId,
                       authority.LeaseId,
                       authority.Fence,
                       item.Sequence,
                       item.IdempotencyKey,
                       digest,
                       envelope),
                   ct)
               ?? throw new TaskServerException(502, "Task Server returned an empty result handoff acknowledgement.");
    }

    public async Task SendOutboxItemAsync(
        RunOutboxAuthority authority,
        RunOutboxItem item,
        CancellationToken ct)
    {
        switch (item.Kind)
        {
            case "artifact":
            {
                var payload = JsonSerializer.Deserialize<DurableArtifactPayload>(item.PayloadJson, Json)
                              ?? throw new InvalidDataException("Durable artifact payload is empty.");
                await SendJsonAsync<Contract.ArtifactIngestRequest, Contract.ArtifactDto>(
                    HttpMethod.Post,
                    $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/artifacts",
                    new Contract.ArtifactIngestRequest(
                        $"art_{HashId(item.IdempotencyKey)}",
                        payload.Name,
                        payload.MediaType,
                        payload.ContentBase64,
                        payload.Sha256,
                        item.IdempotencyKey,
                        authority.Fence,
                        authority.RunnerId,
                        authority.InstanceId,
                        authority.LeaseId,
                        item.Sequence),
                    ct);
                return;
            }
            case "final-result":
            {
                var envelope = JsonSerializer.Deserialize<ImmutableResultEnvelope>(item.PayloadJson, Json)
                               ?? throw new InvalidDataException("Durable result envelope payload is empty.");
                _ = await AcknowledgeResultHandoffAsync(authority, item, envelope, ct);
                return;
            }
            case "completion":
            {
                var payload = JsonSerializer.Deserialize<DurableCompletionPayload>(item.PayloadJson, Json)
                              ?? throw new InvalidDataException("Durable completion payload is empty.");
                await SendJsonAsync<Contract.CompleteRunRequest, Contract.RunDto>(
                    HttpMethod.Post,
                    $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/completion",
                    new Contract.CompleteRunRequest(
                        authority.RunnerId,
                        authority.InstanceId,
                        authority.LeaseId,
                        authority.Fence,
                        payload.Outcome,
                        payload.Summary,
                        payload.ResultEnvelopeDigest,
                        item.IdempotencyKey,
                        item.Sequence,
                        payload.OutcomeDecision),
                    ct);
                _v1Leases.TryRemove(authority.TaskKey, out _);
                _v1TaskBodies.TryRemove(authority.TaskKey, out _);
                return;
            }
            default:
                var eventKind = $"runner.{item.Kind}";
                if (string.Equals(item.Kind, "log", StringComparison.Ordinal))
                {
                    var line = JsonSerializer.Deserialize<CliOutputLine>(item.PayloadJson, Json)
                               ?? throw new InvalidDataException("Durable log payload is empty.");
                    eventKind = ClassifyV1Event(line);
                }
                await SendJsonAsync<Contract.EventIngestRequest, Contract.EventDto>(
                    HttpMethod.Post,
                    $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/events",
                    new Contract.EventIngestRequest(
                        $"evt_{HashId(item.IdempotencyKey)}",
                        eventKind,
                        item.PayloadJson,
                        item.IdempotencyKey,
                        authority.Fence,
                        item.CreatedAt,
                        authority.RunnerId,
                        authority.InstanceId,
                        authority.LeaseId,
                        item.Sequence),
                    ct);
                return;
        }
    }

    public async Task ReportOutboxAsync(
        string runnerId,
        string instanceId,
        DurableRunOutbox outbox,
        CancellationToken ct)
    {
        var snapshot = outbox.Snapshot;
        _ = await SendJsonAsync<RunnerOutboxStatusRequest, RunnerOutboxStatusDto>(
            HttpMethod.Put,
            $"/api/v1/runners/{Uri.EscapeDataString(runnerId)}/outbox-status",
            new RunnerOutboxStatusRequest(
                instanceId,
                snapshot.LastSequence,
                snapshot.LastAcknowledgedSequence,
                snapshot.BacklogCount,
                snapshot.OldestUnacknowledgedSequence,
                snapshot.FinalHandoffState,
                outbox.Authority.RunId,
                snapshot.EnvelopeDigest,
                DateTime.UtcNow),
            ct);
    }

    public async Task<RemoteEpicPlanningPromptResponse?> GetEpicPlanningPromptAsync(
        RemoteEpicPlanningPromptRequest req, CancellationToken ct)
        => await PostJsonAsync<RemoteEpicPlanningPromptRequest, RemoteEpicPlanningPromptResponse>(
            "/api/runner/epic-planning-prompt", req, ct);

    public async Task<ExternalCompletionResponse?> CompleteAsync(string jobId, ExternalCompletionRequest req, CancellationToken ct)
    {
        if (_useV1)
        {
            var authority = V1Authority(jobId);
            var detail = JsonSerializer.Serialize(req, Json);
            await PostJsonAsync<Contract.EventIngestRequest, Contract.EventDto>(
                $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/events",
                new Contract.EventIngestRequest(
                    $"evt_{Guid.NewGuid():N}",
                    "runner.escalation",
                    detail,
                    $"runner-escalation:{authority.RunId}:{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(detail)))[..16]}",
                    authority.Lease.FencingToken),
                ct);
            _ = await PostJsonAsync<Contract.CompleteRunRequest, Contract.RunDto>(
                $"/api/v1/runs/{Uri.EscapeDataString(authority.RunId)}/completion",
                new Contract.CompleteRunRequest(
                    authority.Lease.RunnerId,
                    authority.InstanceId,
                    authority.Lease.LeaseId,
                    authority.Lease.FencingToken,
                    "blocked",
                    req.Summary),
                ct);
            _v1Leases.TryRemove(jobId, out _);
            _v1TaskBodies.TryRemove(jobId, out _);
            return new ExternalCompletionResponse(jobId, "4-auto-review", req.Source);
        }
        return await PostJsonAsync<ExternalCompletionRequest, ExternalCompletionResponse>(
            $"/api/tasks/{Uri.EscapeDataString(jobId)}/external-completion", req, ct);
    }

    /// <summary>Fetch a text file from the task's job folder, e.g. prompt.md. Returns null on 404.</summary>
    public async Task<string?> ReadTaskFileAsync(string jobId, string relativePath, CancellationToken ct)
    {
        if (_useV1 && string.Equals(relativePath, "prompt.md", StringComparison.OrdinalIgnoreCase))
            return _v1TaskBodies.TryGetValue(jobId, out var body) ? body : null;
        var url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/files/{relativePath}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<TResp?> PostJsonAsync<TReq, TResp>(string url, TReq body, CancellationToken ct)
        => await SendJsonAsync<TReq, TResp>(HttpMethod.Post, url, body, ct);

    private async Task<TResp?> SendJsonAsync<TReq, TResp>(HttpMethod method, string url, TReq body, CancellationToken ct)
    {
        // The standalone Task Server's established HTTP contract uses numeric
        // enum values. Legacy monolith DTOs continue to use string enums.
        var requestJson = typeof(TReq).Assembly == typeof(Contract.ProtocolRangeDto).Assembly
            ? TaskServerContractJson
            : Json;
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body, options: requestJson),
        };
        using var resp = await _http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new TaskServerException((int)resp.StatusCode, $"POST {url} -> {(int)resp.StatusCode}: {Trim(text)}");
        }
        return await resp.Content.ReadFromJsonAsync<TResp>(Json, ct);
    }

    private (string RunId, RunLeaseInfoDto Lease, string InstanceId) V1Authority(string taskKey)
        => _v1Leases.TryGetValue(taskKey, out var authority)
            ? authority
            : throw new TaskServerException(409, $"No v1 run authority is cached for task '{taskKey}'.");

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".md" or ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream",
    };

    internal static string MediaTypeForPath(string path) => MediaType(path);

    private static string HashId(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..32];

    internal static string? RepositoryIdentity(string? repositoryUrl)
        => RepositoryIdentityContract.FromUrl(repositoryUrl);

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>A non-success HTTP reply from the Task Server, carrying the status code for branching.</summary>
public sealed class TaskServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
