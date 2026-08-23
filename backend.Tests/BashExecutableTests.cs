using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Order of preference for the local <c>bash -lc</c> executable: Git Bash
/// next to the git.exe on PATH, then the standard Git for Windows install
/// locations, then plain <c>bash</c>. Off Windows it is always plain bash.
/// </summary>
public sealed class BashExecutableTests
{
    [Fact]
    public void Resolve_OffWindows_IsPlainBash()
    {
        var resolved = BashExecutable.Resolve(
            isWindows: false,
            pathVariable: "/usr/local/bin:/usr/bin",
            programFiles: null,
            programFilesX86: null,
            localAppData: null,
            fileExists: _ => true);

        Assert.Equal("bash", resolved);
    }

    [Fact]
    public void Resolve_Windows_FindsGitBashNextToTheGitOnPath()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        };

        var resolved = BashExecutable.Resolve(
            isWindows: true,
            pathVariable: @"C:\Windows\System32;C:\Users\dev\.cargo\bin;C:\Program Files\Git\cmd;C:\Program Files\dotnet",
            programFiles: @"C:\Program Files",
            programFilesX86: @"C:\Program Files (x86)",
            localAppData: @"C:\Users\dev\AppData\Local",
            fileExists: existing.Contains);

        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", resolved);
    }

    [Theory]
    [InlineData(@"D:\tools\PortableGit\mingw64\bin")]
    [InlineData(@"D:\tools\PortableGit\usr\bin")]
    [InlineData(@"D:\tools\PortableGit\bin\")]
    public void Resolve_Windows_DerivesTheInstallRootFromEveryGitPathLayout(string pathEntry)
    {
        var resolved = BashExecutable.Resolve(
            isWindows: true,
            pathVariable: pathEntry,
            programFiles: null,
            programFilesX86: null,
            localAppData: null,
            fileExists: path => path.Equals(@"D:\tools\PortableGit\usr\bin\bash.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"D:\tools\PortableGit\usr\bin\bash.exe", resolved);
    }

    [Fact]
    public void Resolve_Windows_FallsBackToTheStandardInstallLocations()
    {
        var resolved = BashExecutable.Resolve(
            isWindows: true,
            pathVariable: @"C:\Windows\System32;C:\Program Files\dotnet",
            programFiles: @"C:\Program Files",
            programFilesX86: @"C:\Program Files (x86)",
            localAppData: @"C:\Users\dev\AppData\Local",
            fileExists: path => path.Equals(@"C:\Users\dev\AppData\Local\Programs\Git\bin\bash.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Users\dev\AppData\Local\Programs\Git\bin\bash.exe", resolved);
    }

    [Fact]
    public void Resolve_Windows_WithoutGitBash_LeavesResolutionToPath()
    {
        var resolved = BashExecutable.Resolve(
            isWindows: true,
            pathVariable: @"C:\Windows\System32;C:\Program Files\Git\cmd",
            programFiles: @"C:\Program Files",
            programFilesX86: null,
            localAppData: null,
            fileExists: _ => false);

        Assert.Equal("bash", resolved);
    }
}
