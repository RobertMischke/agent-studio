namespace AgentStudio.Pipeline;

/// <summary>
/// Classifies an accepted delivery as documentation-only (the AGT-2417
/// research/docs rule): every changed path is documentation or task evidence
/// and none is product code. A docs-only delivery cannot change a build or
/// test signal, so it integrates through a light gate - a conflict-checked
/// merge probe - instead of the full-suite release gate and its
/// rebased-onto-main requirement.
///
/// <para>
/// Fail closed: an unknown diff (null) or an empty change set is NOT
/// docs-only; those deliveries stay on the strict release path. The path
/// classes are deliberately conservative - <c>prompts/</c>, scripts, and
/// config files count as product behaviour, not documentation.
/// </para>
/// </summary>
public static class DocsOnlyDeliveryPolicy
{
    public static bool IsDocsOnly(IReadOnlyList<string>? changedPaths)
    {
        if (changedPaths is null || changedPaths.Count == 0) return false;
        foreach (var path in changedPaths)
            if (!IsDocsPath(path)) return false;
        return true;
    }

    internal static bool IsDocsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("results/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase)) return true;
        return p.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }
}
