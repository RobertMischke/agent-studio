using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.Bus;
using AgentStudio.Management;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodexDeviceSignInTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "codex-device-sign-in-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TranscriptPolicy_ExtractsAnsiWrappedUrlAndDeviceCode()
    {
        var challenge = new CodexDeviceChallenge(null, null);
        challenge = CodexSignInPolicy.ObserveChallenge(
            challenge,
            "\u001b[36mOpen https://auth.openai.com/codex/device\u001b[0m");
        challenge = CodexSignInPolicy.ObserveChallenge(challenge, "Enter code ABCD-EFGH");

        Assert.True(challenge.IsComplete);
        Assert.Equal("https://auth.openai.com/codex/device", challenge.VerificationUrl);
        Assert.Equal("ABCD-EFGH", challenge.UserCode);
    }

    [Fact]
    public async Task FakeSshTranscript_StartsHostOwnedFlow_PollsCompletion_AndAuditsNoCode()
    {
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        var fake = new FakeSshProcessFactory([
            "To authenticate Codex:",
            "Open https://auth.openai.com/codex/device",
            "Enter code ABCD-EFGH",
            "provider-sign-in-status=authenticated",
            "provider-sign-in-restarted=agent-host.service",
            "provider-sign-in-probe-refresh=requested",
        ]);
        await using var service = new CodexDeviceSignInService(
            fake,
            bridge,
            NullLogger<CodexDeviceSignInService>.Instance);

        var started = await service.StartAsync(
            "agent-runner-01",
            new CodexSignInStartRequest("agent@runner-01"),
            "operator-7",
            CancellationToken.None);
        var status = service.Get("agent-runner-01", started.Handle);

        Assert.Equal("https://auth.openai.com/codex/device", started.VerificationUrl);
        Assert.Equal("ABCD-EFGH", started.UserCode);
        Assert.NotNull(status);
        Assert.Equal("completed", status!.State);
        Assert.DoesNotContain("ABCD-EFGH", status.Detail, StringComparison.Ordinal);
        Assert.Equal("ssh", fake.StartInfo?.FileName);
        Assert.Contains("agent@runner-01", fake.StartInfo!.ArgumentList);
        Assert.Contains("login --device-auth", fake.Process.StandardInput, StringComparison.Ordinal);
        Assert.Contains("login status", fake.Process.StandardInput, StringComparison.Ordinal);
        Assert.Contains("systemctl restart", fake.Process.StandardInput, StringComparison.Ordinal);

        IReadOnlyList<AgentMessage> audits = [];
        for (var attempt = 0; attempt < 50 && audits.Count == 0; attempt++)
        {
            await Task.Delay(20);
            audits = store.Recent(_workspace, project: null, limit: 10);
        }
        var audit = Assert.Single(audits);
        Assert.Equal("provider_sign_in", audit.Topic);
        Assert.Contains("agent-runner-01", audit.Summary);
        Assert.Contains("operator-7", audit.Summary);
        var serialized = System.Text.Json.JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("ABCD-EFGH", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.openai.com", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host;touch-x", "agent@runner")]
    [InlineData("runner-01", "agent@runner;touch-x")]
    public void Policy_RejectsShellCharacters(string hostId, string sshTarget)
    {
        Assert.NotNull(CodexSignInPolicy.Validate(hostId, new CodexSignInStartRequest(sshTarget)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (Exception exception) { SilentCatch.Note(exception, "CodexDeviceSignInTests cleanup"); }
    }

    internal sealed class FakeSshProcessFactory(IReadOnlyList<string> output) : ICodexSignInSshProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public FakeSshProcess Process { get; } = new(output);

        public ICodexSignInSshProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return Process;
        }
    }

    internal sealed class FakeSshProcess(IReadOnlyList<string> output) : ICodexSignInSshProcess
    {
        private readonly ConcurrentQueue<string> _stdout = new(output);
        public string StandardInput { get; private set; } = string.Empty;
        public int ExitCode => 0;

        public Task WriteStandardInputAsync(string value, CancellationToken cancellationToken)
        {
            StandardInput = value;
            return Task.CompletedTask;
        }

        public Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stdout.TryDequeue(out var line) ? line : null);

        public Task<string?> ReadErrorLineAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
