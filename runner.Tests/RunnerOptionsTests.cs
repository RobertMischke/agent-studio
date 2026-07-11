using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void Positional_argument_is_the_task_key()
    {
        var (_, taskKey, once, help) = RunnerOptions.Parse(["AGT-1939"]);
        Assert.Equal("AGT-1939", taskKey);
        Assert.True(once);
        Assert.False(help);
    }

    [Fact]
    public void Flags_override_task_and_server()
    {
        var (options, taskKey, _, _) = RunnerOptions.Parse(
            ["--task", "AGT-1", "--server", "https://central/", "--runner-name", "agent-runner-01"]);
        Assert.Equal("AGT-1", taskKey);
        Assert.Equal("https://central", options.ServerUrl); // trailing slash trimmed
        Assert.Equal("agent-runner-01", options.RunnerName);
    }

    [Fact]
    public void Help_flag_is_detected_without_a_task()
    {
        var (_, taskKey, _, help) = RunnerOptions.Parse(["--help"]);
        Assert.True(help);
        Assert.Null(taskKey);
    }

    [Fact]
    public void Poll_flag_disables_once()
    {
        var (_, _, once, _) = RunnerOptions.Parse(["AGT-1", "--poll"]);
        Assert.False(once);
    }

    [Fact]
    public void Daemon_slot_flags_are_parsed()
    {
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--poll", "--max-parallelism", "4", "--poll-seconds", "9"]);
        Assert.Equal(4, options.HostMaxParallelism);
        Assert.Equal(9, options.PollSeconds);
    }

    [Fact]
    public void Existing_client_identity_can_be_pinned_from_the_cli()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["--client-id", "  agent-runner-01  "]);

        Assert.Equal("agent-runner-01", options.ClientId);
    }

    [Theory]
    [InlineData("AGT-20", "AGT-20")]
    [InlineData("project/task 20", "project-task-20")]
    public void Worktree_segment_is_filesystem_safe(string input, string expected)
        => Assert.Equal(expected, GitWorkspace.SafeSegment(input));

    [Fact]
    public void Project_id_maps_to_an_isolated_shared_clone_cache()
    {
        var root = Path.Combine("runner", "work");

        Assert.Equal(
            Path.Combine(root, "PROJ-042"),
            GitWorkspace.CachePathForProject(root, "PROJ-042"));
        Assert.NotEqual(
            GitWorkspace.CachePathForProject(root, "PROJ-042"),
            GitWorkspace.CachePathForProject(root, "PROJ-043"));
    }

    [Fact]
    public void Missing_project_id_keeps_the_legacy_single_repo_cache_path()
        => Assert.Equal("runner-work", GitWorkspace.CachePathForProject("runner-work", null));

    [Fact]
    public void Health_check_flag_sets_health_check_only_and_needs_no_task_key()
    {
        var (options, taskKey, _, help) = RunnerOptions.Parse(["--health-check"]);
        Assert.True(options.HealthCheckOnly);
        Assert.Null(taskKey);
        Assert.False(help);
    }

    [Fact]
    public void Health_check_only_defaults_false_for_a_normal_run()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["AGT-1"]);
        Assert.False(options.HealthCheckOnly);
    }
}

public class AgentCliArgsTests
{
    [Fact]
    public void Simple_args_split_on_whitespace()
    {
        Assert.Equal(["-p", "--verbose"], AgentCliProcess.SplitArgs("-p --verbose"));
    }

    [Fact]
    public void Quoted_segment_stays_together()
    {
        Assert.Equal(["--flag", "two words"], AgentCliProcess.SplitArgs("--flag \"two words\""));
    }

    [Fact]
    public void Empty_args_yield_empty_list()
    {
        Assert.Empty(AgentCliProcess.SplitArgs(""));
    }
}
