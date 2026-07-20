namespace AgentStudio.TaskServer.Contracts;

public static class TaskServerProtocol
{
    public const int Current = 1;
    public const int MinimumSupported = 1;
    public const int MaximumSupported = 1;
    public const string HeaderName = "X-Task-Protocol-Version";
    public const string ClientVersionHeaderName = "X-Task-Client-Version";

    public static bool Supports(int version)
        => version >= MinimumSupported && version <= MaximumSupported;
}

public sealed record ProtocolRangeDto(
    int Current,
    int MinimumSupported,
    int MaximumSupported,
    string ServerVersion,
    string ServerId,
    IReadOnlyList<string> ClientKinds);

public sealed record ProtocolCompatibilityRequest(
    string ClientKind,
    string ClientVersion,
    int ProtocolVersion);

public sealed record ProtocolCompatibilityResponse(
    bool Supported,
    ProtocolRangeDto Server,
    string? Reason = null);

public sealed record ApiError(string Code, string Message, object? Detail = null);
