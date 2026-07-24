using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

// MachineBound 22.07.: startet drei echte Dienste (task-server, studio-bff, runner) als
// Prozesse und bindet freie Ports - per Definition maschinengebunden, darf nicht im Gate laufen.
[Trait("Category", "MachineBound")]
public sealed class TopologyTests
{
    [Fact(Timeout = 60000)]
    public async Task Task_server_studio_bff_and_runner_have_independent_process_lifecycles()
    {
        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        using var runnerWork = new TempDirectory();
        var serverUrl = $"http://127.0.0.1:{FreePort()}";
        var studioUrl = $"http://127.0.0.1:{FreePort()}";
        using var server = Start(root, "task-server/TaskServer.csproj",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path);
        var serverPid = server.Process.Id;
        await WaitForAsync(serverUrl + "/readyz");

        using var serverClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
        var initial = await serverClient.GetFromJsonAsync<TaskServerStatusDto>("/api/v1/management/status");
        Assert.NotNull(initial);

        using var studio = Start(root, "studio-bff/StudioBff.csproj",
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl);
        await WaitForAsync(studioUrl + "/healthz");
        using var studioClient = new HttpClient { BaseAddress = new Uri(studioUrl) };
        var throughStudio = await studioClient.GetFromJsonAsync<TaskServerStatusDto>("/api/v1/management/status");
        Assert.Equal(initial.ServerId, throughStudio!.ServerId);

        using var runner = Start(root, "runner/AgentRunner.csproj",
            "--poll",
            "--server", serverUrl,
            "--runner-id", "topology-runner",
            "--runner-name", "topology-runner",
            "--workdir", runnerWork.Path,
            "--poll-seconds", "1");
        await WaitForAuditAsync(serverClient, "runner.registered");
        Assert.False(runner.Process.HasExited);

        studio.Stop();
        Assert.False(server.Process.HasExited);
        Assert.False(runner.Process.HasExited);
        var whileDetached = await serverClient.GetFromJsonAsync<TaskServerStatusDto>("/api/v1/management/status");
        Assert.Equal(initial.ServerId, whileDetached!.ServerId);

        using var restartedStudio = Start(root, "studio-bff/StudioBff.csproj",
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl);
        await WaitForAsync(studioUrl + "/healthz");
        var afterRestart = await studioClient.GetFromJsonAsync<TaskServerStatusDto>("/api/v1/management/status");
        Assert.Equal(initial.ServerId, afterRestart!.ServerId);
        Assert.Equal(serverPid, server.Process.Id);
    }

    private static async Task WaitForAuditAsync(HttpClient client, string action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var records = await client.GetFromJsonAsync<List<AuditRecordDto>>("/api/v1/management/audit");
            if (records?.Any(record => record.Action == action) == true) return;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Audit action '{action}' was not observed.");
    }

    private static async Task WaitForAsync(string url)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Process did not become ready at {url}: {last?.Message}");
    }

    private static RunningProcess Start(string root, string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {project}.");
        var running = new RunningProcess(process);
        process.OutputDataReceived += (_, eventArgs) => running.Append(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => running.Append(eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return running;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RunningProcess(Process process) : IDisposable
    {
        private readonly List<string> _output = [];
        public Process Process { get; } = process;

        public void Append(string? line)
        {
            if (line is null) return;
            lock (_output) _output.Add(line);
        }

        public void Stop()
        {
            if (Process.HasExited) return;
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }

        public void Dispose() => Stop();

        public override string ToString()
        {
            lock (_output) return string.Join(Environment.NewLine, _output.TakeLast(30));
        }
    }
}
