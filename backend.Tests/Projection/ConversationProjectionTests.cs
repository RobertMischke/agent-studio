using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F22 backend foundation. Covers the markdown renderer, image rewriter,
/// XSS sanitizer, in-memory cache, the CLI-output source (including the
/// stream-json newline fix that triggered this work), and the projector's
/// merge + sort + cache semantics. Bundled in one file so the new code
/// has a single review surface.
/// </summary>
public class ConversationProjectionTests
{
    // ───────────────────────────────────────────────────────────────────────
    // ImageUrlRewriter
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ImageRewriter_RelativeAttachment_BecomesAbsoluteApiUrl()
    {
        var html = "<p><img src=\"attachments/shot.png\" alt=\"first\" /></p>";
        var ctx = new ImageContext { JobId = "job-1" };
        var result = ImageUrlRewriter.Rewrite(html, ctx);

        Assert.Contains("/api/tasks/job-1/attachments/shot.png", result);
        Assert.Contains("data-lightbox-src=\"/api/tasks/job-1/attachments/shot.png\"", result);
        Assert.Contains("loading=\"lazy\"", result);
        Assert.Contains("alt=\"first\"", result);
    }

    [Fact]
    public void ImageRewriter_ResultsFolder_RoutesThroughResultsHandler()
    {
        var html = "<img src=\"results/before.png\" alt=\"r\" />";
        var ctx = new ImageContext { JobId = "job-1" };
        Assert.Contains("/api/tasks/job-1/results/before.png", ImageUrlRewriter.Rewrite(html, ctx));
    }

    [Fact]
    public void ImageRewriter_NestedResultsFolder_RoutesThroughScreenshotHandler()
    {
        var html = "<img src=\"results/playwright/spec-name/proof.png\" alt=\"proof\" />";
        var ctx = new ImageContext { JobId = "job-1", WatchPath = "C:/work/project" };
        var result = ImageUrlRewriter.Rewrite(html, ctx);

        Assert.Contains("/api/tasks/job-1/screenshot?path=playwright%2Fspec-name%2Fproof.png&amp;watchPath=C%3A%2Fwork%2Fproject", result);
        Assert.Contains("data-lightbox-src=\"/api/tasks/job-1/screenshot?path=playwright%2Fspec-name%2Fproof.png&amp;watchPath=C%3A%2Fwork%2Fproject\"", result);
    }

    [Fact]
    public void ImageRewriter_TopLevelResultsFolder_KeepsResultsHandlerWithWatchPath()
    {
        var html = "<img src=\"results/before.png\" alt=\"r\" />";
        var ctx = new ImageContext { JobId = "job-1", WatchPath = "C:/work/project" };
        var result = ImageUrlRewriter.Rewrite(html, ctx);

        Assert.Contains("/api/tasks/job-1/results/before.png?watchPath=C%3A%2Fwork%2Fproject", result);
    }

    [Fact]
    public void ImageRewriter_ChatAttachments_UsesProjectScopedHandler()
    {
        var html = "<img src=\"chat-attachments/screen.png\" alt=\"c\" />";
        var ctx = new ImageContext { JobId = "job-1", ProjectName = "agent-taskboard" };
        Assert.Contains("/api/runner/agent-taskboard/orchestrator-chat/attachments/screen.png",
            ImageUrlRewriter.Rewrite(html, ctx));
    }

    [Fact]
    public void ImageRewriter_AbsoluteHttp_StaysIntact()
    {
        var html = "<img src=\"https://example.com/x.png\" alt=\"x\" />";
        var ctx = new ImageContext { JobId = "job-1" };
        var result = ImageUrlRewriter.Rewrite(html, ctx);
        Assert.Contains("src=\"https://example.com/x.png\"", result);
    }

    [Fact]
    public void ImageRewriter_TraversalAttempt_StrippedToAltText()
    {
        var html = "<img src=\"../../../etc/passwd\" alt=\"oops\" />";
        var ctx = new ImageContext { JobId = "job-1" };
        var result = ImageUrlRewriter.Rewrite(html, ctx);
        Assert.DoesNotContain("../", result);
        Assert.DoesNotContain("<img", result);
        Assert.Contains("oops", result);
    }

