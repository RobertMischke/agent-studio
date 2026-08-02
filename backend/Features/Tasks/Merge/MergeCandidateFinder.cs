
namespace AgentStudio.Tasks;

/// <summary>
/// Heuristic detector that surfaces "wrapper-of-this" or "wrapped-by-this"
/// candidates for a given primary job. Used by
/// <c>GET /api/tasks/{id}/merge/candidates</c> and as the seed list for the
/// completed-lane audit.
///
/// <para>The detector is intentionally cheap: it walks the in-memory
/// snapshot from <see cref="TaskScannerService"/> and applies three signals
/// (slug prefix, prompt mention, prompt similarity) with weights that
/// agree with the ASS-30/ASS-182 textbook example.</para>
/// </summary>
public sealed class MergeCandidateFinder
{
    private readonly TaskScannerService _scanner;

    public MergeCandidateFinder(TaskScannerService scanner)
    {
        _scanner = scanner;
    }

    public List<MergeCandidate> Find(string primaryId, string? watchPath, int max = 10)
    {
        var primary = _scanner.FindJob(primaryId, watchPath);
        if (primary == null) return [];

        var primaryPrompt = SafeReadPrompt(primary.FolderPath);
        var primarySlug = primary.Id;
        var primaryKey = primary.Key;

        var candidates = new List<MergeCandidate>();
        foreach (var job in _scanner.ScanAllAutomationJobs())
        {
            if (job.Id == primary.Id) continue;
            if (!string.Equals(job.WatchPath, primary.WatchPath, StringComparison.OrdinalIgnoreCase)) continue;

            double score = 0;
            var reasons = new List<string>();

            // Wrapper slug shape (e.g. "human-decision-needed-<primary-slug>")
            if (job.Id.Contains(primarySlug, StringComparison.OrdinalIgnoreCase)
                && job.Id.Length > primarySlug.Length)
            {
                score += 0.5;
                reasons.Add($"slug contains '{primarySlug}'");
            }
            // Followup slug shape
            if (job.Id.EndsWith("-followup", StringComparison.OrdinalIgnoreCase)
                && job.Id.Contains(primarySlug, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.2;
                reasons.Add("followup naming");
            }

            // Prompt literally references the primary
            var candidatePrompt = SafeReadPrompt(job.FolderPath);
            if (!string.IsNullOrEmpty(candidatePrompt))
            {
                if (candidatePrompt.Contains(primarySlug, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.3;
                    reasons.Add("prompt mentions primary slug");
                }
                if (!string.IsNullOrEmpty(primaryKey) &&
                    candidatePrompt.Contains(primaryKey, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.3;
                    reasons.Add($"prompt mentions key {primaryKey}");
                }

                // Cheap prompt similarity. Tokenize both prompts to lowercase
                // word sets, compute Jaccard; anything above 0.5 is a strong
                // signal that one card is a re-scoping of the other.
                if (!string.IsNullOrEmpty(primaryPrompt) && primaryPrompt.Length > 50)
                {
                    var sim = JaccardSimilarity(primaryPrompt, candidatePrompt);
                    if (sim >= 0.8)
                    {
                        score += 0.6;
                        reasons.Add($"prompt similarity {sim:0.00}");
                    }
                    else if (sim >= 0.5)
                    {
                        score += 0.3;
                        reasons.Add($"prompt similarity {sim:0.00}");
                    }
                }
            }

            if (score <= 0) continue;

            candidates.Add(new MergeCandidate
            {
                Id = job.Id,
                Key = job.Key,
                Title = job.Title,
                State = job.State,
                WatchPath = job.WatchPath,
                ProjectName = job.ProjectName,
                Reason = string.Join("; ", reasons),
                Score = Math.Round(Math.Min(score, 1.0), 3),
            });
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Take(max)
            .ToList();
    }

    private static string SafeReadPrompt(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, "prompt.md");
            if (!File.Exists(path)) return string.Empty;
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Jaccard similarity over lowercase word tokens. Cheap, deterministic,
    /// good enough for "are these two prompts substantially the same?".
    /// Returns 0 when either input has no usable tokens.
    /// </summary>
    private static double JaccardSimilarity(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                if (current.Length >= 3) result.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= 3) result.Add(current.ToString());
        return result;
    }
}
