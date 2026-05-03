using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Serialises the test classes that drive the live claude / codex /
/// gemini binaries against <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
/// xUnit's default parallelism would otherwise let two such tests
/// spawn the same CLI in the same per-cwd
/// <c>~/.claude/projects/...</c> session DB simultaneously, which
/// produces the same lock contention the live ASP.NET hang
/// investigation kept tripping over. Inside this collection, tests
/// run one at a time; default-suite tests stay parallel.
/// </summary>
[CollectionDefinition("LiveCli", DisableParallelization = true)]
public sealed class LiveCliCollection { }
