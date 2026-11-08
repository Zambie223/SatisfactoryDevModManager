namespace Sdmm.Core.Config;

/// <summary>
/// Tracks a short sequence of actions in the Settings window and raises <see cref="Completed"/> when it's done.
/// A step out of order starts over; closing the window discards progress.
/// </summary>
public sealed class SettingsSequence
{
    public const int ForkTogglesNeeded = 10;

    private int _step = 1;   // opening the window is step 1
    private int _forkToggles;

    /// <summary>Raised when the sequence completes.</summary>
    public event Action? Completed;

    public int Step => _step;

    public void CacheCleared()
    {
        if (_step == 1) _step = 2;
        else Reset();
    }

    public void TokenHelpExpanded()
    {
        if (_step == 2) _step = 3;
        else Reset();
    }

    public void ForksToggled()
    {
        if (_step != 3)
        {
            Reset();
            return;
        }
        if (++_forkToggles < ForkTogglesNeeded) return;
        Reset();
        Completed?.Invoke();
    }

    private void Reset()
    {
        _step = 1;
        _forkToggles = 0;
    }
}
