using NeonMon.Models;

namespace NeonMon.UI;

// Shows every visible pulse in Peek while a key combination stays down, then gives each pulse back. Pulses that were
// Open (or pinned) are left alone, and the release is checked only while the reveal is active.
internal sealed class GroupReveal : IDisposable
{
    private readonly Func<IEnumerable<WidgetForm>> _pulses;
    private readonly System.Windows.Forms.Timer _releaseTimer = new() { Interval = 30 };
    private readonly Dictionary<WidgetForm, RevealState> _held = [];
    private Func<bool> _stillHeld = () => false;

    public GroupReveal(Func<IEnumerable<WidgetForm>> pulses)
    {
        _pulses = pulses;
        _releaseTimer.Tick += (_, _) =>
        {
            if (!_stillHeld())
            {
                End();
            }
        };
    }

    public bool Active { get; private set; }

    public void Begin(Func<bool> stillHeld)
    {
        if (Active)
        {
            return;
        }

        Active = true;
        _stillHeld = stillHeld;
        foreach (var pulse in _pulses().Where(pulse => pulse.Visible && pulse.State != RevealState.Open))
        {
            _held[pulse] = pulse.State;
            pulse.PeekHeld = true;
            pulse.SetRevealState(RevealState.Peek);
        }

        _releaseTimer.Start();
    }

    public void End()
    {
        if (!Active)
        {
            return;
        }

        _releaseTimer.Stop();
        Active = false;
        var pointer = Cursor.Position;
        foreach (var (pulse, previous) in _held)
        {
            pulse.PeekHeld = false;
            // A pulse the user opened meanwhile stays open; one under the pointer is left to its own hover logic.
            if (previous == RevealState.Hidden && pulse.State == RevealState.Peek && !pulse.HoldsPointer(pointer))
            {
                pulse.SetRevealState(RevealState.Hidden);
            }
        }

        _held.Clear();
    }

    public void Dispose() => _releaseTimer.Dispose();
}
