using Microsoft.AspNetCore.Hosting;

namespace AgentStudio.TaskServer;

public sealed class TaskServerBootstrapOptions
{
    public const string NoAuthentication = "none";
    public const string BearerAuthentication = "bearer";

    private TaskServerBootstrapOptions(
        string listenUrl,
        string storePath,
        string backupPath,
        string authenticationMode,
        string? authenticationToken,
        bool usesLegacyRoleAuthentication)
    {
        ListenUrl = listenUrl;
        StorePath = storePath;
        BackupPath = backupPath;
        AuthenticationMode = authenticationMode;
        AuthenticationToken = authenticationToken;
        UsesLegacyRoleAuthentication = usesLegacyRoleAuthentication;
    }

    public string ListenUrl { get; }
    public string StorePath { get; }
    public string BackupPath { get; }
    public string AuthenticationMode { get; }
    public string? AuthenticationToken { get; }
    public bool UsesSharedBearerAuthentication =>
        string.Equals(AuthenticationMode, BearerAuthentication, StringComparison.Ordinal);
    public bool UsesLegacyRoleAuthentication { get; }
    public bool RequiresAuthentication =>
        UsesSharedBearerAuthentication || UsesLegacyRoleAuthentication;

    public static TaskServerBootstrapOptions Load(IConfiguration configuration)
    {
        var listenUrl = First(
            configuration[WebHostDefaults.ServerUrlsKey],
            configuration["LISTEN_URL"],
            configuration[$"{TaskServerOptions.SectionName}:ListenUrl"],
            "http://127.0.0.1:5071")
            .Trim();
        var storePath = First(
            configuration["STORE_PATH"],
            configuration[$"{TaskServerOptions.SectionName}:DataDirectory"],
            "data")
            .Trim();
        var backupPath = FirstOrNull(
            configuration["BACKUP_PATH"],
            configuration[$"{TaskServerOptions.SectionName}:BackupDirectory"]);
        var authenticationMode = First(
                configuration["AUTH"],
                configuration["AUTH_MODE"],
                configuration[$"{TaskServerOptions.SectionName}:AuthMode"],
                NoAuthentication)
            .Trim()
            .ToLowerInvariant();
        var usesLegacyRoleAuthentication = configuration.GetValue<bool>(
            $"{TaskServerOptions.SectionName}:RequireAuthentication");

        if (authenticationMode is not (NoAuthentication or BearerAuthentication))
            throw new InvalidOperationException(
                "AUTH must be 'none' or 'bearer'.");

        var token = FirstOrNull(
            configuration["AUTH_TOKEN"],
            configuration[$"{TaskServerOptions.SectionName}:AuthToken"]);
        var tokenFile = FirstOrNull(
            configuration["AUTH_TOKEN_FILE"],
            configuration[$"{TaskServerOptions.SectionName}:AuthTokenFile"]);
        if (token is not null && tokenFile is not null)
            throw new InvalidOperationException(
                "Configure only one of AUTH_TOKEN or AUTH_TOKEN_FILE.");
        if (tokenFile is not null)
        {
            var resolvedTokenFile = Path.GetFullPath(tokenFile);
            if (!File.Exists(resolvedTokenFile))
                throw new InvalidOperationException(
                    $"AUTH_TOKEN_FILE does not exist: {resolvedTokenFile}");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(resolvedTokenFile);
                if ((mode & (UnixFileMode.OtherRead
                             | UnixFileMode.OtherWrite
                             | UnixFileMode.OtherExecute)) != 0)
                {
                    throw new InvalidOperationException(
                        "AUTH_TOKEN_FILE must not be accessible to other users.");
                }
            }
            token = File.ReadAllText(resolvedTokenFile).Trim();
        }

        if (authenticationMode == BearerAuthentication
            && (token is null || token.Length < 32))
        {
            throw new InvalidOperationException(
                "AUTH=bearer requires AUTH_TOKEN or AUTH_TOKEN_FILE with at least 32 characters.");
        }
        if (authenticationMode == NoAuthentication && token is not null)
            throw new InvalidOperationException(
                "AUTH_TOKEN and AUTH_TOKEN_FILE are invalid when AUTH=none.");
        if (usesLegacyRoleAuthentication
            && authenticationMode == BearerAuthentication)
        {
            throw new InvalidOperationException(
                "Configure either AUTH=bearer or legacy role authentication, not both.");
        }
        if (authenticationMode == NoAuthentication
            && !usesLegacyRoleAuthentication
            && !ListensOnlyOnLoopback(listenUrl))
        {
            throw new InvalidOperationException(
                "AUTH=none is permitted only when every LISTEN_URL address is loopback.");
        }

        var resolvedStorePath = ResolveStorePath(storePath);
        return new TaskServerBootstrapOptions(
            listenUrl,
            resolvedStorePath,
            backupPath is null
                ? Path.Combine(resolvedStorePath, "backups")
                : ResolvePath(backupPath),
            authenticationMode,
            token,
            usesLegacyRoleAuthentication);
    }

    private static bool ListensOnlyOnLoopback(string value)
    {
        var addresses = value.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0) return false;
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !(uri.IsLoopback
                     || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        return true;
    }

    private static string ResolvePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));

    private static string ResolveStorePath(string path)
    {
        if (!string.Equals(path, "user-data", StringComparison.OrdinalIgnoreCase))
            return ResolvePath(path);
        var userData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(userData, "AgentStudio", "task-server");
    }

    private static string First(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FirstOrNull(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
