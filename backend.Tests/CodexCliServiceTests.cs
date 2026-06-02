using Microsoft.Extensions.Configuration;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Models;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks <see cref="CodexCliService"/>'s session-UUID capture path. The
/// fixtures are real <c>codex exec --json</c> frame shapes; without this
/// regression the per-job session store stays empty and every follow-up
/// rebuilds context from disk via Recovery, discarding Codex's own
/// prompt-cache (see bug-codex-session-id-not-captured-from-thread-started).
/// </summary>
public class CodexCliServiceTests
{
    [Fact]
    public void TryExtractSessionId_ThreadStartedFrame_ReturnsThreadId()
    {
        // codex-cli >= 0.128 (real frame shape).
        const string frame = """{"type":"thread.started","thread_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            CodexCliService.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_LegacySessionMetaPayloadId_StillReturnsId()
    {
        // Older codex-cli builds wrapped the id under payload.id.
        const string frame = """{"type":"session_meta","payload":{"id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            CodexCliService.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_LegacySessionMetaRootId_StillReturnsId()
    {
        // Some builds put the id at session_meta.session_id (root).
        const string frame = """{"type":"session_meta","session_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            CodexCliService.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_NonUuidThreadId_ReturnsNull()
    {
        // Guard: a non-UUID would break `codex exec resume`, so reject it
        // here rather than persisting a value the CLI cannot consume.
        const string frame = """{"type":"thread.started","thread_id":"not-a-uuid"}""";
        Assert.Null(CodexCliService.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_OtherFrameTypes_ReturnNull()
    {
        Assert.Null(CodexCliService.TryExtractSessionId(
            """{"type":"turn.started"}"""));
        Assert.Null(CodexCliService.TryExtractSessionId(
            """{"type":"item.completed","item":{"type":"agent_message","text":"hi"}}"""));
        Assert.Null(CodexCliService.TryExtractSessionId(
            """{"type":"turn.completed","usage":{"input_tokens":10}}"""));
    }

    [Fact]
    public void TryExtractSessionId_NonJsonOrEmpty_ReturnNull()
    {
        Assert.Null(CodexCliService.TryExtractSessionId(null));
        Assert.Null(CodexCliService.TryExtractSessionId(""));
        Assert.Null(CodexCliService.TryExtractSessionId("not-json"));
        Assert.Null(CodexCliService.TryExtractSessionId("[1,2,3]"));
    }

    [Fact]
    public void TryExtractSessionId_MalformedJson_ReturnsNullDoesNotThrow()
    {
        Assert.Null(CodexCliService.TryExtractSessionId(
            """{"type":"thread.started","thread_id":"""));
    }

    [Fact]
    public void BuildSystemPromptPrefix_NonWindows_HasSentinelHintOnly()
    {
        var prefix = CodexCliService.BuildSystemPromptPrefix(isWindows: false);

        Assert.Contains("[[TASK_DONE]]", prefix);
        Assert.Contains("[[TASK_BLOCKED:", prefix);
        Assert.Contains("[[TASK_NEEDS_INPUT:", prefix);
        Assert.Contains("[[TASK_NOOP]]", prefix);
        Assert.DoesNotContain("windows", prefix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateProcessAsUserW", prefix);
        Assert.EndsWith("\n\n", prefix);
    }

    [Fact]
    public void BuildSystemPromptPrefix_Windows_AppendsNoShellHint()
    {
        var prefix = CodexCliService.BuildSystemPromptPrefix(isWindows: true);

        Assert.Contains("[[TASK_DONE]]", prefix);
        Assert.Contains("windows sandbox: runner error", prefix);
        Assert.Contains("CreateProcessAsUserW failed", prefix);
        Assert.Contains("[[TASK_BLOCKED:windows-sandbox]]", prefix);
        Assert.EndsWith("\n\n", prefix);
    }

    [Fact]
    public void BuildSystemPromptPrefix_StaysShort()
    {
        // The prefix is paid on every invocation including resumes whose
        // user prompt is one sentence. Lock the upper bound so a future
        // edit can't bloat into a multi-paragraph essay.
        var win = CodexCliService.BuildSystemPromptPrefix(isWindows: true);
        var posix = CodexCliService.BuildSystemPromptPrefix(isWindows: false);

        Assert.True(win.Length < 900, $"Windows prefix grew to {win.Length} chars");
        Assert.True(posix.Length < 500, $"Non-Windows prefix grew to {posix.Length} chars");
    }

    [Fact]
    public void ResolveInvocationModel_ReplacesForeignClaudeModelWithCodexDefault()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodexCli:Model"] = "gpt-5-codex"
            })
            .Build();

        Assert.Equal("gpt-5-codex",
            CodexCliService.ResolveInvocationModel("claude-opus-4-7", cfg));
    }

    [Fact]
    public void ResolveInvocationModel_PreservesExplicitCodexModel()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Assert.Equal("gpt-5.5",
            CodexCliService.ResolveInvocationModel("gpt-5.5", cfg));
    }

    [Fact]
    public void ResolveInvocationModel_DefaultsEmptyModelToFallback()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Assert.Equal(CodexCliService.FallbackModel,
            CodexCliService.ResolveInvocationModel(null, cfg));
    }

    [Fact]
    public void BuildStartInfo_LongPromptKeepsPromptOutOfArgvAndUsesStdin()
    {
        var svc = BuildService();
        var prompt = new string('x', 12_000);

        var psi = svc.BuildStartInfoForTest(
            prompt,
            workingDirectory: Environment.CurrentDirectory,
            sessionName: "019dee65-7a9b-7843-bfd9-06e555fff02b",
            resumeSession: true,
            model: "claude-opus-4-7");

        Assert.DoesNotContain(prompt, psi.ArgumentList);
        Assert.Contains("-", psi.ArgumentList);
        Assert.Contains("gpt-5-codex", psi.ArgumentList);
        Assert.DoesNotContain("claude-opus-4-7", psi.ArgumentList);

        var argvText = psi.FileName + " " + string.Join(" ", psi.ArgumentList);
        Assert.True(argvText.Length < 8000, $"Codex argv grew to {argvText.Length} chars");

        var stdin = svc.BuildPromptStdinPayloadForTest(
            prompt,
            sessionName: "019dee65-7a9b-7843-bfd9-06e555fff02b",
            resumeSession: true,
            model: "claude-opus-4-7");
        Assert.NotNull(stdin);
        Assert.Contains(prompt, stdin);
        Assert.Contains("[[TASK_DONE]]", stdin);
    }

    [Fact]
    public void BuildStartInfo_ArgvSizeDoesNotGrowWithReissuePromptLength()
    {
        var svc = BuildService();
        var shortRetry = "Reissue: do the work.";
        var longRetry = "Reissue: " + new string('y', 20_000);

        var shortArgs = svc.BuildStartInfoForTest(
            shortRetry,
            Environment.CurrentDirectory,
            sessionName: "019dee65-7a9b-7843-bfd9-06e555fff02b",
            resumeSession: true,
            model: "gpt-5-codex").ArgumentList.ToArray();
        var longArgs = svc.BuildStartInfoForTest(
            longRetry,
            Environment.CurrentDirectory,
            sessionName: "019dee65-7a9b-7843-bfd9-06e555fff02b",
            resumeSession: true,
            model: "gpt-5-codex").ArgumentList.ToArray();

        Assert.Equal(shortArgs, longArgs);
    }

    [Fact]
    public void StartedLine_UsesNormalizedCodexModel()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var model = CodexCliService.ResolveInvocationModel("claude-opus-4-7", cfg);

        var line = CliExecutionServiceBase.BuildStartedLineText(
            CliTypes.Codex,
            processId: 1234,
            model,
            sessionName: null,
            resumeSession: false);

        Assert.Contains("model=gpt-5-codex", line);
        Assert.DoesNotContain("claude-opus-4-7", line);
    }

    [Fact]
    public void TryExtractCommandExecution_ParsesCanonicalFrame()
    {
        // Real Codex frame shape from the 2026-05-12 Lotta-dashboard bug.
        const string frame = """{"type":"item.completed","item":{"id":"item66","type":"command_execution","command":"pwsh.exe -Command \".\\serve.ps1 status\"","aggregated_output":"ng serve laeuft nicht.\r\nFalse\r\n","exit_code":0,"status":"completed"}}""";
        var cap = CodexCliService.TryExtractCommandExecution(frame);
        Assert.NotNull(cap);
        Assert.Equal(0, cap!.Value.ExitCode);
        Assert.Contains("serve.ps1", cap.Value.Command);
        Assert.Contains("ng serve laeuft nicht", cap.Value.OutputTail);
    }

    [Fact]
    public void TryExtractCommandExecution_TailTruncatedForLongOutput()
    {
        var bigOutput = new string('y', 1000);
        var frame = "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"x\",\"exit_code\":0,\"aggregated_output\":\""
                  + bigOutput + "\"}}";
        var cap = CodexCliService.TryExtractCommandExecution(frame);
        Assert.NotNull(cap);
        Assert.True(cap!.Value.OutputTail!.Length <= 400);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"hi\"}}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{")]
    public void TryExtractCommandExecution_ReturnsNullForUnrelatedOrBrokenFrames(string? line)
    {
        Assert.Null(CodexCliService.TryExtractCommandExecution(line));
    }

    [Fact]
    public void IsCompatibleSessionName_AcceptsUuidsRejectsSlugs()
    {
        var svc = BuildService();

        Assert.True(svc.IsCompatibleSessionName("019dee65-7a9b-7843-bfd9-06e555fff02b"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        Assert.False(svc.IsCompatibleSessionName("taskboard-fix-bug-202604282114"));
    }

    private static CodexCliService BuildService()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new CodexCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexCliService>.Instance,
            cfg,
            new OrchestratorApi.Services.Pty.CodexModelDiscovery(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Pty.CodexModelDiscovery>.Instance,
                cfg),
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
    }
}
