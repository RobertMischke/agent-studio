using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public class ClaudeOneShotInlineImageTests
{
    [Fact]
    public void BuildStreamJsonUserMessage_EmitsAnthropicEnvelope()
    {
        var images = new[]
        {
            new CliOneShotImage("AAAA", "image/png")
        };

        var line = ClaudeOneShot.BuildStreamJsonUserMessage("Describe the image", images);

        Assert.EndsWith("\n", line);
        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        var root = doc.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        var content = root.GetProperty("message").GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Describe the image", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("AAAA", source.GetProperty("data").GetString());
    }

    [Fact]
    public void BuildStreamJsonUserMessage_DefaultsMissingMediaTypeToPng()
    {
        var images = new[] { new CliOneShotImage("AAAA", "") };

        var line = ClaudeOneShot.BuildStreamJsonUserMessage("hi", images);

        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        var source = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")[1]
            .GetProperty("source");
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
    }
}
