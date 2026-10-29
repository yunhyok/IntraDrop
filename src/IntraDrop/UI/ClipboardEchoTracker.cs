using IntraDrop.Core;

namespace IntraDrop.UI;

internal enum ClipboardEcho { None, Applied, Sent }

internal sealed class ClipboardEchoTracker
{
    private uint _sequence, _owner;
    private bool _observed, _local;
    private ClipboardContent? _lastApplied, _lastSent;

    public void Observe(uint sequence, uint owner, bool foreground)
    {
        if (!_observed || sequence != _sequence || owner != _owner)
        {
            _sequence = sequence;
            _owner = owner;
            _observed = true;
            _local = false;
        }
        // Reading can be deferred until Excel loses focus; never forget who made the copy.
        _local |= foreground;
    }

    public ClipboardEcho Classify(ClipboardContent content, uint sequence, uint owner, bool background)
    {
        if (!background || (_observed && sequence == _sequence && owner == _owner && _local))
            return ClipboardEcho.None;
        if (content.ContentEquals(_lastApplied)) return ClipboardEcho.Applied;
        return content.ContentEquals(_lastSent) ? ClipboardEcho.Sent : ClipboardEcho.None;
    }

    public void Sent(ClipboardContent content)
    {
        _lastSent = content;
        _lastApplied = null; // An intervening local copy ends the previous received-content suppression.
    }

    public void Applied(ClipboardContent content)
    {
        Clear();
        _lastApplied = content;
    }

    public void Clear()
    {
        _observed = _local = false;
        _lastApplied = _lastSent = null;
    }
}
