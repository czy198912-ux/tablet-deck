// Reads commands from stdin, one per line, and injects keyboard scancodes with SendInput.
//   d <scancode-hex> <extended 0|1>   key down
//   u <scancode-hex> <extended 0|1>   key up
//   focus <process-name>              replies "focus 1" if that process owns the
//                                     foreground window, else "focus 0"
//   ping                              replies "pong"
// Scancodes (not virtual keys) are used because DirectInput/raw-input games such as
// Star Citizen ignore virtual-key-only events.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class KeySender
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is the largest member of the INPUT union; it sizes the struct correctly on x64.
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Name of the process that owns the window in front, without ".exe"; "" if unknown.
    static string ForegroundProcess()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid == 0) return "";
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    static void Main()
    {
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length == 1 && parts[0] == "ping")
            {
                Console.WriteLine("pong");
                Console.Out.Flush();
                continue;
            }
            if (parts.Length == 2 && parts[0] == "focus")
            {
                bool focused = string.Equals(ForegroundProcess(), parts[1], StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(focused ? "focus 1" : "focus 0");
                Console.Out.Flush();
                continue;
            }
            if (parts.Length != 3 || (parts[0] != "d" && parts[0] != "u"))
            {
                Console.WriteLine("err bad-command");
                Console.Out.Flush();
                continue;
            }
            ushort scan = Convert.ToUInt16(parts[1], 16);
            uint flags = KEYEVENTF_SCANCODE;
            if (parts[2] == "1") flags |= KEYEVENTF_EXTENDEDKEY;
            if (parts[0] == "u") flags |= KEYEVENTF_KEYUP;

            var input = new INPUT[1];
            input[0].type = INPUT_KEYBOARD;
            input[0].u.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero };
            uint sent = SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
            Console.WriteLine(sent == 1 ? "ok" : "err " + Marshal.GetLastWin32Error());
            Console.Out.Flush();
        }
    }
}
