using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace RdpShadow.Shared;

/// <summary>
/// Raw Win32 clipboard on its own STA thread with a message-only window, so the same code runs in the WPF app
/// and in the windowless agent. Copies every HGLOBAL format except a blocklist, which keeps Word/Excel/browser
/// fidelity without knowing each app's formats.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    /// <summary>Present on everything we write: a clipboard update carrying it came from us, not the user.</summary>
    const string MarkerFormat = "RdpShadowSync.Origin";

    /// <summary>Raised on the watcher thread when the user copies something (never for our own writes).</summary>
    public event Action<ClipItem>? Copied;

    /// <summary>Per-item size ceiling in bytes (formats + files). Bigger copies are skipped.</summary>
    public long MaxBytes { get; set; } = 100L * 1024 * 1024;
    public bool IncludeFiles { get; set; } = true;
    public bool IncludeImages { get; set; } = true;
    public bool IncludeText { get; set; } = true;

    /// <summary>Last update skipped for size, so the UI can say why nothing arrived.</summary>
    public event Action<long>? TooLarge;

    /// <summary>
    /// Another process re-published an item we wrote on the other side. mstsc /shadow does this with its own clipboard
    /// channel and marks it CanIncludeInClipboardHistory=0, which drops the user's copy from Win+V.
    /// </summary>
    public event Action? Reclaimed;

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly ConcurrentQueue<Action> _work = new();
    readonly string _tempRoot;
    WndProc? _wndProc;
    nint _hwnd;
    uint _marker;

    public ClipboardWatcher(string tempRoot)
    {
        _tempRoot = tempRoot;
        CleanupTemp();
        _thread = new Thread(Run) { IsBackground = true, Name = "Clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>GetClipboardSequenceNumber: changes on every clipboard write by anyone.</summary>
    public static uint Sequence => GetClipboardSequenceNumber();

    /// <summary>Reads the current clipboard (for "send what I copied while paused"). Runs on the watcher thread.</summary>
    public Task<ClipItem?> ReadCurrentAsync() => Invoke(() => Read(ignoreOwn: true));

    /// <summary>Puts a remote item on this machine's clipboard (and therefore Win+V history).</summary>
    public Task WriteAsync(ClipItem item) => Invoke(() => { Write(item); return true; });

    Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        PostMessage(_hwnd, WM_APP, 0, 0);
        return tcs.Task;
    }

    void Run()
    {
        _marker = RegisterClipboardFormat(MarkerFormat);
        _wndProc = (h, msg, w, l) =>
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                // Our marker under a foreign owner: not our write, so a copy of one relayed back. Only when it is kept out of
                // Win+V: an item that already allows history (Snipping Tool sets the flag, mstsc keeps it) gets recorded, and
                // rewriting it would cut off Windows while it is still fetching the image from mstsc.
                if (IsClipboardFormatAvailable(_marker) && GetClipboardOwner() != _hwnd && HistoryBlocked()) Reclaimed?.Invoke();
                // Nobody listening (no link, paused, send off): don't read, a copied file would be loaded into memory for nothing.
                try { if (Copied is not null && Read(ignoreOwn: true) is { } item) Copied?.Invoke(item); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { } // file vanished/locked: skip this copy
                return 0;
            }
            if (msg == WM_APP) { while (_work.TryDequeue(out var a)) a(); return 0; }
            return DefWindowProc(h, msg, w, l);
        };
        var className = "RdpShadowClipboard" + Environment.ProcessId;
        var cls = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), lpszClassName = className };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        AddClipboardFormatListener(_hwnd);
        _ready.Set();
        while (GetMessage(out var m, 0, 0, 0) > 0) DispatchMessage(ref m);
        RemoveClipboardFormatListener(_hwnd);
        DestroyWindow(_hwnd);
    }

    public void Dispose() => PostMessage(_hwnd, WM_QUIT, 0, 0);

    // ---------- read ----------

    ClipItem? Read(bool ignoreOwn)
    {
        var item = new ClipItem();
        var paths = new List<string>();
        if (!Open()) return null;
        try
        {
            if (ignoreOwn && IsClipboardFormatAvailable(_marker)) return null;
            // Password managers and other "don't record this" copies: never send them to the other machine's history.
            if (IsClipboardFormatAvailable(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"))
                || IsClipboardFormatAvailable(RegisterClipboardFormat("Clipboard Viewer Ignore"))
                || Copy(GetClipboardData(RegisterClipboardFormat("CanIncludeInClipboardHistory"))) is [0, 0, 0, 0]) return null;
            long total = 0;
            for (uint f = 0; (f = EnumClipboardFormats(f)) != 0;)
            {
                var name = FormatKey(f);
                if (name is null || !Wanted(name)) continue;
                if (f == CF_HDROP) { paths.AddRange(DropPaths()); continue; }
                var h = GetClipboardData(f);
                if (h == 0) continue;
                total += (long)GlobalSize(h); // before copying: a huge format must not be allocated just to be dropped
                if (total > MaxBytes) { TooLarge?.Invoke(total); return null; }
                if (Copy(h) is { } data) item.Formats.Add((name, data));
            }
        }
        finally { CloseClipboard(); }

        if (paths.Count > 0 && !AddFiles(item, paths)) return null;
        // Only formats we deliberately drop (e.g. OLE internals, or everything with text sync off) -> nothing to send.
        return item.Formats.Count == 0 && item.Files.Count == 0 ? null : item;
    }

    bool HistoryBlocked()
    {
        if (!Open()) return false;
        try { return Copy(GetClipboardData(RegisterClipboardFormat("CanIncludeInClipboardHistory"))) is [0, 0, 0, 0]; }
        finally { CloseClipboard(); }
    }

    bool Wanted(string name)
    {
        if (Blocked.Contains(name)) return false;
        if (name == "#15") return IncludeFiles;
        if (name is "#8" or "PNG" or "JFIF" or "GIF") return IncludeImages;
        return IncludeText;
    }

    // GDI handles (not HGLOBAL), synthesized duplicates, OLE plumbing and anything naming paths on the source machine.
    static readonly HashSet<string> Blocked =
    [
        "#1", "#2", "#3", "#7", "#9", "#14", "#17", // TEXT/OEMTEXT and DIBV5 are synthesized from UNICODETEXT/DIB
        "DataObject", "Ole Private Data", "Object Descriptor", "Link Source Descriptor", "Link Source", "Embed Source",
        "OwnerLink", "ObjectLink", "Native", "Embedded Object",
        "Shell IDList Array", "FileName", "FileNameW", "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents",
        "Preferred DropEffect", "Shell Object Offsets", "DragContext", "DragImageBits", "UsingDefaultDragImage",
        "IsShowingText", "IsShowingLayered", "DropDescription", "DisableDragText", "ComputedDragImage", "IsComputingImage",
        MarkerFormat,
    ];

    string? FormatKey(uint f)
    {
        if (f >= 0x0200 && f < 0x0300) return null; // CF_PRIVATEFIRST..LAST, CF_GDIOBJFIRST..: process-local handles
        if (f < 0xC000) return f is 0x80 or 0x81 or 0x82 or 0x83 or 0x8E ? null : "#" + f; // display formats are owner-drawn
        var sb = new StringBuilder(256);
        return GetClipboardFormatName(f, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    static byte[]? Copy(nint h)
    {
        var size = (long)GlobalSize(h);
        if (size > int.MaxValue) return null;
        var p = GlobalLock(h);
        if (p == 0) return null; // not an HGLOBAL (some apps hand out other handle types under custom names)
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, (int)size);
            return data;
        }
        finally { GlobalUnlock(h); }
    }

    static List<string> DropPaths()
    {
        var h = GetClipboardData(CF_HDROP);
        var list = new List<string>();
        if (h == 0) return list;
        var count = DragQueryFile(h, uint.MaxValue, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var sb = new StringBuilder((int)DragQueryFile(h, i, null, 0) + 1);
            DragQueryFile(h, i, sb, (uint)sb.Capacity);
            list.Add(sb.ToString());
        }
        return list;
    }

    bool AddFiles(ClipItem item, List<string> paths)
    {
        long total = item.Size;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            if (Directory.Exists(path))
            {
                item.Files.Add((name, null));
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    var rel = name + "/" + Path.GetRelativePath(path, entry).Replace('\\', '/');
                    if (Directory.Exists(entry)) { item.Files.Add((rel, null)); continue; }
                    total += new FileInfo(entry).Length;
                    if (total > MaxBytes) { TooLarge?.Invoke(total); return false; }
                    item.Files.Add((rel, File.ReadAllBytes(entry)));
                }
            }
            else if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
                if (total > MaxBytes) { TooLarge?.Invoke(total); return false; }
                item.Files.Add((name, File.ReadAllBytes(path)));
            }
        }
        return true;
    }

    // ---------- write ----------

    void Write(ClipItem item)
    {
        var dropPaths = item.Files.Count > 0 && IncludeFiles ? MaterializeFiles(item) : null;
        if (!Open()) throw new IOException("הלוח תפוס על ידי תוכנה אחרת");
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in item.Formats)
            {
                // Trust boundary: the sender names the formats. Accept only what our own Read would send: a raw
                // CF_HDROP could point a paste at any path (e.g. \\attacker\share), GDI ids would be bogus handles.
                if (format == "#15" || !Wanted(format)) continue;
                uint id;
                if (!format.StartsWith('#')) id = RegisterClipboardFormat(format);
                else if (!uint.TryParse(format.AsSpan(1), out id) || FormatKey(id) != format) continue;
                Set(id, data);
            }
            if (dropPaths is not null)
            {
                Set(CF_HDROP, DropFiles(dropPaths));
                Set(RegisterClipboardFormat("Preferred DropEffect"), BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            }
            Set(_marker, [1]);
        }
        finally { CloseClipboard(); }
    }

    static void Set(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (h == 0) throw new OutOfMemoryException("GlobalAlloc");
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == 0) GlobalFree(h); // on success the clipboard owns h
    }

    /// <summary>Writes received files under a fresh temp folder; returns the top-level entries for CF_HDROP.</summary>
    List<string> MaterializeFiles(ClipItem item)
    {
        CleanupTemp(); // the helper can run for weeks: don't let pasted files pile up until the next start
        var root = Path.Combine(_tempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var top = new List<string>();
        foreach (var (rel, data) in item.Files)
        {
            // Trust boundary: the path comes off the network. Reject anything escaping the temp folder.
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"bad path {rel}");
            if (data is null) Directory.CreateDirectory(full);
            else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, data); }
            if (!rel.Contains('/')) top.Add(full);
        }
        return top;
    }

    static byte[] DropFiles(List<string> paths)
    {
        // DROPFILES { DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; } then double-null-terminated UTF-16 list.
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var buf = new byte[20 + list.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0), 20);
        BitConverter.TryWriteBytes(buf.AsSpan(16), 1);
        list.CopyTo(buf, 20);
        return buf;
    }

    // ponytail: temp files live about a day (swept on start and on each file paste), not tracked per paste.
    void CleanupTemp()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_tempRoot))
                if (Directory.GetCreationTime(dir) < DateTime.Now.AddDays(-1)) Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool Open()
    {
        // Another process holding the clipboard is normal and brief.
        for (var i = 0; i < 20; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    // ---------- Win32 ----------

    const uint WM_CLIPBOARDUPDATE = 0x031D, WM_APP = 0x8000, WM_QUIT = 0x0012, CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;
    static readonly nint HWND_MESSAGE = -3;

    delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(int ex, string cls, string? name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] static extern nint DefWindowProc(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32")] static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32")] static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32")] static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32")] static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32")] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32")] static extern bool CloseClipboard();
    [DllImport("user32")] static extern bool EmptyClipboard();
    [DllImport("user32")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32")] static extern nint GetClipboardData(uint format);
    [DllImport("user32")] static extern nint SetClipboardData(uint format, nint mem);
    [DllImport("user32")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] static extern nint GetClipboardOwner();
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("kernel32")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32")] static extern nint GlobalFree(nint mem);
    [DllImport("kernel32")] static extern nint GlobalLock(nint mem);
    [DllImport("kernel32")] static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32")] static extern nuint GlobalSize(nint mem);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(nint drop, uint index, StringBuilder? file, uint max);
}
