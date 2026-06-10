using System.Runtime.InteropServices;

namespace AgentStudio.Runner;

/// <summary>
/// Abstraction over the host's "keep the system awake" power request so the
/// lifecycle (<see cref="SystemKeepAwake"/>) can be unit-tested without
/// touching real OS power state. Implementations must be idempotent-friendly:
/// the coordinator only calls <see cref="Acquire"/> on a 0-&gt;active edge and
/// <see cref="Release"/> on an active-&gt;0 edge.
/// </summary>
public interface ISystemPowerRequest
{
    /// <summary>Begin asserting that the system must stay awake.</summary>
    void Acquire(string reason);

    /// <summary>Update the human-readable reason shown by diagnostics
    /// (e.g. <c>powercfg /requests</c>) without dropping the request.</summary>
    void UpdateReason(string reason);

    /// <summary>Stop asserting; the system may sleep on its normal idle
    /// timer again.</summary>
    void Release();
}

/// <summary>
/// No-op power request used on non-Windows hosts, in tests, and when
/// keep-awake is disabled by configuration. Records the last call so tests can
/// assert lifecycle without OS coupling.
/// </summary>
public sealed class NoopPowerRequest : ISystemPowerRequest
{
    public int AcquireCount { get; private set; }
    public int ReleaseCount { get; private set; }
    public bool Held { get; private set; }
    public string? LastReason { get; private set; }

    public void Acquire(string reason)
    {
        AcquireCount++;
        Held = true;
        LastReason = reason;
    }

    public void UpdateReason(string reason) => LastReason = reason;

    public void Release()
    {
        if (!Held) return;
        ReleaseCount++;
        Held = false;
    }
}

/// <summary>
/// Windows implementation backed by the Power Request API
/// (<c>PowerCreateRequest</c> / <c>PowerSetRequest</c> /
/// <c>PowerClearRequest</c>). A <see cref="PowerRequestType.SystemRequired"/>
/// request keeps the machine from sleeping on its idle timer while runs are
/// active; the display is deliberately left free to sleep. The request carries
/// a named reason string so it shows up under <c>powercfg /requests</c> for
/// diagnosis.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsPowerRequest : ISystemPowerRequest, IDisposable
{
    private SafePowerRequestHandle? _handle;
    private bool _set;

    public void Acquire(string reason)
    {
        _handle?.Dispose();
        _handle = CreateRequest(reason);
        if (_handle is { IsInvalid: false }
            && PowerSetRequest(_handle, PowerRequestType.SystemRequired))
        {
            _set = true;
        }
    }

    public void UpdateReason(string reason)
    {
        // The reason string is fixed at creation time, so to change it we
        // briefly re-create the request. Acquire the new one before clearing
        // the old to avoid a window where the system could slip into sleep.
        var previous = _handle;
        var previousSet = _set;
        _handle = null;
        _set = false;

        Acquire(reason);

        if (previous is { IsInvalid: false })
        {
            if (previousSet) PowerClearRequest(previous, PowerRequestType.SystemRequired);
            previous.Dispose();
        }
    }

    public void Release()
    {
        if (_handle is { IsInvalid: false } && _set)
        {
            PowerClearRequest(_handle, PowerRequestType.SystemRequired);
        }
        _set = false;
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose() => Release();

    private static SafePowerRequestHandle CreateRequest(string reason)
    {
        var context = new REASON_CONTEXT
        {
            Version = POWER_REQUEST_CONTEXT_VERSION,
            Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = reason,
        };
        return PowerCreateRequest(ref context);
    }

    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    private sealed class SafePowerRequestHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePowerRequestHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePowerRequestHandle PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafePowerRequestHandle powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafePowerRequestHandle powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Couples a single <see cref="ISystemPowerRequest"/> to the live count of
/// active agent runs. This is the one place that decides whether the host
/// should stay awake: callers feed it the total active-run count each tick via
/// <see cref="Update"/>, and it asserts the request while that count is &gt; 0
/// and releases it at 0. Updates are idempotent - repeating the same count is a
/// no-op, so it is safe to call every loop iteration.
/// </summary>
public sealed class SystemKeepAwake : IDisposable
{
    private readonly ISystemPowerRequest _request;
    private readonly object _gate = new();
    private int _heldForCount;
    private bool _held;

    /// <summary>When false, <see cref="Update"/> is a no-op (setting off).</summary>
    public bool Enabled { get; }

    public SystemKeepAwake(ISystemPowerRequest request, bool enabled = true)
    {
        _request = request;
        Enabled = enabled;
    }

    /// <summary>True while the keep-awake request is currently asserted.</summary>
    public bool IsHeld
    {
        get { lock (_gate) return _held; }
    }

    /// <summary>
    /// Reconcile the power request with the current total number of active
    /// agent runs across all projects. Acquires on the first active run,
    /// refreshes the reason string when the count changes while held, and
    /// releases when the count returns to zero.
    /// </summary>
    public void Update(int activeRunCount)
    {
        if (!Enabled) return;
        if (activeRunCount < 0) activeRunCount = 0;

        lock (_gate)
        {
            if (activeRunCount > 0)
            {
                if (!_held)
                {
                    _request.Acquire(FormatReason(activeRunCount));
                    _held = true;
                    _heldForCount = activeRunCount;
                }
                else if (activeRunCount != _heldForCount)
                {
                    _request.UpdateReason(FormatReason(activeRunCount));
                    _heldForCount = activeRunCount;
                }
            }
            else if (_held)
            {
                _request.Release();
                _held = false;
                _heldForCount = 0;
            }
        }
    }

    private static string FormatReason(int activeRunCount)
        => $"Agent Studio: {activeRunCount} aktive Agent-Run{(activeRunCount == 1 ? "" : "s")}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_held)
            {
                _request.Release();
                _held = false;
            }
        }
        (_request as IDisposable)?.Dispose();
    }
}
