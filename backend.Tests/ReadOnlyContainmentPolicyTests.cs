using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ADR-0052 "containment over trust": the read-only task-mode pipeline omits the
/// git pre/post steps, so a non-empty working-tree diff when a planning / research
/// run finishes is a hard containment violation - reported on the timeline, never
/// auto-reverted. These cover the pure decision behind
/// <c>ProjectRunner.ReportReadOnlyContainmentIfDirty</c>: only read-only modes with
/// a dirty repo are flagged, the file list is capped while the count stays exact,
/// and coding mode / clean trees are silent.
/// </summary>
public sealed class ReadOnlyContainmentPolicyTests
{
    [Theory]
    [InlineData(TaskModes.Planning)]
    [InlineData(TaskModes.Research)]
    public void ReadOnlyMode_WithDirtyTree_IsViolation(string mode)
    {
        var result = ReadOnlyContainmentPolicy.Evaluate(
            mode, isRepo: true, changedFiles: new[] { "backend/Services/X.cs", "docs/y.md" });

        Assert.True(result.IsViolation);
        Assert.Equal(2, result.ChangedFiles);
        Assert.Equal("backend/Services/X.cs, docs/y.md", result.FileList);
        Assert.Contains($"Read-only {mode} run left 2 changed file(s)", result.Summary);
        Assert.Contains("not auto-reverted", result.Summary);
    }

    [Fact]
    public void CodingMode_WithDirtyTree_IsNotViolation()
    {
        // Coding runs own the git steps - a dirty tree is normal, not a breach.
        var result = ReadOnlyContainmentPolicy.Evaluate(
            TaskModes.Coding, isRepo: true, changedFiles: new[] { "backend/Services/X.cs" });

        Assert.False(result.IsViolation);
        Assert.Same(ReadOnlyContainment.None, result);
    }

    [Fact]
    public void ConceptMode_WithOneDossier_IsContained()
    {
        var result = ReadOnlyContainmentPolicy.Evaluate(
            TaskModes.Concept,
            isRepo: true,
            changedFiles:
            [
                "docs/concept-pipeline/index.html",
                "docs/concept-pipeline/workbench.json",
            ]);

        Assert.False(result.IsViolation);
    }

    [Theory]
    [InlineData("backend/Features/X.cs")]
    [InlineData("docs/one/index.html", "docs/two/index.html")]
    public void ConceptMode_WithOutsideOrMultipleWorkbenchDiff_IsViolation(params string[] changedFiles)
    {
        var result = ReadOnlyContainmentPolicy.Evaluate(
            TaskModes.Concept, isRepo: true, changedFiles);

        Assert.True(result.IsViolation);
        Assert.Contains("containment violation", result.Summary);
    }

    [Fact]
    public void ReadOnlyMode_WithCleanTree_IsNotViolation()
    {
        var result = ReadOnlyContainmentPolicy.Evaluate(
            TaskModes.Planning, isRepo: true, changedFiles: Array.Empty<string>());

        Assert.False(result.IsViolation);
        Assert.Same(ReadOnlyContainment.None, result);
    }

    [Fact]
    public void ReadOnlyMode_OutsideRepo_IsNotViolation()
    {
        // A non-repo working dir cannot have a meaningful diff; stay silent.
        var result = ReadOnlyContainmentPolicy.Evaluate(
            TaskModes.Research, isRepo: false, changedFiles: new[] { "stray.txt" });

        Assert.False(result.IsViolation);
    }

    [Fact]
    public void ManyChangedFiles_AreCappedWithMoreSuffix_CountStaysExact()
    {
        var files = Enumerable.Range(0, ReadOnlyContainmentPolicy.MaxInlinedFiles + 5)
            .Select(i => $"f{i}.cs")
            .ToList();

        var result = ReadOnlyContainmentPolicy.Evaluate(TaskModes.Planning, isRepo: true, files);

        Assert.True(result.IsViolation);
        Assert.Equal(files.Count, result.ChangedFiles);
        Assert.Contains("+5 more", result.FileList);
        // The inlined list holds exactly the cap of paths (the rest is summarised).
        Assert.Equal(
            ReadOnlyContainmentPolicy.MaxInlinedFiles,
            result.FileList.Split(", ").Count(p => !p.EndsWith("more")));
    }

    [Fact]
    public void NullMode_IsTreatedAsCoding_NoViolation()
    {
        // TaskModes.Normalize coerces null/unknown to coding, so a missing mode
        // is never flagged as a read-only breach.
        var result = ReadOnlyContainmentPolicy.Evaluate(
            mode: null, isRepo: true, changedFiles: new[] { "x.cs" });

        Assert.False(result.IsViolation);
    }
}
