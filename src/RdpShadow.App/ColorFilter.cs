using System.Runtime.InteropServices;
using System.Windows.Threading;
using static RdpShadow.App.Native;

namespace RdpShadow.App;

/// <summary>
/// Brightness/contrast for the hosted viewer. Its pixels belong to mstsc, so WPF effects can't reach them.
/// Instead a click-through magnifier window (Magnification API at 1x with a color matrix) sits over the
/// viewer and redraws it filtered. Nothing is created while the values are neutral.
/// </summary>
sealed class ColorFilter
{
    readonly Func<RECT?> _target;
    nint _host, _mag;
    RECT _shownAt;
    bool _shown;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };

    /// <param name="target">Screen rect to filter, or null while there is nothing to show.</param>
    public ColorFilter(Func<RECT?> target)
    {
        _target = target;
        _timer.Tick += Tick;
    }

    /// <param name="brightness">-50..50, added to every channel in percent.</param>
    /// <param name="contrast">50..150 percent, around mid-gray.</param>
    public void Set(nint owner, int brightness, int contrast)
    {
        if (brightness == 0 && contrast == 100) { Stop(); return; }
        if (_host == 0 && !Create(owner)) return;
        float c = contrast / 100f, t = 0.5f * (1 - c) + brightness / 100f;
        // Row vector [r g b a 1] x matrix: scale on the diagonal, offset in the last row.
        MagSetColorEffect(_mag, [c, 0, 0, 0, 0, 0, c, 0, 0, 0, 0, 0, c, 0, 0, 0, 0, 0, 1, 0, t, t, t, 0, 1]);
        _timer.Start();
        Tick(null, EventArgs.Empty);
    }

    public void Stop()
    {
        _timer.Stop();
        Show(false);
    }

    bool Create(nint owner)
    {
        // ponytail: never MagUninitialize/destroy; the windows live until the process exits.
        if (!MagInitialize()) return false;
        // Layered + transparent = mouse and keyboard go straight to the viewer underneath.
        _host = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "static", null,
            WS_POPUP, 0, 0, 0, 0, owner, 0, 0, 0);
        SetLayeredWindowAttributes(_host, 0, 255, LWA_ALPHA);
        _mag = CreateWindowEx(0, "Magnifier", null, WS_CHILD | WS_VISIBLE, 0, 0, 0, 0, _host, 0, 0, 0);
        if (_mag == 0) { DestroyWindow(_host); _host = 0; return false; }
        MagSetWindowTransform(_mag, [1, 0, 0, 0, 1, 0, 0, 0, 1]);
        var self = _host;
        MagSetWindowFilterList(_mag, 0 /* MW_FILTERMODE_EXCLUDE */, 1, ref self);
        return true;
    }

    // ponytail: 60fps polling; the windowed magnifier has no change notification, it must be told to repaint.
    void Tick(object? sender, EventArgs e)
    {
        if (_target() is not { } r || r.Right <= r.Left) { Show(false); return; }
        if (!_shown || !r.Equals(_shownAt))
        {
            SetWindowPos(_host, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE | SWP_NOZORDER);
            MoveWindow(_mag, 0, 0, r.Right - r.Left, r.Bottom - r.Top, false);
            _shownAt = r;
            Show(true);
        }
        MagSetWindowSource(_mag, r);
        InvalidateRect(_mag, 0, false);
    }

    void Show(bool show)
    {
        if (_host == 0 || _shown == show) return;
        _shown = show;
        ShowWindow(_host, show ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    [DllImport("magnification")] static extern bool MagInitialize();
    [DllImport("magnification")] static extern bool MagSetColorEffect(nint hwnd, float[] effect);
    [DllImport("magnification")] static extern bool MagSetWindowTransform(nint hwnd, float[] transform);
    [DllImport("magnification")] static extern bool MagSetWindowSource(nint hwnd, RECT source);
    [DllImport("magnification")] static extern bool MagSetWindowFilterList(nint hwnd, int mode, int count, ref nint list);
}
