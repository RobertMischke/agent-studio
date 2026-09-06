using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720: the shared gate dependency cache must never hand a gate a tree that
/// npm did not finish writing.
/// <para>
/// CAC-18 passed remote review 412 times while every pre-main full suite on the
/// studio died in vite before vitest listed a file. The cause was one cache entry
/// holding 2,580 of 25,748 files with an empty <c>vite/dist/client/</c> and no
/// <c>node_modules/.package-lock.json</c>, saved on 10 August 2026. Its
/// <c>.nm-state</c> marker still matched the lockfile hash, so every later run
/// read <c>hit reason=lock-unchanged</c>, skipped <c>npm ci</c>, and reproduced
/// the same failure. Nothing in the protocol could heal it.
/// </para>
/// <para>
/// The invariants pinned here are the three that close that loop: a marker only
/// certifies a tree that carries npm's install ledger, a save publishes all or
/// nothing, and an entry can be evicted by name.
/// </para>
/// </summary>
public sealed class GateDependencyCacheIntegrityTests : IDisposable
{
    private const string RepositoryIdentity = "agt-2720-fixture-repository";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gate-dependency-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Evaluate_TreeThatLostItsInstallLedger_IsMissNotHit()
    {
        // The CAC-18 sequence verbatim: a complete install was stamped, then a
        // transfer truncated the tree. The marker still matches the lockfile
        // hash, which is exactly why this read "hit" for four weeks.
        var workspace = NewNodeWorkspace("cac-18");
        WriteCompleteInstall(workspace);
        StampCurrentLockHash(workspace);
        File.Delete(LedgerPath(workspace));

        var decision = DependencyPreparationState.Evaluate(workspace, NpmScope());

        Assert.Equal("miss", decision.State);
        Assert.Equal("install-incomplete", decision.Reason);
    }

    [Fact]
    public void Evaluate_CompleteTreeWithMatchingMarker_IsHit()
    {
        var workspace = NewNodeWorkspace("complete");
        WriteCompleteInstall(workspace);
        StampCurrentLockHash(workspace);

        var decision = DependencyPreparationState.Evaluate(workspace, NpmScope());

        Assert.Equal("hit", decision.State);
        Assert.Equal("lock-unchanged", decision.Reason);
    }

    [Fact]
    public void Evaluate_InstallerThatWritesNoLedger_StillCaches()
    {
        // A yarn or pnpm install leaves no node_modules/.package-lock.json even
        // when a package-lock.json is still checked in. Demanding the ledger from
        // the lockfile NAME would turn every such gate into a permanent cold
        // install, silently and forever. The marker records what the install
        // actually left behind, so this scope keeps its cache.
        var workspace = NewNodeWorkspace("no-ledger-installer");
        WriteTruncatedViteTree(workspace);
        StampCurrentLockHash(workspace);

        var decision = DependencyPreparationState.Evaluate(workspace, NpmScope());

        Assert.Equal("hit", decision.State);
        Assert.Equal("lock-unchanged", decision.Reason);
    }

    [Fact]
    public void Evaluate_LegacyMarkerWithoutLedgerRecord_StillProtectsAnNpmScope()
    {
        // Entries stamped before this change carry only the hash. An npm scope
        // must keep the CAC-18 protection; the cost is one cold install, after
        // which the marker states the fact.
        var workspace = NewNodeWorkspace("legacy-marker");
        WriteTruncatedViteTree(workspace);
        File.WriteAllText(
            Path.Combine(workspace, DependencyPreparationState.MarkerFileName),
            DependencyPreparationState.ComputeLockHash(workspace, ["package-lock.json"]));

        var decision = DependencyPreparationState.Evaluate(workspace, NpmScope());

        Assert.Equal("miss", decision.State);
        Assert.Equal("install-incomplete", decision.Reason);
    }

