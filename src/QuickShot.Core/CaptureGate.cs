namespace QuickShot.Core;

public enum CapturePhase { Idle, Armed }
public sealed class CaptureGate
{
    public static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(10);
    public CapturePhase Phase { get; private set; }
    public uint ImageSequence { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    private uint _lastSequence;

    public void Initialize(uint sequence)
    {
        _lastSequence = sequence;
        Cancel();
    }

    public bool ClipboardChanged(uint sequence, bool containsImage, DateTimeOffset now)
    {
        Expire(now);
        if (sequence == _lastSequence) return false;
        _lastSequence = sequence;
        Cancel();
        if (!containsImage) return false;
        ImageSequence = sequence;
        Phase = CapturePhase.Armed;
        Deadline = now.Add(ArmedDuration);
        return true;
    }

    public bool TryConsume(uint currentSequence, DateTimeOffset now)
    {
        Expire(now);
        if (Phase != CapturePhase.Armed) return false;
        var valid = currentSequence == ImageSequence;
        Cancel();
        return valid;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Phase == CapturePhase.Armed && now >= Deadline) Cancel();
    }

    public void Cancel() => Phase = CapturePhase.Idle;
}

