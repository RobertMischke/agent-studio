using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Serializes every test that reads or writes the process-global
/// <see cref="AgentStudio.Cli.CliRepairGate"/> cooldown state. xUnit runs
/// collections in parallel by default (same class of bug as AGT-2025's
/// <c>CodexDetectedDefaultCollection</c>); without this, a reset in one test
/// class could clear the cooldown out from under a concurrency assertion
/// running in the other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliRepairGateCollection
{
    public const string Name = "CliRepairGate";
}
