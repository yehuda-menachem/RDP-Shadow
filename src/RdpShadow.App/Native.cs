using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpShadow.App;

static class Native
{
    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    public const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000,
        WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);
    [DllImport("user32")] public static extern int GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] public static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32")] public static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32")] public static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] public static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    public const int WM_SYSCOMMAND = 0x0112, MF_CHECKED = 0x8;
    [DllImport("user32")] public static extern nint GetSystemMenu(nint hwnd, bool revert);
    [DllImport("user32")] public static extern int GetMenuState(nint menu, int id, int flags);
    [DllImport("user32")] public static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int LWA_ALPHA = 2, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, GA_ROOT = 2;
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(nint hwnd, int key, byte alpha, int flags);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, int flags);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32")] public static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32")] public static extern nint GetAncestor(nint hwnd, int flags);

    public const int EVENT_OBJECT_LOCATIONCHANGE = 0x800B, OBJID_WINDOW = 0;
    public delegate void WinEventProc(nint hook, int eventType, nint hwnd, int idObject, int idChild, int thread, int time);
    [DllImport("user32")] public static extern nint SetWinEventHook(int eventMin, int eventMax, nint module, WinEventProc proc, int processId, int threadId, int flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(nint hook);
}

/// <summary>Remote session lookup, same data as <c>query session /server:X</c> but not localized.</summary>
static class Wts
{
    const int WTSActive = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct WTS_SESSION_INFO { public int SessionId; public nint WinStationName; public int State; }

    [DllImport("wtsapi32", CharSet = CharSet.Unicode)] static extern nint WTSOpenServer(string serverName);
    [DllImport("wtsapi32")] static extern void WTSCloseServer(nint server);
    [DllImport("wtsapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);
    [DllImport("wtsapi32")] static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32", CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out int bytes);

    /// <summary>The active (logged-on, possibly locked) session and its user, or null if nobody is logged on.</summary>
    /// <exception cref="Win32Exception">Host unreachable, access denied, or AllowRemoteRPC is off.</exception>
    public static (int Id, string? User)? FindActiveSession(string host)
    {
        var server = WTSOpenServer(host);
        try
        {
            if (!WTSEnumerateSessions(server, 0, 1, out var sessions, out var count))
                throw new Win32Exception();
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(sessions + i * size);
                    if (s.State == WTSActive && s.SessionId != 0) return (s.SessionId, UserName(server, s.SessionId));
                }
                return null;
            }
            finally { WTSFreeMemory(sessions); }
        }
        finally { WTSCloseServer(server); }
    }

    static string? UserName(nint server, int session)
    {
        const int WTSUserName = 5;
        if (!WTSQuerySessionInformation(server, session, WTSUserName, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