    [Fact]
    public void Save_PublishesTheEntryAndLeavesNoStagingDebris()
    {
        var workspace = NewNodeWorkspace("publish");
        WriteCompleteInstall(workspace);
        StampCurrentLockHash(workspace);

        var messages = NewSession(workspace).Save();

        Assert.True(File.Exists(Path.Combine(
            EntryContentRoot(),
            DependencyPreparationState.DependencyDirectoryName,
            DependencyPreparationState.InstallLedgerFileName)));
        Assert.Contains(messages, message => message.Contains("published=1 failed=0", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(EntryRoot())
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith('.')));
    }

    [Fact]
    public void Save_TruncatedWorkspaceTree_LeavesThePreviousEntryIntact()
    {
        // A transfer that lost content must never overwrite a healthy entry:
        // that is how a single bad run poisons every later gate.
        var healthy = NewNodeWorkspace("healthy");
        WriteCompleteInstall(healthy);
        StampCurrentLockHash(healthy);
        NewSession(healthy).Save();
        var publishedLedger = Path.Combine(
            EntryContentRoot(),
            DependencyPreparationState.DependencyDirectoryName,
            DependencyPreparationState.InstallLedgerFileName);
        Assert.True(File.Exists(publishedLedger));

        var truncated = NewNodeWorkspace("truncated");
        WriteCompleteInstall(truncated);
        StampCurrentLockHash(truncated);
        File.Delete(LedgerPath(truncated));

        var messages = NewSession(truncated).Save();

        Assert.Contains(
            messages,
            message => message.Contains(
                "dependency-cache save skipped scope=. reason=install-incomplete",
                StringComparison.Ordinal));
        Assert.True(File.Exists(publishedLedger));
        // The truncated workspace keeps its own debris; the shared entry is untouched.
        Assert.True(Directory.Exists(Path.Combine(truncated, "node_modules")));
    }

    [Fact]
    public void Save_OneTruncatedScope_StillPublishesItsHealthySibling()
    {
        // All-or-nothing publication would let one bad scope strip the cache for
        // every other scope in the repository, turning a bounded fault into a
        // permanently cold gate host.
        var workspace = NewNodeWorkspace("mixed");
        var sibling = Path.Combine(workspace, "tools");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(
            Path.Combine(sibling, "package-lock.json"),
            """{"name":"tools","lockfileVersion":3,"packages":{}}""");
        WriteCompleteInstall(workspace);
        WriteCompleteInstall(sibling);
        StampCurrentLockHash(workspace);
        DependencyPreparationState.Stamp(
            sibling, DependencyPreparationState.ComputeLockHash(sibling, ["package-lock.json"]));
        File.Delete(LedgerPath(workspace));

        var session = DependencyCacheSession.Create(
            Path.Combine(_root, "cache"),
            RepositoryIdentity,
            workspace,
            [NpmScope(), new ReviewDependencyScopeDto("tools", ["package-lock.json"])]);
        session.Save();

        Assert.False(Directory.Exists(Path.Combine(
            EntryContentRoot(), DependencyPreparationState.DependencyDirectoryName)));
        Assert.True(File.Exists(Path.Combine(
            EntryContentRoot(),
            "tools",
            DependencyPreparationState.DependencyDirectoryName,
            DependencyPreparationState.InstallLedgerFileName)));
    }

    [Fact]
    public void Save_DoesNotDiscardCachedScopesThisSessionNeverStaged()
    {
        // Scope sets vary between consecutive gates on the same repository. A
        // whole-entry swap would empty the cache whenever two runs touch
        // different subtrees.
        var frontend = NewNodeWorkspace("frontend-run");
        var nested = Path.Combine(frontend, "frontend");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(nested, "package-lock.json"),
            """{"name":"frontend","lockfileVersion":3,"packages":{}}""");
        WriteCompleteInstall(nested);
        DependencyPreparationState.Stamp(
            nested, DependencyPreparationState.ComputeLockHash(nested, ["package-lock.json"]));
        DependencyCacheSession.Create(
            Path.Combine(_root, "cache"),
            RepositoryIdentity,
            frontend,
            [new ReviewDependencyScopeDto("frontend", ["package-lock.json"])]).Save();

        var rootOnly = NewNodeWorkspace("root-run");
        WriteCompleteInstall(rootOnly);
        StampCurrentLockHash(rootOnly);
        NewSession(rootOnly).Save();

