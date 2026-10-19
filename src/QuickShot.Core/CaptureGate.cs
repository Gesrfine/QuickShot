namespace QuickShot.Core;

public enum CapturePhase { Idle, WaitingForImage, Armed }

/// <summary>Pure state machine: only a changed image after a capture shortcut can arm delivery.</summary>
public sealed class CaptureGate
{
    public static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(10);
    public CapturePhase Phase { get; private set; }
    public uint ImageSequence { get; private set; }
    public DateTimeOffset Deadline { get; private set; }

    public void CaptureStarted(uint clipboardSequence, DateTimeOffset now)
    {
        ImageSequence = clipboardSequence;
        Phase = CapturePhase.WaitingForImage;
        Deadline = now.AddMinutes(2);
    }

    public bool ClipboardChanged(uint sequence, bool containsImage, bool? fromFastStone, DateTimeOffset now)
    {
        Expire(now);
        if (Phase == CapturePhase.Armed && sequence != ImageSequence) Cancel();
        if (Phase != CapturePhase.WaitingForImage || sequence == ImageSequence || !containsImage || fromFastStone == false)
            return false;
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
        if (Phase != CapturePhase.Idle && now >= Deadline) Cancel();
    }

    public void Cancel() => Phase = CapturePhase.Idle;
}

