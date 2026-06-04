using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the long-op recogniser that feeds the watchdog's widened silence
/// budget (ASS-665). Pure string matching, sub-millisecond, runs on every
/// default <c>dotnet test</c>. The contract: known dev-server / build /
/// poll-loop commands are recognised so a legitimate silent wait is not
/// killed as a hang, and ordinary fast commands are not.
/// </summary>
public class LongRunningOperationDetectorTests
{
    [Theory]
    // The motivating case: ng serve cold compile.
    [InlineData("Bash ng serve")]
    [InlineData("Bash cd frontend && ng serve --port 4200")]
    [InlineData("Bash npx ng build --configuration production")]
    // Node package-manager builds / dev servers.
    [InlineData("Bash npm run build")]
    [InlineData("Bash npm start")]
    [InlineData("Bash pnpm dev")]
    [InlineData("Bash yarn build")]
    [InlineData("Bash npm ci")]
    // Bundlers and frameworks.
    [InlineData("Bash npx vite build")]
    [InlineData("Bash node_modules/.bin/webpack --watch")]
    [InlineData("Bash next build")]
    // .NET.
    [InlineData("Bash dotnet build")]
    [InlineData("Bash dotnet test --filter Category=Unit")]
    [InlineData("Bash dotnet run --project backend")]
    // Test toolchains.
    [InlineData("Bash npx playwright test")]
    [InlineData("Bash cargo test")]
    [InlineData("Bash go test ./...")]
    // Dev-server waits / poll loops: the agent is alive, polling a port.
    [InlineData("Bash npx wait-on http://localhost:4200")]
    [InlineData("Bash curl --retry 30 --retry-delay 2 http://localhost:4200")]
    [InlineData("Bash until curl -sf http://localhost:4200; do sleep 2; done")]
    [InlineData("Bash while ! curl -s localhost:3000; do sleep 1; done")]
    public void RecognisesKnownLongOps(string command)
    {
        Assert.True(LongRunningOperationDetector.IsLongRunningOperation(command),
            $"expected long-op for: {command}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Read frontend/src/app/app.component.ts")]
    [InlineData("Grep TODO")]
    [InlineData("Bash git status")]
    [InlineData("Bash ls -la")]
    [InlineData("Bash cat package.json")]
    [InlineData("Bash echo done")]
    [InlineData("Edit src/main.ts")]
    public void DoesNotRecogniseOrdinaryCommands(string? command)
    {
        Assert.False(LongRunningOperationDetector.IsLongRunningOperation(command),
            $"did not expect long-op for: {command ?? "<null>"}");
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(LongRunningOperationDetector.IsLongRunningOperation("Bash NG SERVE"));
        Assert.True(LongRunningOperationDetector.IsLongRunningOperation("Bash DotNet Build"));
    }

    [Fact]
    public void TryMatch_ReportsTheMatchedFragment()
    {
        Assert.True(LongRunningOperationDetector.TryMatch("Bash ng serve --open", out var matched));
        Assert.Equal("ng serve", matched);

        Assert.False(LongRunningOperationDetector.TryMatch("Bash git log", out var none));
        Assert.Equal(string.Empty, none);
    }
}
