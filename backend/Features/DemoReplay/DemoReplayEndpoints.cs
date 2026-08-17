using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplay;

/// <summary>
/// The single narrow mutation the public demo exposes to a replay credential. It
/// appends simulated runner lifecycle events to fixture tasks and does nothing
/// else: no claim, no lease, no prompt, no decision, no lane transition, no file
/// or Git state. Authorization for this route lives in
/// <see cref="AccessSecurityMiddleware"/>, which requires the exclusive
/// <see cref="RunnerScopes.DemoReplay"/> scope before the handler is reached.
/// </summary>
public static class DemoReplayEndpoints
{
    public static void MapDemoReplayEndpoints(this WebApplication app)
    {
        var options = DemoReplayOptions.FromConfiguration(app.Configuration);

        app.MapPost("/api/runner/replay/events", async (
            DemoReplayEventRequest request,
            HttpContext context,
            AccessSecurityStore security,
            ITaskScanner scanner,
            RunnerEventJournal journal,
            DemoReplayEpochLedger ledger,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.DemoReplay");
            if (request?.Frame is null)
                return Denied(logger, DemoReplayDenialCodes.RequestInvalid, "A replay frame is required.");

            var frame = request.Frame;

            // Seals are always checked against the pinned trace identity, never
            // against the values the caller declared. A caller that supplies a
            // different trace or digest is rejected by the policy below.
            var signatureValid = DemoReplayTraceSignature.VerifyFrame(
                options.TraceId, options.TraceDigest, frame, request.Signature, options.PublicKeyBase64);

            var admission = DemoReplayAdmissionPolicy.Evaluate(
                options,
                ledger.Peek(),
                new DemoReplayAdmissionRequest(
                    request.TraceId,
                    request.TraceDigest,
                    request.Epoch,
                    frame.Sequence,
                    frame.TaskKey,
                    frame.Kind,
                    signatureValid));
            if (!admission.Admitted)
                return Denied(logger, admission.DenialCode!, admission.Message!);

            var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
                string.Equals(candidate.TaskKey, frame.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Key, frame.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (task is null)
                return Results.NotFound(new { error = "task-not-found", message = "The replay frame targets a task that is not in this scene." });

            // The cursor moves before the write so two concurrent frames cannot
            // both land on the same sequence.
            if (!ledger.TryAdvance(request.Epoch, frame.Sequence))
                return Denied(
                    logger,
                    DemoReplayDenialCodes.SequenceNotMonotonic,
                    "Replay sequences must increase strictly inside one epoch.");

            var timestamp = request.OccurredAt == default
                ? DateTime.UtcNow
                : request.OccurredAt.ToUniversalTime();
            await journal.AppendAsync(task, ToRecordedEvent(request, frame, timestamp), ct);

            var runner = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
            if (runner is not null)
            {
                security.AppendRunAudit(new RunSecurityAuditEvent(
                    timestamp,
                    "replay:" + frame.Kind,
                    frame.TaskKey,
                    task.ProjectName,
                    "automation:demo-replay",
                    runner.RunnerId,
                    runner.CredentialId,
                    Outcome: DemoReplayOrigins.Simulated));
            }

            return Results.Accepted(value: new DemoReplayEventAccepted(
                request.Epoch, frame.Sequence, frame.TaskKey, frame.Kind, DemoReplayOrigins.Simulated));
        });
    }

    private static RunnerRecordedEvent ToRecordedEvent(
        DemoReplayEventRequest request,
        DemoReplayFrame frame,
        DateTime timestamp)
        => new()
        {
            Id = $"replay:{request.Epoch}:{frame.Sequence}",
            Kind = frame.Kind,
            Timestamp = timestamp,
            Origin = DemoReplayOrigins.Simulated,
            SessionId = frame.SessionId,
            TurnId = frame.TurnId,
            RunIndex = frame.RunIndex,
            Cli = frame.Cli,
            Model = frame.Model,
            ThinkingLevel = frame.ThinkingLevel,
            DurationMs = frame.DurationMs,
            InputTokens = frame.InputTokens,
            OutputTokens = frame.OutputTokens,
            ReasoningTokens = frame.ReasoningTokens,
            Message = frame.Message,
        };

    private static IResult Denied(ILogger logger, string code, string message)
    {
        logger.LogWarning("Demo replay frame denied: {Code}", code);
        return Results.Json(new { error = code, message }, statusCode: 403);
    }
}
