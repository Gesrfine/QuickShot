using System.Text.Json.Nodes;
using QuickShot.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++; Console.WriteLine("PASS: " + name);
    }

    private static async Task Main()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new CaptureGate(); gate.Initialize(100);
        Check(!gate.ClipboardChanged(100, true, now), "startup ignores an existing image");
        Check(gate.ClipboardChanged(101, true, now), "a new image arms without a capture tool or hotkey");
        Check(gate.Deadline == now.AddSeconds(10), "eligibility lasts ten seconds from the new image");
        Check(!gate.ClipboardChanged(101, true, now.AddSeconds(5)) && gate.Deadline == now.AddSeconds(10), "duplicate notifications never extend eligibility");
        Check(gate.TryConsume(101, now.AddSeconds(9)), "correct shortcut can consume the new image");
        Check(!gate.TryConsume(101, now.AddSeconds(9)), "a single copy cannot deliver twice");
        Check(!gate.ClipboardChanged(101, true, now.AddSeconds(9)), "notifications after delivery cannot revive the image");
        Check(!gate.ClipboardChanged(102, false, now), "copying text or a file does not arm");
        Check(gate.ClipboardChanged(103, true, now), "another application's new image can arm");
        Check(!gate.TryConsume(104, now), "clipboard replacement is rechecked when committing a shortcut");
        Check(gate.Phase == CapturePhase.Idle, "failed commit clears eligibility");
        Check(gate.ClipboardChanged(105, true, now), "another new image can arm after a failed commit");
        gate.Cancel();
        Check(!gate.ClipboardChanged(105, true, now), "Escape or other input cannot be undone by a duplicate notification");
        gate.ClipboardChanged(106, true, now);
        Check(!gate.TryConsume(106, now.AddSeconds(10)), "the exact ten-second deadline is excluded");
        gate.ClipboardChanged(107, true, now);
        gate.ClipboardChanged(108, false, now);
        Check(gate.Phase == CapturePhase.Idle, "text copied over an armed image cancels it");
        gate.ClipboardChanged(109, true, now);
        gate.ClipboardChanged(110, true, now.AddSeconds(1));
        Check(gate.ImageSequence == 110 && gate.Deadline == now.AddSeconds(11), "a new image replaces the previous image's eligibility");
        gate.Initialize(uint.MaxValue);
        Check(gate.ClipboardChanged(0, true, now), "clipboard sequence wraparound still detects a new image");
        gate.Initialize(20);
        Check(!gate.ClipboardChanged(20, true, now), "resume establishes a fresh baseline instead of arming stale content");
        foreach (var text in new[] { "C", "Ctrl+Alt+C", "Alt+Shift+D", "Ctrl+Shift+F12", "PrintScreen", "Win+Left" })
            Check(Shortcut.Parse(text).Text == text, "shortcut canonicalization: " + text);
        foreach (var text in new[] { "", "Ctrl", "Ctrl+Ctrl+C", "C+D", "Ctrl+Alt+Shift+C", "Esc", "中" })
        {
            try { Shortcut.Parse(text); throw new InvalidOperationException("accepted invalid shortcut: " + text); }
            catch (FormatException) { Check(true, "invalid shortcut rejected: " + text); }
        }
        Shortcut[] shortcuts = [Shortcut.Parse("C"), Shortcut.Parse("Ctrl+Alt+D")];
        Check(ShortcutMatcher.Evaluate(shortcuts, new('C', ShortcutModifiers.None, true)) == ShortcutDecision.Match, "single key matches");
        Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA2, ShortcutModifiers.Ctrl, true)) == ShortcutDecision.Wait, "a valid modifier prefix waits");
        Check(ShortcutMatcher.Evaluate(shortcuts, new('D', ShortcutModifiers.Ctrl | ShortcutModifiers.Alt, true)) == ShortcutDecision.Match, "three-key combination matches");
        Check(ShortcutMatcher.Evaluate(shortcuts, new('A', ShortcutModifiers.None, true)) == ShortcutDecision.Cancel, "ordinary typing cancels before delivery");
        Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA0, ShortcutModifiers.Shift, true)) == ShortcutDecision.Cancel, "unrelated modifiers cancel");
        Check(ShortcutMatcher.Evaluate(shortcuts, new('C', ShortcutModifiers.None, true, true, true)) == ShortcutDecision.Ignore, "holding the copy shortcut cannot repeat-deliver");
        Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA2, ShortcutModifiers.None, false, false, true)) == ShortcutDecision.Ignore, "releasing Ctrl after copying does not cancel");
        Check(ShortcutMatcher.Evaluate(shortcuts, new('C', ShortcutModifiers.None, false, false, true)) == ShortcutDecision.Ignore, "releasing the copy key does not cancel");
        Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA2, ShortcutModifiers.None, false)) == ShortcutDecision.Cancel, "abandoning a new modifier prefix cancels");
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new JsonObject { ["type"] = "hello", ["version"] = Protocol.Version, ["message"] = "剪切板图片" }, CancellationToken.None);
        stream.Position = 0;
        var message = await Protocol.ReadAsync(stream, CancellationToken.None);
        Check(message?["message"]?.GetValue<string>() == "剪切板图片", "Unicode native frames round-trip");
        Check(await Protocol.ReadAsync(stream, CancellationToken.None) == null, "clean native stream close is recognized");
        using var invalid = new MemoryStream(BitConverter.GetBytes(Protocol.MaxMessageBytes + 1));
        try { await Protocol.ReadAsync(invalid, CancellationToken.None); throw new InvalidOperationException("accepted oversized frame"); }
        catch (InvalidDataException) { Check(true, "oversized inbound frames are rejected"); }
        using var truncated = new MemoryStream([8, 0, 0, 0, 1]);
        try { await Protocol.ReadAsync(truncated, CancellationToken.None); throw new InvalidOperationException("accepted truncated frame"); }
        catch (EndOfStreamException) { Check(true, "truncated frames are rejected"); }
        Check(Protocol.Version == 3, "2.0 rejects the legacy protocol");
        Console.WriteLine($"Core checks passed: {_checks}");
    }
}
