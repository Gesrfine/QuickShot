namespace QuickShot.Core;

[Flags]
public enum ShortcutModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

public sealed record Shortcut(ShortcutModifiers Modifiers, uint VirtualKey, string KeyName)
{
    public string Text => string.Join("+", ModifierNames.Where(pair => Modifiers.HasFlag(pair.Value)).Select(pair => pair.Key).Append(KeyName));
    private static readonly KeyValuePair<string, ShortcutModifiers>[] ModifierNames =
    [new("Ctrl", ShortcutModifiers.Ctrl), new("Alt", ShortcutModifiers.Alt), new("Shift", ShortcutModifiers.Shift), new("Win", ShortcutModifiers.Win)];
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["PrintScreen"] = 0x2C,
        ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26,
        ["Right"] = 0x27, ["Down"] = 0x28
    };

    public static Shortcut Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("请设置一个快捷键。");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length > 3 || parts.Any(string.IsNullOrEmpty)) throw new FormatException("快捷键最多包含三个同时按下的键。");
        var modifiers = ShortcutModifiers.None;
        string? primary = null; uint key = 0;
        foreach (var part in parts)
        {
            var normalized = part.ToUpperInvariant();
            var modifier = normalized switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Ctrl, "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift, "WIN" or "META" => ShortcutModifiers.Win, _ => ShortcutModifiers.None
            };
            if (modifier != ShortcutModifiers.None)
            {
                if (modifiers.HasFlag(modifier)) throw new FormatException("快捷键中不能重复同一个修饰键。");
                modifiers |= modifier; continue;
            }
            if (primary != null) throw new FormatException("请使用修饰键加一个字母、数字或功能键，例如 Ctrl+Alt+C。");
            if (normalized.Length == 1 && (normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            { primary = normalized; key = normalized[0]; }
            else if (normalized.StartsWith('F') && int.TryParse(normalized.AsSpan(1), out var number) && number is >= 1 and <= 12)
            { primary = "F" + number; key = (uint)(0x70 + number - 1); }
            else if (NamedKeys.TryGetValue(part, out key)) primary = NamedKeys.Keys.First(name => name.Equals(part, StringComparison.OrdinalIgnoreCase));
            else throw new FormatException("支持字母、数字、F1–F12、方向键等；Esc 始终用于取消。");
        }
        if (primary == null) throw new FormatException("快捷键需要包含一个字母、数字或功能键。");
        return new Shortcut(modifiers, key, primary);
    }

    public bool Matches(uint virtualKey, ShortcutModifiers modifiers) => VirtualKey == virtualKey && Modifiers == modifiers;
    public bool AllowsModifierPrefix(ShortcutModifiers modifiers) => (modifiers & ~Modifiers) == ShortcutModifiers.None;
    public static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    public static ShortcutModifiers ModifierFor(uint key) => key switch
    {
        0x11 or 0xA2 or 0xA3 => ShortcutModifiers.Ctrl, 0x12 or 0xA4 or 0xA5 => ShortcutModifiers.Alt,
        0x10 or 0xA0 or 0xA1 => ShortcutModifiers.Shift, 0x5B or 0x5C => ShortcutModifiers.Win, _ => ShortcutModifiers.None
    };
}

public readonly record struct KeyStroke(uint VirtualKey, ShortcutModifiers Modifiers, bool IsDown, bool IsRepeat = false, bool HeldAtCapture = false);
public enum ShortcutDecision { Ignore, Wait, Match, Cancel }

public static class ShortcutMatcher
{
    public static ShortcutDecision Evaluate(IReadOnlyCollection<Shortcut> targets, KeyStroke stroke)
    {
        if (stroke.HeldAtCapture && !stroke.IsDown) return ShortcutDecision.Ignore;
        if (stroke.IsRepeat) return ShortcutDecision.Ignore;
        if (!stroke.IsDown) return Shortcut.IsModifier(stroke.VirtualKey) ? ShortcutDecision.Cancel : ShortcutDecision.Ignore;
        if (Shortcut.IsModifier(stroke.VirtualKey))
            return targets.Any(target => target.AllowsModifierPrefix(stroke.Modifiers)) ? ShortcutDecision.Wait : ShortcutDecision.Cancel;
        return targets.Any(target => target.Matches(stroke.VirtualKey, stroke.Modifiers)) ? ShortcutDecision.Match : ShortcutDecision.Cancel;
    }
}
