using System.Buffers.Binary;
using System.Text.Json.Nodes;
using QuickShot.Core;

var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
CaptureGate Armed(uint sequence = 21)
{
    var gate = new CaptureGate(); gate.CaptureStarted(sequence - 1, now); gate.ClipboardChanged(sequence, true, true, now.AddSeconds(1)); return gate;
}
var idle = new CaptureGate();
Check(!idle.ClipboardChanged(10, true, true, now) && idle.Phase == CapturePhase.Idle, "ordinary image copy never arms");
idle.CaptureStarted(10, now);
Check(!idle.ClipboardChanged(10, true, true, now) && idle.Phase == CapturePhase.WaitingForImage, "old clipboard image never arms");
Check(!idle.ClipboardChanged(11, false, true, now), "text clipboard never arms");
Check(!idle.ClipboardChanged(12, true, false, now), "another app's image never arms");
Check(idle.ClipboardChanged(13, true, null, now.AddSeconds(1)), "unknown owner after capture supports delayed clipboard ownership");
Check(idle.Deadline == now.AddSeconds(11), "10 seconds starts when the image arrives");
var success = Armed();
Check(success.TryConsume(21, now.AddSeconds(2)) && success.Phase == CapturePhase.Idle, "first delivery consumes the capture");
Check(!success.TryConsume(21, now.AddSeconds(3)), "repeated keys cannot deliver twice");
var expired = Armed();
Check(!expired.TryConsume(21, now.AddSeconds(11)), "deadline is exclusive and restores ordinary typing");
var canceled = Armed(); canceled.Cancel();
Check(!canceled.TryConsume(21, now.AddSeconds(2)), "Escape cancels delivery");
var replaced = Armed(); replaced.ClipboardChanged(22, true, true, now.AddSeconds(2));
Check(replaced.Phase == CapturePhase.Idle && !replaced.TryConsume(22, now.AddSeconds(2)), "replacement image cancels instead of sending a new image");
var changedDuringKey = Armed();
Check(!changedDuringKey.TryConsume(22, now.AddSeconds(2)), "sequence is rechecked when a key is pressed");
var captureCanceled = new CaptureGate(); captureCanceled.CaptureStarted(10, now); captureCanceled.Cancel();
Check(!captureCanceled.ClipboardChanged(11, true, true, now.AddSeconds(2)), "canceled capture cannot arm later");
var waiting = new CaptureGate(); waiting.CaptureStarted(10, now); waiting.Expire(now.AddMinutes(2));
Check(waiting.Phase == CapturePhase.Idle, "unfinished capture eventually expires");
var restart = Armed(); restart.CaptureStarted(21, now.AddSeconds(2));
Check(!restart.TryConsume(21, now.AddSeconds(2)) && restart.ClipboardChanged(22, true, true, now.AddSeconds(3)), "new capture replaces old armed state");

foreach (var (text, expected) in new[] { (" c ", "C"), ("alt+control+c", "Ctrl+Alt+C"), ("shift+ctrl+F12", "Ctrl+Shift+F12"), ("Win+Left", "Win+Left"), ("1", "1") })
    Check(Shortcut.Parse(text).Text == expected, "shortcut canonicalization: " + expected);
foreach (var invalid in new[] { "", "Ctrl", "Ctrl+Ctrl+C", "C+D", "Ctrl+Alt+Shift+C", "Esc", "中" })
{
    try { Shortcut.Parse(invalid); throw new Exception("FAIL: invalid shortcut accepted: " + invalid); }
    catch (FormatException) { passed++; Console.WriteLine("PASS: invalid shortcut rejected: " + invalid); }
}
Shortcut[] shortcuts = [Shortcut.Parse("C"), Shortcut.Parse("Ctrl+Alt+D")];
Check(ShortcutMatcher.Evaluate(shortcuts, new(0x43, ShortcutModifiers.None, true)) == ShortcutDecision.Match, "single key matches");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA2, ShortcutModifiers.Ctrl, true)) == ShortcutDecision.Wait, "Ctrl prefix waits for a configured combo");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA4, ShortcutModifiers.Ctrl | ShortcutModifiers.Alt, true)) == ShortcutDecision.Wait, "second modifier prefix waits");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0x44, ShortcutModifiers.Ctrl | ShortcutModifiers.Alt, true)) == ShortcutDecision.Match, "three-key combo matches only its full chord");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0x44, ShortcutModifiers.Ctrl, true)) == ShortcutDecision.Cancel, "incomplete combo cancels immediately");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0x58, ShortcutModifiers.None, true)) == ShortcutDecision.Cancel, "unrelated typing cancels");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA0, ShortcutModifiers.Shift, true)) == ShortcutDecision.Cancel, "unrelated modifier cancels");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA2, ShortcutModifiers.None, false)) == ShortcutDecision.Cancel, "abandoning a modifier-only prefix cancels");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0xA4, ShortcutModifiers.Shift, false, HeldAtCapture: true)) == ShortcutDecision.Ignore, "releasing the screenshot chord does not cancel");
Check(ShortcutMatcher.Evaluate(shortcuts, new(0x43, ShortcutModifiers.None, true, IsRepeat: true, HeldAtCapture: true)) == ShortcutDecision.Ignore, "a key held before screenshot cannot trigger a repeat delivery");
var wrongKeyGate = Armed();
if (ShortcutMatcher.Evaluate(shortcuts, new(0x58, ShortcutModifiers.None, true)) == ShortcutDecision.Cancel) wrongKeyGate.Cancel();
Check(!wrongKeyGate.TryConsume(21, now.AddSeconds(2)), "a later correct key cannot revive a canceled screenshot");
var clickedGate = Armed(); clickedGate.Cancel();
Check(!clickedGate.TryConsume(21, now.AddSeconds(2)), "mouse cancellation prevents later shortcut delivery");

using var stream = new MemoryStream();
var message = new JsonObject { ["type"] = "hello", ["text"] = "中文 📸" };
await Protocol.WriteAsync(stream, message, CancellationToken.None);
Check(BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()) == stream.Length - 4, "native framing uses byte count, not character count");
stream.Position = 0;
var decoded = await Protocol.ReadAsync(stream, CancellationToken.None);
Check(decoded?["text"]?.GetValue<string>() == "中文 📸", "Unicode native-message round trip");
Check(await Protocol.ReadAsync(stream, CancellationToken.None) == null, "clean pipe close is recognized");
using var oversized = new MemoryStream();
var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, Protocol.MaxMessageBytes + 1); oversized.Write(header); oversized.Position = 0;
try { await Protocol.ReadAsync(oversized, CancellationToken.None); throw new Exception("FAIL: oversized inbound frame"); }
catch (InvalidDataException) { passed++; Console.WriteLine("PASS: oversized inbound frame rejected"); }
using var truncated = new MemoryStream([5, 0, 0, 0, 123]);
try { await Protocol.ReadAsync(truncated, CancellationToken.None); throw new Exception("FAIL: truncated inbound frame"); }
catch (EndOfStreamException) { passed++; Console.WriteLine("PASS: truncated inbound frame rejected"); }
try { await Protocol.WriteAsync(Stream.Null, new JsonObject { ["text"] = new string('x', Protocol.MaxMessageBytes) }, CancellationToken.None); throw new Exception("FAIL: oversized outbound frame"); }
catch (InvalidDataException) { passed++; Console.WriteLine("PASS: oversized outbound frame rejected"); }
Console.WriteLine($"{passed} checks passed.");
