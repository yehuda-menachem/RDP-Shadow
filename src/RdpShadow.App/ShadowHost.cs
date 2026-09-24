using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static RdpShadow.App.Native;

namespace RdpShadow.App;

/// <summary>
/// Runs <c>mstsc /shadow</c> and adopts its viewer window as a child of this control.
/// mstsc gets the shadow invitation over an undocumented RPC, so hosting its window is the only
/// supported way to embed shadowing (project_spec.md §3).
/// </summary>
public sealed class ShadowHost : HwndHost
{
    const string ViewerClass = "SrApiViewerAxContainerClass";
    const int SmartSizingCommand = 16; // viewer system menu "Smart sizing"; mstsc persists it as HKCU\...\Terminal Server Client\ShadowSmartSizing

    nint _container, _viewer, _sizeHook;
    Process? _mstsc;
    readonly WinEventProc _onViewerMoved;
    readonly ColorFilter _filter;
    int _brightness, _contrast = 100;

    public ShadowHost()
    {
        _onViewerMoved = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (hwnd == _viewer && idObject == OBJID_WINDOW) FitViewer();
        };
        // Only over a live viewer that is on screen (not while the settings pages hide the session view or the window is
        // minimized: a minimized window still counts as visible, and the filter would redraw at 60fps for nobody).
        _filter = new(() => _viewer != 0 && IsWindowVisible(_container) && !IsIconic(GetAncestor(_container, GA_ROOT))
            && GetWindowRect(_container, out var r) ? r : null);
    }

    /// <summary>Raised when an established shadow ends on its own (remote side closed, network drop).</summary>
    public event Action? Disconnected;

    public bool IsConnected => _viewer != 0;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _container = CreateWindowEx(0, "static", null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 0, 0, parent.Handle, 0, 0, 0);
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Disconnect();
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        FitViewer();
    }

    public async Task ConnectAsync(string host, int sessionId, bool control)
    {
        Disconnect();
        var args = $"/v:{host} /shadow:{sessionId} /noConsentPrompt" + (control ? " /control" : "");

        for (var attempt = 1; ; attempt++)
        {
            var p = Process.Start("mstsc.exe", args);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_mstsc != p || _viewer == 0) return;
                Disconnect(); // also unhooks and stops the color filter, which would otherwise linger while reconnecting
                Disconnected?.Invoke();
            });
            _mstsc = p;

            var viewer = await FindViewerAsync(p);
            if (_mstsc != p) throw new OperationCanceledException();
            if (viewer != 0) { Adopt(viewer); return; }

            // ponytail: one retry when mstsc quits within seconds — seen on the first /shadow launch (2026-09-23).
            // A real failure shows mstsc's own error dialog first, so it takes longer and is not retried.
            var exited = p.HasExited;
            var quickExit = exited && p.ExitTime - p.StartTime < TimeSpan.FromSeconds(5);
            Disconnect(); // disposes p
            if (!quickExit || attempt == 2)
                throw new InvalidOperationException(exited ? "mstsc נסגר לפני שה-Shadow נפתח" : "תם הזמן לפתיחת ה-Shadow");
        }
    }

    /// <summary>Brightness -50..50 and contrast 50..150 (percent) applied on top of the remote image.</summary>
    public void SetColor(int brightness, int contrast)
    {
        (_brightness, _contrast) = (brightness, contrast);
        if (_viewer != 0) _filter.Set(GetAncestor(_container, GA_ROOT), brightness, contrast);
    }

    /// <summary>Flips mstsc's smart sizing (fit to window vs. 1:1 with scrollbars).</summary>
    public void ToggleSmartSizing()
    {
        if (_viewer != 0) PostMessage(_viewer, WM_SYSCOMMAND, SmartSizingCommand, 0);
    }

    public void Disconnect()
    {
        var p = _mstsc;
        _mstsc = null;
        _viewer = 0;
        _filter.Stop();
        if (_sizeHook != 0) { UnhookWinEvent(_sizeHook); _sizeHook = 0; }
        if (p is null) return;
        try { p.Kill(); } catch (InvalidOperationException) { } // already exited
        p.Dispose();
    }

    async Task<nint> FindViewerAsync(Process p)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && _mstsc == p && !p.HasExited)
        {
            for (nint h = 0; (h = FindWindowEx(0, h, ViewerClass, null)) != 0;)
                if (GetWindowThreadProcessId(h, out var pid) != 0 && pid == p.Id && IsWindowVisible(h))
                    return h;
            await Task.Delay(150);
        }
        return 0;
    }

    void Adopt(nint viewer)
    {
        // Style first, then parent: SetParent needs WS_CHILD set and WS_POPUP cleared.
        var style = GetWindowLong(viewer, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLong(viewer, GWL_STYLE, style | WS_CHILD);
        SetParent(viewer, _container);
        _viewer = viewer;
        // mstsc resizes its window to the remote resolution once the stream starts; keep pulling it back.
        GetWindowThreadProcessId(viewer, out var pid);
        _sizeHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _onViewerMoved, pid, 0, 0);
        // Scale the remote screen to the frame. GetMenuState returns -1 if the item is missing -> treated as on, no-op.
        if ((GetMenuState(GetSystemMenu(viewer, false), SmartSizingCommand, 0) & MF_CHECKED) == 0)
            PostMessage(viewer, WM_SYSCOMMAND, SmartSizingCommand, 0);
        FitViewer();
        SetColor(_brightness, _contrast);
    }

    void FitViewer()
    {
        if (_viewer == 0 || !GetWindowRect(_container, out var c) || !GetWindowRect(_viewer, out var v)) return;
        // Screen rects: the borderless container's window rect is its client area. Moving only on mismatch
        // breaks the hook -> MoveWindow -> hook loop.
        if (!v.Equals(c))
            MoveWindow(_viewer, 0, 0, c.Right - c.Left, c.Bottom - c.Top, true);
    }
}
