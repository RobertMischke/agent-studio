using AgentStudio.Publishing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// PUB-1 - pure unit tests for the workflow-fact extraction that drives publish
/// target derivation. No repository needed: the parser turns a workflow file's
/// text into the coarse booleans the derivation branches on (release trigger,
/// npm/NuGet publish step, Pages deploy). Pinning these keeps the derivation
/// robust against the two reference layouts (coding-agent-runner: NuGet + Website;
/// coding-agent-chat: npm + Website) without a YAML dependency.
/// </summary>
public class PublishWorkflowParserTests
{
    [Fact]
    public void Parse_NuGetReleaseWorkflow_DetectsTagTriggerAndNuGet()
    {
        const string yaml = """
            name: release
            on:
              push:
                tags:
                  - 'v*'
            jobs:
              publish:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet pack -c Release
                  - run: dotnet nuget push bin/*.nupkg --api-key $KEY
            """;
        var facts = PublishWorkflowParser.Parse("release.yml", yaml);

        Assert.True(facts.HasReleaseTrigger);
        Assert.True(facts.PublishesNuGet);
        Assert.False(facts.PublishesNpm);
        Assert.False(facts.DeploysWebsite);
    }

    [Fact]
    public void Parse_NpmReleaseWorkflow_DetectsTagTriggerAndNpm()
    {
        const string yaml = """
            on:
              push:
                tags: ['v*']
            jobs:
              publish:
                steps:
                  - run: npm ci
                  - run: npm publish --access public
            """;
        var facts = PublishWorkflowParser.Parse("release.yml", yaml);

        Assert.True(facts.HasReleaseTrigger);
        Assert.True(facts.PublishesNpm);
        Assert.False(facts.PublishesNuGet);
    }

    [Fact]
    public void Parse_ReleasePublishedEvent_CountsAsReleaseTrigger()
    {
        const string yaml = """
            on:
              release:
                types: [published]
            jobs:
              build:
                steps:
                  - run: npm publish
            """;
        var facts = PublishWorkflowParser.Parse("publish.yml", yaml);
        Assert.True(facts.HasReleaseTrigger);
        Assert.True(facts.PublishesNpm);
    }

    [Fact]
    public void Parse_PagesDeployWorkflow_DetectsWebsiteAndArtifactPath()
    {
        const string yaml = """
            name: deploy website
            on:
              push:
                branches: [main]
            jobs:
              deploy:
                steps:
                  - uses: actions/upload-pages-artifact@v3
                    with:
                      path: website
                  - uses: actions/deploy-pages@v4
            """;
        var facts = PublishWorkflowParser.Parse("deploy-website.yml", yaml);

        Assert.True(facts.DeploysWebsite);
        Assert.Equal("website", facts.PagesArtifactPath);
        Assert.False(facts.HasReleaseTrigger);
        Assert.False(facts.PublishesNpm);
        Assert.False(facts.PublishesNuGet);
    }

    [Fact]
    public void Parse_GhPagesByName_DetectsWebsiteEvenWithoutKnownAction()
    {
        var facts = PublishWorkflowParser.Parse("pages.yml", "on: { push: { branches: [main] } }\njobs: {}\n");
        Assert.True(facts.DeploysWebsite);
    }

    [Fact]
    public void Parse_PlainCiWorkflow_IsNeitherReleaseNorWebsite()
    {
        const string yaml = """
            on:
              pull_request:
              push:
                branches: [main, develop]
            jobs:
              test:
                steps:
                  - run: dotnet test
                  - run: npm test
            """;
        var facts = PublishWorkflowParser.Parse("ci.yml", yaml);

        Assert.False(facts.HasReleaseTrigger);
        Assert.False(facts.DeploysWebsite);
        // `npm test` / `dotnet test` are not publish steps.
        Assert.False(facts.PublishesNpm);
        Assert.False(facts.PublishesNuGet);
    }

    [Theory]
    [InlineData("branches: [main]", false)]      // a branch push, not a tag push
    [InlineData("tags: ['v*']", true)]
    [InlineData("tags: [ \"v[0-9]+.[0-9]+.*\" ]", true)]
    [InlineData("tags:\n    - v1.*", true)]
    public void HasTagPushTrigger_RecognisesVersionTagGlobs(string triggerFragment, bool expected)
    {
        var yaml = "on:\n  push:\n    " + triggerFragment.Replace("\n", "\n  ") + "\n";
        Assert.Equal(expected, PublishWorkflowParser.HasTagPushTrigger(yaml));
    }

    [Theory]
    [InlineData("- 'v*'", true)]
    [InlineData("['v1.2.3']", true)]
    [InlineData("- \"1.0.*\"", true)]
    [InlineData("[main]", false)]
    [InlineData("- release/*", false)]
    public void LooksVersionGlob_MatchesVersionShapes(string fragment, bool expected)
        => Assert.Equal(expected, PublishWorkflowParser.LooksVersionGlob(fragment));
}
