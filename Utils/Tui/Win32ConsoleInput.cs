using System.Runtime.InteropServices;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Win32 console-input shim for frame-engine mouse support. On Windows, ConPTY translates the
/// terminal's VT mouse reports (enabled via <see cref="Ansi.MouseOn"/>) into MOUSE_EVENT input
/// records - which <c>Console.ReadKey</c> silently DISCARDS, so a pure VT/SGR parser never sees
/// them. This shim reads the console input queue directly (<c>ReadConsoleInputW</c>), surfacing
/// mouse records (button/wheel/drag + cell coords) alongside ordinary key records, and manages the
/// console modes mouse reporting needs: ENABLE_MOUSE_INPUT on, QuickEdit off (QuickEdit hijacks
/// clicks for text selection and swallows every mouse event). All P/Invoke is guarded so non-console
/// stdin (redirected/tests) degrades to "unavailable" and the caller falls back to Console.ReadKey.
/// </summary>
internal static class Win32ConsoleInput
{
    private const int STD_INPUT_HANDLE = -10;
    private const ushort KEY_EVENT = 0x0001;
    private const ushort MOUSE_EVENT_TYPE = 0x0002;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint MOUSE_MOVED = 0x0001;
    private const uint MOUSE_WHEELED = 0x0004;
    private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        // Union starts at offset 4 (2 bytes padding after the ushort).
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PeekConsoleInputW(nint hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleInputW(nint hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(nint hConsoleInput, out uint lpcNumberOfEvents);

    private static nint _hIn;
    private static uint _savedMode;
    private static bool _active;

    /// <summary>True after a successful <see cref="EnableMouse"/> - the read loop may use
    /// <see cref="TryReadEvent"/>. False on non-Windows, redirected stdin, or mode failure.</summary>
    public static bool Active => _active;

    /// <summary>Turn on mouse input records: save the input mode, then set ENABLE_MOUSE_INPUT and
    /// clear QuickEdit (via ENABLE_EXTENDED_FLAGS). Returns false (inactive) on any failure.</summary>
    public static bool EnableMouse()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            _hIn = GetStdHandle(STD_INPUT_HANDLE);
            if (_hIn == nint.Zero || _hIn == -1) return false;
            if (!GetConsoleMode(_hIn, out _savedMode)) return false;
            uint mode = (_savedMode | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE;
            if (!SetConsoleMode(_hIn, mode)) return false;
            _active = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Restore the saved console mode. Idempotent, never throws.</summary>
    public static void DisableMouse()
    {
        if (!_active) return;
        _active = false;
        try { SetConsoleMode(_hIn, _savedMode); } catch { /* ignore */ }
    }

    /// <summary>
    /// One event from the console input queue, translated: <c>IsMouse</c> carries SGR-style button
    /// codes (0 = left, 32 = drag-motion with left held, 64/65 = wheel up/down) + 1-based cell
    /// coords, mirroring the VT path so the caller's dispatch is shared. <c>HasKey</c> carries an
    /// ordinary keydown. Neither flag = an event was consumed that needs no action (focus, resize,
    /// key-up, uninteresting button) - caller just loops.
    /// </summary>
    public readonly record struct InputEvent(bool IsMouse, int Button, int X, int Y, bool Release, bool HasKey, ConsoleKeyInfo Key);

    /// <summary>Non-blocking: false when the queue is empty. When true, exactly one record was
    /// consumed and translated into <paramref name="ev"/>.</summary>
    public static bool TryReadEvent(out InputEvent ev)
    {
        ev = default;
        if (!_active) return false;
        try
        {
            if (!GetNumberOfConsoleInputEvents(_hIn, out uint pending) || pending == 0) return false;
            var buf = new INPUT_RECORD[1];
            // Peek first: a pending KEY_EVENT keydown is consumed through Console.ReadKey so .NET's
            // own decoder handles surrogates/alt-numpad exactly as the rest of the editor expects.
            if (!PeekConsoleInputW(_hIn, buf, 1, out uint got) || got == 0) return false;
            if (buf[0].EventType == KEY_EVENT && buf[0].KeyEvent.bKeyDown != 0)
            {
                var ki = Console.ReadKey(intercept: true);
                ev = new InputEvent(false, 0, 0, 0, false, true, ki);
                return true;
            }
            // Everything else (mouse, key-up, focus, resize records) we consume directly.
            if (!ReadConsoleInputW(_hIn, buf, 1, out got) || got == 0) return false;
            if (buf[0].EventType != MOUSE_EVENT_TYPE) return true;   // consumed, nothing to do

            var m = buf[0].MouseEvent;
            int x = m.dwMousePosition.X + 1, y = m.dwMousePosition.Y + 1;   // 1-based cells
            if ((m.dwEventFlags & MOUSE_WHEELED) != 0)
            {
                short delta = unchecked((short)(m.dwButtonState >> 16));
                ev = new InputEvent(true, delta > 0 ? 64 : 65, x, y, false, false, default);
                return true;
            }
            bool leftDown = (m.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
            if ((m.dwEventFlags & MOUSE_MOVED) != 0)
            {
                if (!leftDown) return true;   // hover - consumed, no action
                ev = new InputEvent(true, 32, x, y, false, false, default);   // drag with left held
                return true;
            }
            // Plain button transition: left press or left release. Other buttons: consumed, no action.
            ev = new InputEvent(true, 0, x, y, !leftDown, false, default);
            return true;
        }
        catch { return false; }
    }
}
