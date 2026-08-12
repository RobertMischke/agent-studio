using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteReviewReportEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-evidence-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preparation_failure_card_names_exact_command_and_persists_complete_streams()
    {
        Directory.CreateDirectory(_root);
        var stdout = Encoding.UTF8.GetBytes("full install stdout\n");
        var stderr = Encoding.UTF8.GetBytes("full install stderr\n");
        var stdoutDigest = Digest(stdout);
        var stderrDigest = Digest(stderr);
        var command = new ReviewCommandEvidenceDto(
            "prepare-dependencies",
            "preparation",
            "/bin/bash",
            ["-lc", "dotnet restore Studio.slnx && npm --prefix frontend ci"],
            new string('a', 40),
            new string('a', 40),
            new string('b', 40),
            DateTime.UtcNow.AddSeconds(-3),
            DateTime.UtcNow,
            9,
            null,
            stdoutDigest,
            stderrDigest,
            Phase: "preparation",
            WorkspaceRole: "candidate",
            Budget: new ReviewCommandBudgetEvidenceDto("review-command", 120_000, 3_000, false));
        var request = new ReviewReportRequest(
            "reviewer",
            "instance",
            "lease",
            1,
            "report-key",
            "ReviewInfra",
            "PreparationFailed",
            "Dependency preparation failed.",
            new ReviewWorkspaceProofDto(
                "repo", new string('a', 40), new string('a', 40), new string('b', 40),
                false, false, "workspace", "review-attempt-f1"),
            new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [command],
            [
                Artifact("candidate.prepare-dependencies.stdout.log", stdout, stdoutDigest),
                Artifact("candidate.prepare-dependencies.stderr.log", stderr, stderrDigest),
            ],
            []);

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root,
            "attempt-1",
            "subject-1",
            request,
            new string('c', 64),
            DateTime.UtcNow,
            default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains("| preparation | candidate | prepare-dependencies |", report, StringComparison.Ordinal);
        Assert.Contains(
            "`/bin/bash -lc dotnet restore Studio.slnx && npm --prefix frontend ci`",
            report,
            StringComparison.Ordinal);
        Assert.Contains("review-command: 3000/120000 ms", report, StringComparison.Ordinal);
        Assert.Contains("[stdout](remote-review-attempt-1-candidate_prepare-dependencies_stdout_log)", report, StringComparison.Ordinal);
        Assert.Contains("[stderr](remote-review-attempt-1-candidate_prepare-dependencies_stderr_log)", report, StringComparison.Ordinal);
        Assert.Equal(
            stdout,
            await File.ReadAllBytesAsync(Path.Combine(
                _root, "remote-review-attempt-1-candidate_prepare-dependencies_stdout_log")));
        Assert.Equal(
            stderr,
            await File.ReadAllBytesAsync(Path.Combine(
                _root, "remote-review-attempt-1-candidate_prepare-dependencies_stderr_log")));
        var json = await File.ReadAllTextAsync(Path.Combine(
            _root, "remote-review-grade-attempt-1.json"));
        Assert.Contains("\"leaseId\": \"lease\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hostId\": \"host\"", json, StringComparison.Ordinal);
    }

    private static ReviewArtifactEvidenceDto Artifact(string name, byte[] content, string digest)
        => new(
            name,
            "text/plain; charset=utf-8",
            digest,
            content.LongLength,
            Convert.ToBase64String(content));

    private static string Digest(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
