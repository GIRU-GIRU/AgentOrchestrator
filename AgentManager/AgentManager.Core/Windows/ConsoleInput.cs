using System.Runtime.InteropServices;

namespace AgentManager.Core.Windows;

/// <summary>What one console shows: its visible rows with trailing blanks removed, and where its caret is among them.</summary>
public sealed record ConsoleScreen(IReadOnlyList<string> Rows, int CursorColumn, int CursorRow);

/// <summary>One agent's console, attached for the length of a call to <see cref="ConsoleInput.Attached{T}"/>.</summary>
public interface IAgentConsole
{
    ConsoleScreen? ReadScreen();

    bool Type(string text);

    bool PressEnter();

    /// <summary>Ctrl+S: Claude Code's prompt stash, which sets the box's text aside and puts it back when the box is empty.</summary>
    bool PressStash();

    bool Erase(int characters);
}

/// <summary>
/// Types a line into one agent's own console input, as if the user typed it in that window: the agent reads it as the
/// user's prompt (a working Claude queues it as a message while it works). It never goes through the desktop or the
/// focused window, so it cannot land anywhere else, as synthetic desktop input could. A dialog open in the agent would take the keys as its
/// answer, so callers skip agents waiting on one (<see cref="Delivery"/>).
/// </summary>
public static class ConsoleInput
{
    private const ushort KEY_EVENT = 1;

    private const ushort VK_BACK = 0x08;

    private const ushort VK_RETURN = 0x0D;

    private const ushort VK_S = 0x53;

    private const char CTRL_S = '\u0013';

    private const uint LEFT_CTRL_PRESSED = 0x0008;

    private const uint GENERIC_READ_WRITE = 0xC0000000;

    private const uint FILE_SHARE_READ_WRITE = 3;

    private const uint OPEN_EXISTING = 3;

    private static readonly IntPtr INVALID_HANDLE = new(-1);

    /// <summary>Enter goes separately, after the text has been read, so a fast batch is not taken as a paste ending in a newline.</summary>
    private static readonly TimeSpan ENTER_DELAY = TimeSpan.FromMilliseconds(300);

    /// <summary>A process has one console at a time, so everything that attaches goes through this: the hub's thread and the UI's would detach each other.</summary>
    internal static readonly Lock GATE = new();

    public static (bool IsSent, string How) Type(int pid, string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0)
        {
            return (false, "nothing to type");
        }

        return Attached<(bool IsSent, string How)>(pid, console =>
        {
            if (!console.Type(line))
            {
                return (false, $"typing failed (error {Marshal.GetLastWin32Error()})");
            }

            Thread.Sleep(ENTER_DELAY);
            return console.PressEnter()
                ? (true, $"typed into the console of pid {pid}")
                : (false, $"Enter failed (error {Marshal.GetLastWin32Error()})");
        }, why => (false, why));
    }

    /// <summary>Runs <paramref name="work"/> attached to the agent's console, or <paramref name="failed"/> with the reason it could not attach.</summary>
    public static T Attached<T>(int pid, Func<IAgentConsole, T> work, Func<string, T> failed)
    {
        lock (GATE)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid))
            {
                return failed($"cannot attach to the console of pid {pid} (error {Marshal.GetLastWin32Error()})");
            }

            try
            {
                var input = CreateFileW("CONIN$", GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (input == INVALID_HANDLE)
                {
                    return failed($"cannot open the console input of pid {pid} (error {Marshal.GetLastWin32Error()})");
                }

                var output = CreateFileW("CONOUT$", GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                try
                {
                    return work(new AttachedConsole(input, output));
                }
                finally
                {
                    CloseHandle(input);
                    if (output != INVALID_HANDLE)
                    {
                        CloseHandle(output);
                    }
                }
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private sealed class AttachedConsole(IntPtr input, IntPtr output) : IAgentConsole
    {
        public ConsoleScreen? ReadScreen()
        {
            if (output == INVALID_HANDLE || !GetConsoleScreenBufferInfo(output, out var info))
            {
                return null;
            }

            var window = info.srWindow;
            var width = window.Right - window.Left + 1;
            var rows = new List<string>();
            var cells = new char[width];
            for (var y = window.Top; y <= window.Bottom; y++)
            {
                if (!ReadConsoleOutputCharacterW(output, cells, (uint)width, new COORD { X = window.Left, Y = y }, out var read))
                {
                    return null;
                }

                rows.Add(new string(cells, 0, (int)read).TrimEnd());
            }

            return new ConsoleScreen(rows, info.dwCursorPosition.X - window.Left, info.dwCursorPosition.Y - window.Top);
        }

        public bool Type(string text)
        {
            return Write(text.SelectMany(c => Press(c, 0)).ToArray());
        }

        public bool PressEnter()
        {
            return Write(Press('\r', VK_RETURN));
        }

        public bool PressStash()
        {
            return Write(Press(CTRL_S, VK_S, LEFT_CTRL_PRESSED));
        }

        public bool Erase(int characters)
        {
            return Write(Enumerable.Repeat(Press('\b', VK_BACK), characters).SelectMany(keys => keys).ToArray());
        }

        private bool Write(INPUT_RECORD[] keys)
        {
            return WriteConsoleInputW(input, keys, (uint)keys.Length, out _);
        }

        private static INPUT_RECORD[] Press(char character, ushort virtualKey, uint controlKeys = 0)
        {
            return [Key(true, character, virtualKey, controlKeys), Key(false, character, virtualKey, controlKeys)];
        }
    }

    private static INPUT_RECORD Key(bool isDown, char character, ushort virtualKey, uint controlKeys)
    {
        var record = new INPUT_RECORD { EventType = KEY_EVENT };
        record.KeyEvent.bKeyDown = isDown ? 1 : 0;
        record.KeyEvent.wRepeatCount = 1;
        record.KeyEvent.wVirtualKeyCode = virtualKey;
        record.KeyEvent.UnicodeChar = character;
        record.KeyEvent.dwControlKeyState = controlKeys;
        return record;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [FieldOffset(0)]
        public int bKeyDown;

        [FieldOffset(4)]
        public ushort wRepeatCount;

        [FieldOffset(6)]
        public ushort wVirtualKeyCode;

        [FieldOffset(8)]
        public ushort wVirtualScanCode;

        [FieldOffset(10)]
        public char UnicodeChar;

        [FieldOffset(12)]
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;

        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;

        public short Top;

        public short Right;

        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;

        public COORD dwCursorPosition;

        public ushort wAttributes;

        public SMALL_RECT srWindow;

        public COORD dwMaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInputW(IntPtr input, INPUT_RECORD[] records, uint count, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr output, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr output, [Out] char[] characters, uint length, COORD start, out uint read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
