using System.Text;

namespace AgentStudio.TestSupport;

/// <summary>
/// Writes a throwaway "CLI" that prints fixed lines and exits 0, in the form the
/// current host can execute natively.
///
/// Tests that hand a fake binary to the runner cannot use a <c>#!/bin/sh</c>
/// script: Windows has no shebang handling, and <c>File.SetUnixFileMode</c>
/// throws <see cref="PlatformNotSupportedException"/> there, so the test fails in
/// its own arrangement before reaching the behaviour it wants to check. The
/// behaviour usually is portable - only the stub's packaging is not.
///
/// For a stub that must replay a recorded stream (framing, sentinels, exit
/// codes), use <c>testdata/cli-fixtures/fake-cli.mjs</c> instead. This helper is
/// for the simple case: emit these lines, exit 0, ignore all arguments.
/// </summary>
public static class StubCli
{
    /// <summary>
    /// Create the stub under <paramref name="directory"/> and return its full
    /// path. <paramref name="name"/> is the base name without extension; the
    /// platform-appropriate one is appended.
    /// </summary>
    public static async Task<string> WriteAsync(
        string directory,
        string name,
        params string[] stdoutLines)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            // A .cmd is directly startable through CreateProcess (Windows hands it
            // to cmd.exe itself), so no shell wrapper is needed at the call site.
            var batch = Path.Combine(directory, name + ".cmd");
            var body = new StringBuilder("@echo off\r\n");
            foreach (var line in stdoutLines)
                body.Append(line.Length == 0 ? "echo." : "echo " + EscapeForBatch(line)).Append("\r\n");
            // No BOM: cmd.exe would treat it as part of the first command.
            await File.WriteAllTextAsync(batch, body.ToString(), new UTF8Encoding(false));
            return batch;
        }

        var script = Path.Combine(directory, name + ".sh");
        var shell = new StringBuilder("#!/bin/sh\n");
        foreach (var line in stdoutLines)
            shell.Append("printf '%s\\n' ").Append(QuoteForShell(line)).Append('\n');
        await File.WriteAllTextAsync(script, shell.ToString());
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>
    /// Batch has no quoting that survives <c>echo</c>, so the metacharacters are
    /// escaped individually. <c>%</c> is doubled because the file is parsed as a
    /// batch script; the rest take a caret.
    /// </summary>
    private static string EscapeForBatch(string line)
    {
        var escaped = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '%') escaped.Append("%%");
            else if (c is '^' or '&' or '<' or '>' or '|') escaped.Append('^').Append(c);
            else escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>Single-quote for sh, closing and reopening around embedded quotes.</summary>
    private static string QuoteForShell(string line)
        => "'" + line.Replace("'", "'\\''") + "'";
}