        Assert.True(Directory.Exists(Path.Combine(
            EntryContentRoot(), "frontend", DependencyPreparationState.DependencyDirectoryName)));
        Assert.True(Directory.Exists(Path.Combine(
            EntryContentRoot(), DependencyPreparationState.DependencyDirectoryName)));
    }

    [Fact]
    public void RestoredEntry_IsUsableAndStillCertifiesAsComplete()
    {
        var source = NewNodeWorkspace("source");
        WriteCompleteInstall(source);
        StampCurrentLockHash(source);
        NewSession(source).Save();

        var target = NewNodeWorkspace("target");
        NewSession(target).Restore();

        var decision = DependencyPreparationState.Evaluate(target, NpmScope());
        Assert.Equal("hit", decision.State);
        Assert.Equal("lock-unchanged", decision.Reason);
    }

    [Fact]
    public void Evict_DropsTheEntryAndNamesTheReason()
    {
        var workspace = NewNodeWorkspace("evict");
        WriteCompleteInstall(workspace);
        StampCurrentLockHash(workspace);
        NewSession(workspace).Save();
        Assert.True(Directory.Exists(EntryContentRoot()));

        var messages = NewSession(workspace).Evict("gate-environment-failure");

        Assert.False(Directory.Exists(EntryContentRoot()));
        Assert.Contains(
            messages,
            message => message.Contains(
                "dependency-cache evicted scope=. reason=gate-environment-failure",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EvictedEntry_ForcesTheNextGateToReinstall()
    {
        var workspace = NewNodeWorkspace("reinstall");
        WriteCompleteInstall(workspace);
        StampCurrentLockHash(workspace);
        NewSession(workspace).Save();
        NewSession(workspace).Evict("gate-environment-failure");

        var next = NewNodeWorkspace("next");
        NewSession(next).Restore();

        var decision = DependencyPreparationState.Evaluate(next, NpmScope());
        Assert.Equal("miss", decision.State);
        Assert.Equal("deps-dir-missing", decision.Reason);
    }

    private static ReviewDependencyScopeDto NpmScope()
        => new("", ["package-lock.json"]);

    private DependencyCacheSession NewSession(string workspace)
        => DependencyCacheSession.Create(
            Path.Combine(_root, "cache"),
            RepositoryIdentity,
            workspace,
            [NpmScope()]);

    private string EntryRoot()
        => DependencyCacheSession.CachePath(Path.Combine(_root, "cache"), RepositoryIdentity);

    private string EntryContentRoot() => Path.Combine(EntryRoot(), "content");

    private string NewWorkspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string NewNodeWorkspace(string name)
    {
        var path = NewWorkspace(name);
        File.WriteAllText(
            Path.Combine(path, "package-lock.json"),
            """{"name":"fixture","lockfileVersion":3,"packages":{}}""");
        return path;
    }

    /// <summary>
    /// The shape the CAC-18 entry actually had: vite present but its client
    /// bundle empty, and no install ledger beside it.
    /// </summary>
    private static void WriteTruncatedViteTree(string workspace)
    {
        var vite = Path.Combine(workspace, "node_modules", "vite", "dist", "node", "chunks");
        Directory.CreateDirectory(vite);
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "vite", "dist", "client"));
        File.WriteAllText(Path.Combine(vite, "config.js"), "// truncated install\n");
    }

    private static void WriteCompleteInstall(string workspace)
    {
        WriteTruncatedViteTree(workspace);
        File.WriteAllText(
            Path.Combine(
                workspace,
                DependencyPreparationState.DependencyDirectoryName,
                DependencyPreparationState.InstallLedgerFileName),
            """{"name":"fixture","lockfileVersion":3,"packages":{}}""");
    }

    private static void StampCurrentLockHash(string workspace)
        => DependencyPreparationState.Stamp(
            workspace,
            DependencyPreparationState.ComputeLockHash(workspace, ["package-lock.json"]));

    private static string LedgerPath(string workspace)
        => Path.Combine(
            workspace,
            DependencyPreparationState.DependencyDirectoryName,
            DependencyPreparationState.InstallLedgerFileName);
}