    [Fact]
    public void ImageRewriter_BareFilename_RoutedToAttachments()
    {
        var html = "<img src=\"diagram.png\" alt=\"d\" />";
        var ctx = new ImageContext { JobId = "job-1" };
        Assert.Contains("/api/tasks/job-1/attachments/diagram.png",
            ImageUrlRewriter.Rewrite(html, ctx));
    }

    // ───────────────────────────────────────────────────────────────────────
    // MarkdigRenderer + Sanitizer
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renderer_PlainMarkdown_ProducesParagraphsAndCode()
    {
        var renderer = new MarkdigRenderer();
        var html = renderer.ToHtml("hello\n\nworld", new ImageContext { JobId = "job" });

        Assert.Contains("<p>hello</p>", html);
        Assert.Contains("<p>world</p>", html);
    }

    [Fact]
    public void Renderer_ScriptTag_IsRemovedBySanitizer()
    {
        var renderer = new MarkdigRenderer();
        var html = renderer.ToHtml("<script>alert(1)</script>hello",
            new ImageContext { JobId = "job" });

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains("hello", html);
    }

    [Fact]
    public void Renderer_OnclickAttribute_IsStripped()
    {
        var renderer = new MarkdigRenderer();
        var html = renderer.ToHtml(
            "<a href=\"https://safe.example/\" onclick=\"evil()\">link</a>",
            new ImageContext { JobId = "job" });

        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil()", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renderer_JavascriptHref_IsStrippedByScheme()
    {
        var renderer = new MarkdigRenderer();
        var html = renderer.ToHtml(
            "[click](javascript:alert(1))",
            new ImageContext { JobId = "job" });

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renderer_RewritesRelativeImage_DuringPipeline()
    {
        var renderer = new MarkdigRenderer();
        var html = renderer.ToHtml("![shot](attachments/foo.png)",
            new ImageContext { JobId = "job-7" });

        Assert.Contains("/api/tasks/job-7/attachments/foo.png", html);
    }

    [Fact]
    public void Renderer_NewlineFromStreamJsonBody_RendersAsRealBreak()
    {
        // F22 trigger: the CLI used to log message bodies as one line with
        // \n escapes; the unescaper turns those into real newlines BEFORE
        // markdown sees the text, so we end up with two paragraphs.
        var renderer = new MarkdigRenderer();
        var unescaped = CliOutputSource.UnescapeStreamJsonBody("first\\n\\nsecond");
        var html = renderer.ToHtml(unescaped, new ImageContext { JobId = "job" });

        Assert.Contains("<p>first</p>", html);
        Assert.Contains("<p>second</p>", html);
    }

    // ───────────────────────────────────────────────────────────────────────
    // ConversationCache
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_HitWhenMTimesUnchanged()
    {
        var cache = new ConversationCache();
        var mt = new Dictionary<string, DateTime> { ["cli"] = new(2026, 1, 1) };
        var events = new List<ProjectedEvent> { new() { Id = "a", Kind = "message.user" } };

        cache.Set("job-1", events, mt);
        Assert.True(cache.TryGet("job-1", mt, out var hit));
        Assert.Single(hit);
    }

    [Fact]
    public void Cache_MissWhenAnyMTimeMoves()
    {
        var cache = new ConversationCache();
        var mt = new Dictionary<string, DateTime> { ["cli"] = new(2026, 1, 1) };
        cache.Set("job-1", Array.Empty<ProjectedEvent>(), mt);

        var newer = new Dictionary<string, DateTime> { ["cli"] = new(2026, 1, 2) };
        Assert.False(cache.TryGet("job-1", newer, out _));
    }

    [Fact]
    public void Cache_LruEviction_DropsColdestEntry()
    {
        var cache = new ConversationCache(capacity: 2);
        var mt = new Dictionary<string, DateTime> { ["cli"] = new(2026, 1, 1) };

        cache.Set("a", Array.Empty<ProjectedEvent>(), mt);
        cache.Set("b", Array.Empty<ProjectedEvent>(), mt);
        // Touch 'a' so 'b' becomes coldest.
        cache.TryGet("a", mt, out _);
        cache.Set("c", Array.Empty<ProjectedEvent>(), mt);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("a", mt, out _));
        Assert.True(cache.TryGet("c", mt, out _));
        Assert.False(cache.TryGet("b", mt, out _));
    }

    // ───────────────────────────────────────────────────────────────────────
    // CliOutputSource
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnescapeStreamJsonBody_HandlesStandardEscapes()
    {
        var input = "line one\\nline two\\twith tab\\rand cr\\\\real\\\"quote";
        var output = CliOutputSource.UnescapeStreamJsonBody(input);
        Assert.Equal("line one\nline two\twith tab\rand cr\\real\"quote", output);
    }

    [Fact]
    public void UnescapeStreamJsonBody_NoBackslash_ReturnsInputUnchanged()
    {
        var input = "plain text without escapes";
        Assert.Same(input, CliOutputSource.UnescapeStreamJsonBody(input));
    }

    [Fact]
    public void UnescapeStreamJsonBody_UnknownEscape_LeftAsIs()
    {
        var input = @"path C:\Users\rmisc\file.txt";
        Assert.Equal(input, CliOutputSource.UnescapeStreamJsonBody(input));
    }

    [Fact]
    public void CliOutputSource_Classifies_UserOrchestratorAgent()
    {
        var u = CliOutputSource.Classify(new CliOutputLine
        {
            Stream = "user",
            Text = "do the thing",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }, 0)!;
        Assert.Equal("message.user", u.Kind);
        Assert.Equal("user", u.Role);

        var o = CliOutputSource.Classify(new CliOutputLine
        {
            Stream = "orchestrator",
            Text = "[watchdog] quiet for 30s",
            Timestamp = DateTime.UtcNow
        }, 1)!;
        Assert.Equal("supervisor.wait", o.Kind);
        Assert.Equal(ProjectedEventSeverity.Warn, o.Severity);

        var sd = CliOutputSource.Classify(new CliOutputLine
        {
            Stream = "orchestrator",
            Text = "[schema-drift] expected MetaCycleReport",
            Timestamp = DateTime.UtcNow
        }, 2)!;
        Assert.Equal("system.schemaDrift", sd.Kind);

        var d = CliOutputSource.Classify(new CliOutputLine
        {
            Stream = "orchestrator",
            Text = "[reissue] sending follow-up to agent",
            Timestamp = DateTime.UtcNow
        }, 3)!;
        Assert.Equal("decision.orchestrator", d.Kind);

        var a = CliOutputSource.Classify(new CliOutputLine
        {
            Stream = "stdout",
            Text = "agent output line",
            Timestamp = DateTime.UtcNow
        }, 4)!;
        Assert.Equal("message.taskAgent", a.Kind);
        Assert.Equal("agent", a.Role);
    }

    [Fact]
    public async Task CliOutputSource_ReadsLogFile_AndReportsMTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "f22-cli-src-" + Guid.NewGuid());
        var logsDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, "cli-output.log");
        await File.WriteAllTextAsync(logPath,
            "[12:00:00.000] [user] hi\n[12:00:01.000] [stdout] hello\n");

        try
        {
            var src = new CliOutputSource();
            var info = new TaskInfo { Id = "job-x", FolderPath = dir };

            var events = await src.ReadAsync(info, CancellationToken.None);
            Assert.Equal(2, events.Count);
            Assert.Equal("message.user", events[0].Kind);
            Assert.Equal("message.taskAgent", events[1].Kind);
            Assert.True(src.GetSourceMTimeUtc(info) > DateTime.MinValue);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CliOutputSource_MissingLog_ReturnsEmptyAndMinMTime()
    {
        var src = new CliOutputSource();
        var info = new TaskInfo
        {
            Id = "job-none",
            FolderPath = Path.Combine(Path.GetTempPath(), "this-folder-does-not-exist-" + Guid.NewGuid())
        };
        Assert.Empty(src.ReadAsync(info, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(DateTime.MinValue, src.GetSourceMTimeUtc(info));
    }

    // ───────────────────────────────────────────────────────────────────────
    // ConversationProjector
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Projector_MergesSourcesByTimestamp_AndRendersBodies()
    {
        var renderer = new MarkdigRenderer();
        var cache = new ConversationCache();
        var sources = new IConversationEventSource[]
        {
            new StubSource("a", new[]
            {
                new RawSourceEvent
                {
                    Id = "a1", Kind = "message.user", SourceKind = "a",
                    TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc),
                    BodyMarkdown = "second"
                }
            }),
            new StubSource("b", new[]
            {
                new RawSourceEvent
                {
                    Id = "b1", Kind = "message.taskAgent", SourceKind = "b",
                    TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc),
                    BodyMarkdown = "first"
                }
            })
        };

        var projector = new ConversationProjector(
            sources, renderer, cache,
            scanner: null!, // ProjectAndBroadcastAsync isn't called in this test
            hub: null,
            logger: NullLogger<ConversationProjector>.Instance);

        var info = new TaskInfo { Id = "job-merge", FolderPath = Path.GetTempPath() };
        var (events, _) = await InvokeProjectInternalAsync(projector, info);

        Assert.Equal(2, events.Count);
        Assert.Equal("b1", events[0].Id);
        Assert.Equal("a1", events[1].Id);
        Assert.Contains("first", events[0].BodyHtml);
        Assert.Contains("second", events[1].BodyHtml);
    }

    [Fact]
    public async Task Projector_SecondCall_ServesFromCache()
    {
        var renderer = new MarkdigRenderer();
        var cache = new ConversationCache();
        var stub = new StubSource("a", new[]
        {
            new RawSourceEvent
            {
                Id = "a1", Kind = "message.user", SourceKind = "a",
                TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                BodyMarkdown = "hi"
            }
        });
        var projector = new ConversationProjector(
            new IConversationEventSource[] { stub }, renderer, cache,
            scanner: null!, hub: null,
            logger: NullLogger<ConversationProjector>.Instance);

        var info = new TaskInfo { Id = "job-cache", FolderPath = Path.GetTempPath() };
        await InvokeProjectInternalAsync(projector, info);
        await InvokeProjectInternalAsync(projector, info);

        Assert.Equal(1, stub.ReadCount); // second call short-circuits on the cache
    }

    private static async Task<(IReadOnlyList<ProjectedEvent> Events, IReadOnlyDictionary<string, DateTime> MTimes)>
        InvokeProjectInternalAsync(ConversationProjector projector, TaskInfo info)
    {
        // ProjectInternalAsync is private; the projector exposes the same
        // pipeline via ProjectAsync, but that requires the scanner. Use
        // reflection so this test does not depend on a full scanner setup.
        var mi = typeof(ConversationProjector).GetMethod("ProjectInternalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("ProjectInternalAsync not found");
        var task = (Task)mi.Invoke(projector, new object?[] { info, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        var events = (IReadOnlyList<ProjectedEvent>)result!.GetType().GetField("Item1")!.GetValue(result)!;
        var mtimes = (IReadOnlyDictionary<string, DateTime>)result.GetType().GetField("Item2")!.GetValue(result)!;
        return (events, mtimes);
    }

    private sealed class StubSource : IConversationEventSource
    {
        private readonly IReadOnlyList<RawSourceEvent> _events;
        public int ReadCount { get; private set; }
        public string SourceKind { get; }
        public StubSource(string kind, IReadOnlyList<RawSourceEvent> events)
        {
            SourceKind = kind;
            _events = events;
        }
        public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(TaskInfo jobInfo, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(_events);
        }
        public DateTime GetSourceMTimeUtc(TaskInfo jobInfo) => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
