using System.Collections.Concurrent;
using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// Owns every write to a task's durable <c>logs/cli-output.log</c> file.
/// The active log and one ignored rotation file are each capped at 10 MiB so
/// runaway CLI output cannot create a Git-hosting-blocking workspace blob.
/// </summary>
internal static class CliOutputLogFile
{
    internal const int MaxBytes = 10 * 1024 * 1024;
    internal const string RotationSuffix = ".1";
    internal const string RotationIgnorePattern = "**/logs/cli-output.log.1";

    private const int ReceiptTailBytes = 256 * 1024;
    private static readonly ConcurrentDictionary<string, object> WriteLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Appends line-oriented text and rotates before the active file can exceed
    /// the cap. Returns false only when <paramref name="duplicateMarker"/> is
    /// already present in the active log or its rotation file.
    /// </summary>
    internal static bool Append(
        string logPath,
        string content,
        DateTime? markerTimestamp = null,
        string? duplicateMarker = null) =>
        Append(logPath, content, MaxBytes, markerTimestamp ?? DateTime.UtcNow, duplicateMarker);

    internal static bool Append(
        string logPath,
        string content,
        int maxBytes,
        DateTime markerTimestamp,
        string? duplicateMarker = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (string.IsNullOrEmpty(content)) return true;

        var fullPath = Path.GetFullPath(logPath);
        var gate = WriteLocks.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var rotationPath = fullPath + RotationSuffix;
            NormalizeExisting(fullPath, rotationPath, maxBytes, markerTimestamp);

            if (!string.IsNullOrWhiteSpace(duplicateMarker)
                && (ContainsTailMarker(fullPath, duplicateMarker)
                    || ContainsTailMarker(rotationPath, duplicateMarker)))
            {
                return false;
            }

            var payload = PreparePayload(fullPath, content);
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var existingLength = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            if (existingLength + payloadBytes.Length <= maxBytes)
            {
                Write(fullPath, FileMode.Append, payloadBytes);
                return true;
            }

            if (existingLength > 0)
            {
                var existing = ReadTailUtf8(fullPath, maxBytes);
                Write(rotationPath, FileMode.Create, Encoding.UTF8.GetBytes(FitUtf8Tail(existing, maxBytes)));
            }

            var marker = RotationMarker(markerTimestamp, maxBytes);
            var markerBytes = Encoding.UTF8.GetBytes(marker);
            var retained = FitUtf8Tail(payload, Math.Max(0, maxBytes - markerBytes.Length));
            var retainedBytes = Encoding.UTF8.GetBytes(retained);
            var active = new byte[markerBytes.Length + retainedBytes.Length];
            Buffer.BlockCopy(markerBytes, 0, active, 0, markerBytes.Length);
            Buffer.BlockCopy(retainedBytes, 0, active, markerBytes.Length, retainedBytes.Length);
            Write(fullPath, FileMode.Create, active);
            return true;
        }
    }

    /// <summary>
    /// Migrates a legacy oversized active log in place. The newest tail remains
    /// active and the immediately preceding tail is retained as the sole
    /// rotation file. Calling this again after migration is a no-op.
    /// </summary>
    internal static bool MigrateExisting(string logPath) =>
        MigrateExisting(logPath, MaxBytes, DateTime.UtcNow);

    internal static bool MigrateExisting(string logPath, int maxBytes, DateTime markerTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var fullPath = Path.GetFullPath(logPath);
        var gate = WriteLocks.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            return NormalizeExisting(fullPath, fullPath + RotationSuffix, maxBytes, markerTimestamp);
        }
    }

    private static bool NormalizeExisting(
        string logPath,
        string rotationPath,
        int maxBytes,
        DateTime markerTimestamp)
    {
        var changed = false;
        if (File.Exists(rotationPath) && new FileInfo(rotationPath).Length > maxBytes)
        {
            var rotationTail = FitUtf8Tail(ReadTailUtf8(rotationPath, maxBytes), maxBytes);
            Write(rotationPath, FileMode.Create, Encoding.UTF8.GetBytes(rotationTail));
            changed = true;
        }

        if (!File.Exists(logPath) || new FileInfo(logPath).Length <= maxBytes)
            return changed;

        var retainedWindow = ReadTailUtf8(logPath, checked(maxBytes * 2));
        var marker = RotationMarker(markerTimestamp, maxBytes);
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        var activeTail = FitUtf8Tail(retainedWindow, Math.Max(0, maxBytes - markerBytes.Length));
        var olderLength = Math.Max(0, retainedWindow.Length - activeTail.Length);
        var olderTail = olderLength == 0
            ? string.Empty
            : FitUtf8Tail(retainedWindow[..olderLength], maxBytes);

        if (olderTail.Length > 0)
            Write(rotationPath, FileMode.Create, Encoding.UTF8.GetBytes(olderTail));
        else if (File.Exists(rotationPath))
            File.Delete(rotationPath);

        var activeTailBytes = Encoding.UTF8.GetBytes(activeTail);
        var active = new byte[markerBytes.Length + activeTailBytes.Length];
        Buffer.BlockCopy(markerBytes, 0, active, 0, markerBytes.Length);
        Buffer.BlockCopy(activeTailBytes, 0, active, markerBytes.Length, activeTailBytes.Length);
        Write(logPath, FileMode.Create, active);
        return true;
    }

    private static string PreparePayload(string logPath, string content)
    {
        var payload = content.TrimEnd('\r', '\n') + Environment.NewLine;
        if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0 || EndsWithNewline(logPath))
            return payload;
        return Environment.NewLine + payload;
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private static bool ContainsTailMarker(string path, string marker)
    {
        if (!File.Exists(path)) return false;
        return ReadTailUtf8(path, ReceiptTailBytes).Contains(marker, StringComparison.Ordinal);
    }

    private static string RotationMarker(DateTime timestamp, int maxBytes) =>
        $"[{timestamp:HH:mm:ss.fff}] [system] [cli-output-rotated] "
        + $"Continued after rotating at {maxBytes / (1024 * 1024)} MiB; the preceding tail is in cli-output.log.1."
        + Environment.NewLine;

    private static string ReadTailUtf8(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0 || maxBytes <= 0) return string.Empty;
        var length = (int)Math.Min(stream.Length, maxBytes);
        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        var read = stream.Read(bytes, 0, length);
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static string FitUtf8Tail(string content, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(content)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= maxBytes) return content;

        var start = bytes.Length - maxBytes;
        var decoded = Encoding.UTF8.GetString(bytes, start, maxBytes);
        var newline = decoded.IndexOf('\n');
        if (newline >= 0) decoded = decoded[(newline + 1)..];
        while (decoded.Length > 0 && Encoding.UTF8.GetByteCount(decoded) > maxBytes)
            decoded = decoded[1..];
        return decoded;
    }

    private static void Write(string path, FileMode mode, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            mode,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
