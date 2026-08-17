using System.Globalization;

namespace AgentStudio.WindowsTunnelSupervision;

/// <summary>
/// Pure interpretation of the raw facts <see cref="WindowsTunnelSupervisionService"/>
/// reads at the OS boundary: one <c>schtasks /Query /FO LIST /V</c> transcript per
/// task, and the tail of the watchdog's append-only journal
/// (<c>&lt;devspace&gt;/.tunnel-watchdog.log</c>, written by
/// <c>deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh</c>).
/// </summary>
public static class WindowsTunnelSupervisionPolicy
{
    /// <summary>
    /// Maps one <c>schtasks /Query</c> result to a task's presence. A non-zero
    /// exit code means schtasks could not find the named task (it has never
    /// been registered); any other outcome is read from the "Status:" line.
    /// </summary>
    public static ScheduledTaskStatus ParseScheduledTaskStatus(string taskName, int exitCode, string stdout)
    {
        if (exitCode != 0) return new ScheduledTaskStatus(taskName, ScheduledTaskPresence.NotRegistered, null, null);

        var status = FindFieldValue(stdout, "Status:");
        var presence = status?.Trim().ToUpperInvariant() switch
        {
            "RUNNING" => ScheduledTaskPresence.Running,
            "READY" => ScheduledTaskPresence.Registered,
            "DISABLED" => ScheduledTaskPresence.Disabled,
            _ => ScheduledTaskPresence.Unknown,
        };
        var lastRunAt = FindFieldValue(stdout, "Last Run Time:");
        var lastRunResult = FindFieldValue(stdout, "Last Result:");
        return new ScheduledTaskStatus(taskName, presence, lastRunResult, lastRunAt);
    }

    /// <summary>
    /// Scans the watchdog journal tail for the most recent successful heal
    /// (<c>event=heal_succeeded</c>) and counts <c>event=heal_failure_count</c>
    /// lines that appear after it, i.e. failed heal attempts since the tunnel
    /// last recovered. Lines are chronological, oldest first, one event per line
    /// (see tunnel-watchdog.sh's <c>journal()</c> helper).
    /// </summary>
    public static (string? At, string? Detail, int ConsecutiveFailures) ParseHealHistory(string? logTail)
    {
        if (string.IsNullOrWhiteSpace(logTail)) return (null, null, 0);

        string? lastHealAt = null;
        string? lastHealDetail = null;
        var failuresSinceHeal = 0;
        foreach (var rawLine in logTail.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Contains("event=heal_succeeded", StringComparison.Ordinal))
            {
                lastHealAt = ExtractLeadingTimestamp(line);
                lastHealDetail = "Tunnel restored after a functional probe failure.";
                failuresSinceHeal = 0;
            }
            else if (line.Contains("event=heal_failure_count", StringComparison.Ordinal))
            {
                failuresSinceHeal++;
            }
        }
        return (lastHealAt, lastHealDetail, failuresSinceHeal);
    }

    private static string? FindFieldValue(string stdout, string fieldLabel)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith(fieldLabel, StringComparison.Ordinal)) continue;
            var value = line[fieldLabel.Length..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string? ExtractLeadingTimestamp(string line)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex <= 0) return null;
        var candidate = line[..spaceIndex];
        return DateTimeOffset.TryParse(
            candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            ? candidate
            : null;
    }
}
