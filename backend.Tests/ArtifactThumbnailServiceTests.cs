using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ArtifactThumbnailServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"agt-thumbnail-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, 360)]
    [InlineData(12, 96)]
    [InlineData(480, 480)]
    [InlineData(4000, 720)]
    public void NormalizeWidth_ClampsToBoundedGalleryRange(int? requested, int expected)
    {
        Assert.Equal(expected, ArtifactThumbnailPolicy.NormalizeWidth(requested));
    }

    [Fact]
    public void Create_ResizesLargeSourceAndReturnsWebp()
    {
        Directory.CreateDirectory(_tempDir);
        var source = Path.Combine(_tempDir, "evidence.png");
        using (var image = new Image<Rgba32>(1200, 800, new Rgba32(32, 80, 140)))
            image.SaveAsPng(source);

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 4 * 1024 * 1024 });
        var service = new ArtifactThumbnailService(cache, NullLogger<ArtifactThumbnailService>.Instance);

        var result = service.Create(source, 360);

        Assert.NotNull(result);
        Assert.Equal("image/webp", result.ContentType);
        var size = Image.Identify(result.Bytes);
        Assert.NotNull(size);
        Assert.True(size.Width <= 360);
        Assert.True(size.Height <= 360);
        Assert.True(result.Bytes.Length < new FileInfo(source).Length);
    }

    [Fact]
    public void Create_ReturnsNullForInvalidImageContent()
    {
        Directory.CreateDirectory(_tempDir);
        var source = Path.Combine(_tempDir, "broken.png");
        File.WriteAllText(source, "not an image");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        var service = new ArtifactThumbnailService(cache, NullLogger<ArtifactThumbnailService>.Instance);

        Assert.Null(service.Create(source, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
