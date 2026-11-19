using System.Runtime.InteropServices;

namespace PodsController.Interop;

/// <summary>
/// 本程序用到的 Win32 / Winsock / 蓝牙 P/Invoke。
///
/// Python 版用 ctypes 调同名 API，这里逐条对齐（含 AF_BTH / RFCOMM 通道号方案），
/// 不额外引入 WinRT 依赖，保证「通道 1 / 通道 15」这种按型号固定的连接方式还能用。
/// </summary>
internal static class Native
{
    // ------------------------------------------------------------------ //
    // 常量
    // ------------------------------------------------------------------ //
    public const int AF_BTH = 32;
    public const int SOCK_STREAM = 1;
    public const int BTHPROTO_RFCOMM = 3;
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_RCVTIMEO = 0x1006;
    public const int SO_SNDTIMEO = 0x1005;
    public const int SOCKET_ERROR = -1;
    public const int WSAETIMEDOUT = 10060;
    public const int WSAEWOULDBLOCK = 10035;

    public const uint WM_APP = 0x8000;
    public const uint WM_TRAYICON = WM_APP + 1;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;

    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int GWLP_WNDPROC = -4;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_POPUP = 0x80000000;

    public const uint SW_HIDE = 0;
    public const uint SW_SHOW = 5;
    public const uint SW_SHOWNOACTIVATE = 4;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_A = 0x41;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_V = 0x56;

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_SHOWTIP = 0x00000080;
    public const uint NIIF_INFO = 0x00000001;

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    public const uint MB_ICONERROR = 0x00000010;

    public const int NOTIFY_FOR_THIS_SESSION = 0;
    public const int WTS_CONSOLE_CONNECT = 1;
    public const int WTS_SESSION_UNLOCK = 8;

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ------------------------------------------------------------------ //
    // 结构
    // ------------------------------------------------------------------ //
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SockAddrBth
    {
        public ushort AddressFamily;
        public ulong BtAddr;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint cbSize;
        public uint Style;
        public IntPtr WndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BluetoothAddress
    {
        public ulong Long;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BluetoothDeviceInfo
    {
        public uint Size;
        public BluetoothAddress Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BluetoothDeviceSearchParams
    {
        public uint Size;
        public int ReturnAuthenticated;
        public int ReturnRemembered;
        public int ReturnUnknown;
        public int ReturnConnected;
        public int IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // ------------------------------------------------------------------ //
    // ws2_32
    // ------------------------------------------------------------------ //
    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAStartup(ushort version, IntPtr data);

    [DllImport("ws2_32.dll")]
    public static extern int WSAGetLastError();

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int connect(IntPtr s, ref SockAddrBth name, int nameLen);

    [DllImport("ws2_32.dll")]
    public static extern int send(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll")]
    public static extern int recv(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll")]
    public static extern int closesocket(IntPtr s);

    [DllImport("ws2_32.dll")]
    public static extern int setsockopt(IntPtr s, int level, int optName, ref uint optVal, int optLen);

    // ------------------------------------------------------------------ //
    // user32
    // ------------------------------------------------------------------ //
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public delegate IntPtr KeyboardHookDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookDelegate callback, IntPtr instance, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_UNCHECKED = 0x00000000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hWnd;
        public uint Message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    // ------------------------------------------------------------------ //
    // shell32
    // ------------------------------------------------------------------ //
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    // ------------------------------------------------------------------ //
    // bthprops.cpl（已配对的蓝牙设备表，Python 版用同一套 API）
    // ------------------------------------------------------------------ //
    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams search, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl")]
    public static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    public static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo info);

    // ------------------------------------------------------------------ //
    // wtsapi32
    // ------------------------------------------------------------------ //
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

    [DllImport("wtsapi32.dll")]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ------------------------------------------------------------------ //
    // 小工具
    // ------------------------------------------------------------------ //
    public static string BluetoothAddressToMac(ulong address)
    {
        var bytes = BitConverter.GetBytes(address); // 小端：低字节在前
        var parts = new List<string>(6);
        for (var i = 5; i >= 0; i--) parts.Add(bytes[i].ToString("X2"));
        return string.Join(":", parts);
    }
}
