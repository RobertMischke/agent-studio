using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace AgentStudio.Tasks;

public sealed record ArtifactThumbnail(byte[] Bytes, string ContentType, DateTimeOffset LastModified);

/// <summary>
/// Produces bounded WebP thumbnails for task result images. The source path is
/// already resolved and traversal-checked by <see cref="ScreenshotIndexService"/>;
/// this service owns only decode, resize, and a short-lived in-memory cache.
/// Full-size files remain available to the lightbox through the existing
/// artifact route and are never used by the gallery grid.
/// </summary>
public sealed class ArtifactThumbnailService
{
    private const long MaximumSourceBytes = 64L * 1024 * 1024;
    private const long MaximumSourcePixels = 80_000_000;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ArtifactThumbnailService> _logger;

    public ArtifactThumbnailService(IMemoryCache cache, ILogger<ArtifactThumbnailService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public ArtifactThumbnail? Create(string resolvedPath, int? requestedWidth)
    {
        var info = new FileInfo(resolvedPath);
        if (!info.Exists) return null;
        if (info.Length > MaximumSourceBytes)
        {
            _logger.LogWarning(
                "task-thumbnail-source-too-large path={Path} bytes={Bytes}",
                resolvedPath,
                info.Length);
            return null;
        }

        var width = ArtifactThumbnailPolicy.NormalizeWidth(requestedWidth);
        var cacheKey = $"task-thumbnail:{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{width}";
        if (_cache.TryGetValue<ArtifactThumbnail>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var identified = Image.Identify(info.FullName);
            if (identified is null
                || (long)identified.Width * identified.Height > MaximumSourcePixels)
            {
                _logger.LogWarning(
                    "task-thumbnail-dimensions-too-large path={Path} width={Width} height={Height}",
                    resolvedPath,
                    identified?.Width,
                    identified?.Height);
                return null;
            }
            using var image = Image.Load(info.FullName);
            while (image.Frames.Count > 1) image.Frames.RemoveFrame(1);
            image.Mutate(operation =>
            {
                operation.AutoOrient();
                if (image.Width > width || image.Height > width)
                {
                    operation.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(width, width),
                        Sampler = KnownResamplers.Bicubic,
                        Compand = true,
                    });
                }
            });

            using var output = new MemoryStream();
            image.Save(output, new WebpEncoder { Quality = 72 });
            var thumbnail = new ArtifactThumbnail(
                output.ToArray(),
                "image/webp",
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            _cache.Set(cacheKey, thumbnail, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(20),
                Size = thumbnail.Bytes.Length,
            });
            return thumbnail;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException
                                   or InvalidImageContentException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "task-thumbnail-generation-failed path={Path}", resolvedPath);
            return null;
        }
    }
}

internal static class ArtifactThumbnailPolicy
{
    internal const int DefaultWidth = 360;
    internal const int MinimumWidth = 96;
    internal const int MaximumWidth = 720;

    internal static int NormalizeWidth(int? requestedWidth) =>
        Math.Clamp(requestedWidth ?? DefaultWidth, MinimumWidth, MaximumWidth);
}
