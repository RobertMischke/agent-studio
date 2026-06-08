using System.Globalization;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Pure path + bucket helpers for the flat, ID-named task storage layout
/// (F45 restscope). Tasks live under
/// <c>&lt;projectRoot&gt;/tasks/&lt;bucket&gt;/&lt;taskId&gt;/</c> where
/// <c>bucket = floor(keyNumber / 1000)</c> zero-padded to three digits and
/// <c>taskId</c> is the stable task key (e.g. <c>ASS-617</c>). The derived
/// index lives under <c>&lt;projectRoot&gt;/id/</c>. Lane is metadata
/// (<c>task.json.state</c>), no longer folder position.
///
/// <para>
/// This type is side-effect free except for read-only directory
/// enumeration, so it is unit-testable against a temp directory with no
/// running host. It does not construct lane-folder paths and performs no
/// structural directory mutation, so it stays outside the
/// TaskFolderAccessIsolation whitelist.
/// </para>
/// </summary>
internal static class TaskStorageLayout
{
    // Folder names for the flat task storage layout: tasks live under
    // <projectRoot>/tasks/<bucket>/<key>/ and the derived index under
    // <projectRoot>/id/. Lane is metadata (task.json.state), not folder
    // position. (Terminology: everything is a "task"; the index is the
    // project's "id" layer.)
    public const string JobsDirName = "tasks";
    public const string IndexDirName = "id";

    public const int BucketSize = 1000;

    /// <summary>Three-digit zero-padded shard for a key number (floor / 1000).</summary>
    public static string Bucket(int keyNumber)
    {
        if (keyNumber < 0) keyNumber = 0;
        return (keyNumber / BucketSize).ToString("D3", CultureInfo.InvariantCulture);
    }

    public static string JobsRoot(string projectRoot) =>
        Path.Combine(projectRoot, JobsDirName);

    public static string IndexRoot(string projectRoot) =>
        Path.Combine(projectRoot, IndexDirName);

    public static string BucketDir(string projectRoot, int keyNumber) =>
        Path.Combine(JobsRoot(projectRoot), Bucket(keyNumber));

    public static string JobDir(string projectRoot, int keyNumber, string taskId) =>
        Path.Combine(BucketDir(projectRoot, keyNumber), taskId);

    /// <summary>
    /// The index-relative location string for a task,
    /// <c>"&lt;bucket&gt;/&lt;taskId&gt;"</c> with a forward slash so the
    /// value is stable across OSes (matches the F45 by-key.json shape).
    /// </summary>
    public static string Location(int keyNumber, string taskId) =>
        $"{Bucket(keyNumber)}/{taskId}";

    /// <summary>
    /// Parses the trailing integer of a task key (<c>ASS-617</c> -&gt; 617).
    /// Prefix-agnostic so the migrator does not need the project shortCode
    /// just to compute a bucket. Returns false for a blank key or a
    /// non-numeric / non-positive tail.
    /// </summary>
    public static bool TryParseKeyNumber(string? key, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var dash = key.LastIndexOf('-');
        var tail = dash >= 0 ? key[(dash + 1)..] : key;
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    /// <summary>
    /// Absolute paths of every task folder under <c>tasks/</c>, skipping any
    /// dot-prefixed bucket or entry (reserved for staging / hidden state). A
    /// missing <c>tasks/</c> yields an empty sequence.
    /// </summary>
    public static IEnumerable<string> EnumerateJobDirs(string projectRoot)
    {
        var jobsRoot = JobsRoot(projectRoot);
        if (!Directory.Exists(jobsRoot)) yield break;
        foreach (var bucketDir in Directory.EnumerateDirectories(jobsRoot))
        {
            if (Path.GetFileName(bucketDir).StartsWith('.')) continue;
            foreach (var jobDir in Directory.EnumerateDirectories(bucketDir))
            {
                if (Path.GetFileName(jobDir).StartsWith('.')) continue;
                yield return jobDir;
            }
        }
    }
}
