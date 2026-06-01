using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Abstraction over the task scanner that the executor (Runner) side depends
/// on, so executor services need not reference the concrete server-side
/// <c>TaskScannerService</c>. Part of the Server/Runner decoupling: in-process
/// the server binds this to TaskScannerService; in the distributed mode a Runner
/// can bind it to an HTTP client against the Server's /api/tasks surface.
/// </summary>
public interface ITaskScanner
{
    /// <summary>All tasks across configured watch paths (cached snapshot).</summary>
    List<TaskInfo> ScanAllJobs();
}
