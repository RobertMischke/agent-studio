using OrchestratorApi.Services.Cli;
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
    public void IsCompatibleSessionName_AcceptsUuidsRejectsSlugs()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var svc = new CodexCliService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexCliService>.Instance,
            cfg,
            new OrchestratorApi.Services.Pty.CodexModelDiscovery(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorApi.Services.Pty.CodexModelDiscovery>.Instance,
                cfg));

        Assert.True(svc.IsCompatibleSessionName("019dee65-7a9b-7843-bfd9-06e555fff02b"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        Assert.False(svc.IsCompatibleSessionName("taskboard-fix-bug-202604282114"));
    }
}
