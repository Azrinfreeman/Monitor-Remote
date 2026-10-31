using System.Runtime.InteropServices;

sealed class NativeInput {
    readonly List<Input> held = [];
    readonly bool demo = DesktopDisplayCatalog.Demo;
    int demoX = 640, demoY = 360;
    public int HeldCount => held.Count;
    public long Operations { get; private set; }
    static readonly Dictionary<string, ushort> Keys = new(StringComparer.Ordinal) {
        ["Backspace"] = 8, ["Tab"] = 9, ["Enter"] = 13, ["Escape"] = 27, ["Space"] = 32,
        ["PageUp"] = 33, ["PageDown"] = 34, ["End"] = 35, ["Home"] = 36, ["ArrowLeft"] = 37,
        ["ArrowUp"] = 38, ["ArrowRight"] = 39, ["ArrowDown"] = 40, ["Delete"] = 46, ["Insert"] = 45,
        ["F1"] = 112, ["F2"] = 113, ["F3"] = 114, ["F4"] = 115, ["F5"] = 116, ["F6"] = 117, ["F7"] = 118, ["F8"] = 119, ["F9"] = 120, ["F10"] = 121, ["F11"] = 122, ["F12"] = 123,
        ["Ctrl"] = 17, ["Shift"] = 16, ["Alt"] = 18
    };
    public static (int X, int Y) Map(DesktopDisplay d, double x, double y) {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new InvalidOperationException("Pointer coordinates must be within the screen.");
        return (d.X + (int)Math.Round(x * (d.Width - 1)), d.Y + (int)Math.Round(y * (d.Height - 1)));
    }
    public void Apply(ControlMessage message, DesktopDisplay display, DesktopDisplay[] displays) {
        if (!DesktopDisplayCatalog.Interactive()) throw new InvalidOperationException("Windows is locked or its normal desktop is unavailable.");
        switch (message.Type) {
            case "move": var point = Map(display, message.X, message.Y); Move(point.X, point.Y, displays); break;
            case "delta":
                if (!double.IsFinite(message.Dx) || !double.IsFinite(message.Dy) || Math.Abs(message.Dx) > 2000 || Math.Abs(message.Dy) > 2000) throw new InvalidOperationException("Invalid pointer movement.");
                var cursor = new Point { X = demoX, Y = demoY };
                if (!demo && !GetCursorPos(out cursor)) throw new InvalidOperationException("Windows cursor unavailable.");
                Move(Math.Clamp(cursor.X + (int)Math.Round(message.Dx), display.X, display.X + display.Width - 1), Math.Clamp(cursor.Y + (int)Math.Round(message.Dy), display.Y, display.Y + display.Height - 1), displays);
                break;
            case "click":
                var flags = ButtonFlags(message.Button);
                if (held.Count != 0) throw new InvalidOperationException("Finish dragging before clicking.");
                var clicks = new List<Input> { Mouse(flags.Down), Mouse(flags.Up) };
                if (message.Double) { clicks.Add(Mouse(flags.Down)); clicks.Add(Mouse(flags.Up)); }
                Send(clicks.ToArray()); break;
            case "button":
                var button = ButtonFlags(message.Button);
                if (message.Down && held.Count != 0) throw new InvalidOperationException("A button is already held.");
                if (!message.Down && !held.Any(h => h.Type == 0 && h.Data.Mouse.Flags == button.Up)) break;
                Send([Mouse(message.Down ? button.Down : button.Up)]); break;
            case "wheel":
                if (!double.IsFinite(message.Dx) || !double.IsFinite(message.Dy) || Math.Abs(message.Dx) > 10 || Math.Abs(message.Dy) > 10) throw new InvalidOperationException("Invalid scroll movement.");
                var wheels = new List<Input>();
                if (message.Dy != 0) wheels.Add(Mouse(0x800, unchecked((uint)(int)Math.Round(message.Dy * 120))));
                if (message.Dx != 0) wheels.Add(Mouse(0x1000, unchecked((uint)(int)Math.Round(message.Dx * 120))));
                Send(wheels.ToArray()); break;
            case "key":
                var key = Resolve(message.Key);
                var modifiers = message.Modifiers ?? [];
                if (modifiers.Length > 3 || modifiers.Distinct().Count() != modifiers.Length || modifiers.Any(m => m is not ("Ctrl" or "Shift" or "Alt"))) throw new InvalidOperationException("Invalid keyboard modifier.");
                if (message.Key == "Delete" && modifiers.Contains("Ctrl") && modifiers.Contains("Alt")) throw new InvalidOperationException("Ctrl+Alt+Delete is unavailable.");
                var events = modifiers.Select(m => Keyboard(Resolve(m))).ToList();
                events.Add(Keyboard(key)); events.Add(Keyboard(key, true));
                events.AddRange(modifiers.Reverse().Select(m => Keyboard(Resolve(m), true))); Send(events.ToArray()); break;
            case "text":
                if (message.Text == null || message.Text.Length > 1024 || !ValidText(message.Text)) throw new InvalidOperationException("Text must contain valid Unicode and be at most 1024 characters.");
                var textEvents = new List<Input>();
                foreach (var character in message.Text.Replace("\r\n", "\n").Replace('\r', '\n')) {
                    if (character is '\n' or '\t') { var code = character == '\n' ? (ushort)13 : (ushort)9; textEvents.Add(Keyboard(code)); textEvents.Add(Keyboard(code, true)); }
                    else { textEvents.Add(Unicode(character)); textEvents.Add(Unicode(character, true)); }
                }
                Send(textEvents.ToArray()); break;
            case "release": ReleaseAll(); break;
            default: throw new InvalidOperationException("Unknown input operation.");
        }
        Operations++;
    }
    static bool ValidText(string text) {
        for (var i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i]) || char.IsControl(text[i]) && text[i] is not ('\n' or '\r' or '\t')) return false;
        }
        return true;
    }
    static ushort Resolve(string? key) {
        if (key != null && Keys.TryGetValue(key, out var code)) return code;
        if (key is { Length: 1 } && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        throw new InvalidOperationException("Unsupported key.");
    }
    static (uint Down, uint Up) ButtonFlags(string? button) => button switch { "left" => (2, 4), "right" => (8, 16), _ => throw new InvalidOperationException("Use left or right mouse button.") };
    void Move(int x, int y, DesktopDisplay[] displays) {
        var left = displays.Min(d => d.X); var top = displays.Min(d => d.Y);
        var width = displays.Max(d => d.X + d.Width) - left; var height = displays.Max(d => d.Y + d.Height) - top;
        var input = Mouse(0x8000 | 0x4000 | 1);
        input.Data.Mouse.X = (int)Math.Round((double)(x - left) * 65535 / Math.Max(1, width - 1));
        input.Data.Mouse.Y = (int)Math.Round((double)(y - top) * 65535 / Math.Max(1, height - 1));
        Send([input]); demoX = x; demoY = y;
    }
    public void ReleaseAll() {
        if (held.Count == 0) return;
        try { Send(held.ToArray()); } catch (InvalidOperationException) { /* Retry cleanup on the next lifecycle tick. */ }
    }
    void Send(Input[] events) {
        if (events.Length == 0) return;
        var count = demo ? (uint)events.Length : SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        for (var i = 0; i < count; i++) {
            var input = events[i];
            if (input.Type == 0) {
                var up = input.Data.Mouse.Flags switch { 2u => 4u, 8u => 16u, _ => 0u };
                if (up != 0) { var release = Mouse(up); if (!held.Contains(release)) held.Add(release); }
                else if (input.Data.Mouse.Flags is 4 or 16) held.RemoveAll(h => h.Type == 0 && h.Data.Mouse.Flags == input.Data.Mouse.Flags);
            } else {
                var isUp = (input.Data.Key.Flags & 2) != 0;
                if (!isUp) { input.Data.Key.Flags |= 2; held.Add(input); }
                else held.RemoveAll(h => h.Type == 1 && h.Data.Key.Code == input.Data.Key.Code && h.Data.Key.Scan == input.Data.Key.Scan);
            }
        }
        if (count != events.Length) throw new InvalidOperationException("Windows accepted only part of the input. Check the foreground app; elevated apps may reject control.");
    }
    static Input Mouse(uint flags, uint data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, MouseData = data } } };
    static Input Keyboard(ushort key, bool up = false) => new() { Type = 1, Data = new() { Key = new() { Code = key, Flags = (up ? 2u : 0) | (key is >= 33 and <= 46 ? 1u : 0) } } };
    static Input Unicode(char text, bool up = false) => new() { Type = 1, Data = new() { Key = new() { Scan = text, Flags = 4 | (up ? 2u : 0) } } };
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort Code, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
}
