namespace AgentStudio.DemoReplay;

/// <summary>
/// In-memory anti-replay cursor for the public-demo replay scope. It is the only
/// state the scope owns: the highest epoch this instance has seen and the last
/// sequence accepted inside it. A restart starts a fresh scene, which is exactly
/// what the six-hour replacement job produces anyway.
/// </summary>
public sealed class DemoReplayEpochLedger
{
    private readonly object _gate = new();
    private DemoReplayCursor? _cursor;

    public DemoReplayCursor? Peek()
    {
        lock (_gate) return _cursor;
    }

    /// <summary>
    /// Atomically advances the cursor. Returns false when a concurrent frame won
    /// the race, so the caller emits the same monotonic denial the policy would.
    /// </summary>
    public bool TryAdvance(long epoch, long sequence)
    {
        lock (_gate)
        {
            if (_cursor is not null)
            {
                if (epoch < _cursor.Epoch) return false;
                if (epoch == _cursor.Epoch && sequence <= _cursor.Sequence) return false;
            }
            _cursor = new DemoReplayCursor(epoch, sequence);
            return true;
        }
    }
}
