
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Ticket test-path unit: the pure (cliType, mode) → flags mapper renders the
/// correct CLI arguments for every permission mode. This is the seam every
/// driver uses on spawn, so asserting it here covers the "right flags per mode"
/// acceptance criterion without driving a real CLI process.
/// </summary>
public sealed class CliPermissionFlagsTests
{
    [Fact]
    public void Claude_Yolo_SkipsPermissions()
        => Assert.Equal(
            ["--dangerously-skip-permissions"],
            CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Yolo));

    [Fact]
    public void Claude_WorkspaceWrite_AcceptEdits()
        => Assert.Equal(
            ["--permission-mode", "acceptEdits"],
            CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Claude_ReadOnly_PlanMode()
        => Assert.Equal(
            ["--permission-mode", "plan"],
            CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.ReadOnly));

    [Fact]
    public void Claude_Custom_InjectsNothing()
        => Assert.Empty(CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Custom));

    [Fact]
    public void Codex_Yolo_DangerFullAccess()
        => Assert.Equal(
            ["--sandbox", "danger-full-access"],
            CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Yolo));

    [Fact]
    public void Codex_WorkspaceWrite_SandboxWorkspaceWrite()
        => Assert.Equal(
            ["--sandbox", "workspace-write"],
            CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Codex_ReadOnly_SandboxReadOnly()
        => Assert.Equal(
            ["--sandbox", "read-only"],
            CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.ReadOnly));

    [Fact]
    public void Codex_Custom_InjectsNothing()
        => Assert.Empty(CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Custom));

    [Fact]
    public void Gemini_Yolo_MatchesHistoricSkipTrustY()
        => Assert.Equal(
            ["--skip-trust", "-y"],
            CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.Yolo));

    [Fact]
    public void Gemini_WorkspaceWrite_AutoEditAndKeepsSkipTrust()
        => Assert.Equal(
            ["--skip-trust", "--approval-mode", "auto_edit"],
            CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.WorkspaceWrite));

    [Fact]
    public void Gemini_Custom_StillSkipsTrustToAvoidHang()
        => Assert.Equal(
            ["--skip-trust"],
            CliPermissionFlags.For(CliTypes.Gemini, CliPermissionModes.Custom));

    [Fact]
    public void NullMode_NormalizesToYolo_PreservingHistoricBehaviour()
    {
        // A caller that never threads a mode keeps the maximum-autonomy default
        // every driver shipped with before this feature.
        Assert.Equal(
            CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Yolo),
            CliPermissionFlags.For(CliTypes.Claude, null));
        Assert.Equal(
            CliPermissionFlags.For(CliTypes.Codex, CliPermissionModes.Yolo),
            CliPermissionFlags.For(CliTypes.Codex, null));
    }

    [Fact]
    public void UnknownCliType_NormalizesViaCliTypesContract()
        // CliTypes.Normalize maps an unknown id to Claude, so the mapper does
        // too — never throwing on an unexpected cliType.
        => Assert.Equal(
            CliPermissionFlags.For(CliTypes.Claude, CliPermissionModes.Yolo),
            CliPermissionFlags.For("totally-made-up", CliPermissionModes.Yolo));

    [Fact]
    public void Normalize_UnknownMode_IsYolo()
        => Assert.Equal(CliPermissionModes.Yolo, CliPermissionModes.Normalize("nonsense"));
}
