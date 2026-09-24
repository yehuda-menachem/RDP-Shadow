using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RdpShadow.Shared;

namespace RdpShadow.App;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<Computer> _computers = [];
    readonly HashSet<Computer> _probing = [];
    readonly ClipboardWatcher _watcher = new(Path.Combine(Path.GetTempPath(), "RdpShadow"));
    readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    ClipboardLink? _link;
    ClipboardLink.State? _pillState;
    string? _pillMessage;
    Computer? _session;
    int _sessionId;
    DateTime _connectedAt;
    int _sessionEpoch; // bumped when a session ends, so retries of an old session stop even if the same computer is reopened
    bool _loadingUi, _fullScreen, _control;
    WindowState _stateBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var host in _settings.Computers) _computers.Add(new Computer(host));
        ComputerList.ItemsSource = _computers;
        QualityHostBox.ItemsSource = PrepareHostBox.ItemsSource = _computers;
        QualityHostBox.DisplayMemberPath = PrepareHostBox.DisplayMemberPath = nameof(Computer.Host);

        // Older versions allowed up to 2048MB; see ClipSetting_Changed for the 500 cap.
        _settings.Clipboard = _settings.Clipboard with { MaxMegabytes = Math.Clamp(_settings.Clipboard.MaxMegabytes, 1, 500) };
        LoadClipSettingsUi();
        ApplyWatcherSettings();
        // Sliders' ValueChanged pushes the values to the shadow host.
        BrightnessSlider.Value = Math.Clamp(_settings.Brightness, -50, 50);
        ContrastSlider.Value = Math.Clamp(_settings.Contrast, 50, 150);
        Nav.SelectedIndex = 0;
        UpdateEmptyHint();

        Shadow.Disconnected += () => _ = ReconnectAsync();
        // Nobody sees the list during a session or minimized: skip the network round (TCP, RPC, SMB per computer).
        _probeTimer.Tick += (_, _) => { if (_session is null && WindowState != WindowState.Minimized) ProbeAll(); };
        _probeTimer.Start();
        _toastTimer.Tick += (_, _) => { Toast.IsOpen = false; _toastTimer.Stop(); };
        // Otherwise an oversized copy just doesn't arrive and the old content gets pasted over there.
        _watcher.TooLarge += _ => Dispatcher.BeginInvoke(() =>
        {
            if (_link is not null && _settings.Clipboard.Direction != ClipDirection.ToMe && WindowState != WindowState.Minimized)
                ShowToast("לא נשלח", $"גדול מ-{_settings.Clipboard.MaxMegabytes}MB. אפשר לשנות את המגבלה בעמוד \"לוח\".");
        });
        ProbeAll();
    }

    // ================= navigation =================

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: UIElement page }) return;
        foreach (var p in new UIElement[] { ComputersPage, ClipboardPage, QualityPage, PreparePage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == QualityPage && QualityHostBox.SelectedItem is null) QualityHostBox.SelectedIndex = 0;
        if (page == PreparePage && PrepareHostBox.SelectedItem is null) PrepareHostBox.SelectedIndex = 0;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(_computers).Filter =
            text.Length == 0 ? null : o => ((Computer)o).Host.Contains(text, StringComparison.OrdinalIgnoreCase);
        Nav.SelectedIndex = 0;
    }

    // ================= computers =================

    void NewHostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddComputer_Click(sender, e);
    }

    void AddComputer_Click(object sender, RoutedEventArgs e)
    {
        var host = NewHostBox.Text.Trim().TrimStart('\\'); // "\\PC" pasted from Explorer
        if (host.Length == 0 || host.Any(ch => char.IsWhiteSpace(ch) || ch is '\\' or '/')) return;
        if (_computers.Any(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) { NewHostBox.Clear(); return; }
        var computer = new Computer(host);
        _computers.Add(computer);
        SaveComputers();
        NewHostBox.Clear();
        _ = ProbeAsync(computer);
    }

    void RemoveComputer_Click(object sender, RoutedEventArgs e)
    {
        var c = (Computer)((FrameworkElement)sender).DataContext;
        if (c == _session) return;
        _computers.Remove(c);
        SaveComputers();
    }

    void SaveComputers()
    {
        _settings.Computers = _computers.Select(c => c.Host).ToList();
        _settings.Save();
        UpdateEmptyHint();
    }

    void UpdateEmptyHint() => EmptyHint.Visibility = _computers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void PrepareComputer_Click(object sender, RoutedEventArgs e)
    {
        PrepareHostBox.SelectedItem = ((FrameworkElement)sender).DataContext;
        Nav.SelectedIndex = 3;
    }

    void ProbeAll()
    {
        foreach (var c in _computers.ToList()) _ = ProbeAsync(c);
    }

    async Task ProbeAsync(Computer c)
    {
        if (!_probing.Add(c)) return;
        try
        {
            if (!await Reachable(c.Host, 3389))
            {
                c.Detail = "לא עונה";
                (c.ShadowText, c.ShadowColor, c.AgentText, c.AgentColor) = ("לא זמין", Computer.Gray, "", Computer.Gray);
                c.CanConnect = c.NeedsAgent = c.AgentOutdated = false;
                return;
            }

            (int Id, string? User)? session = null;
            string? error = null;
            try { session = await Task.Run(() => Wts.FindActiveSession(c.Host)); }
            catch (Win32Exception ex) { error = ex.Message; }

            c.Detail = session is { } s ? $"משתמש מחובר: {s.User}" : error is null ? "אין משתמש מחובר" : $"אין גישה לרשימת הסשנים: {error}";
            (c.ShadowText, c.ShadowColor) = session is not null ? ("מוכן ל-Shadow", Computer.Green)
                : error is null ? ("אין משתמש", Computer.Gray) : ("לא ידוע", Computer.Yellow);
            c.CanConnect = session is not null || error is not null; // no session list: still try the console session

            if (await Reachable(c.Host, Wire.Port))
            {
                (c.AgentText, c.AgentColor, c.NeedsAgent) = ("לוח זמין", Computer.Green, false);
                c.AgentOutdated = await Task.Run(() => RemoteAgent.IsOutdated(c.Host));
            }
            else
            {
                var installed = await Task.Run(() => RemoteAgent.IsInstalled(c.Host));
                (c.AgentText, c.AgentColor, c.NeedsAgent) = (installed ? "Agent לא פעיל" : "Agent לא מותקן", Computer.Yellow, true);
                c.AgentOutdated = false; // "prepare" reinstalls anyway
            }
        }
        finally { _probing.Remove(c); }
    }

    async void UpdateAgent_Click(object sender, RoutedEventArgs e)
    {
        var c = (Computer)((FrameworkElement)sender).DataContext;
        // Holding the probe slot keeps the 10 s probe from repainting the row while the service is stopped.
        if (!_probing.Add(c)) return;
        (c.AgentOutdated, c.AgentText, c.AgentColor) = (false, "מעדכן Agent...", Computer.Yellow);
        ComputersNotice.Visibility = Visibility.Collapsed;
        try
        {
            // Same path as "prepare": copies the exe and restarts the service; machine settings are kept.
            await Task.Run(() => RemoteAgent.Install(c.Host, RemoteAgent.ReadSettings(c.Host)));
        }
        catch (Exception ex) when (IsRemoteError(ex))
        {
            ComputersNotice.Text = $"עדכון ה-Agent ב-{c.Host} נכשל: {RemoteErrorText(ex)}";
            ComputersNotice.Visibility = Visibility.Visible;
        }
        finally { _probing.Remove(c); }
        _ = ProbeAsync(c);
    }

    static async Task<bool> Reachable(string host, int port)
    {
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(1500)); return true; }
        catch (Exception ex) when (ex is SocketException or TimeoutException) { return false; }
    }

    // ================= session =================

    void View_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync((Computer)((FrameworkElement)sender).DataContext, control: false);
    void Control_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync((Computer)((FrameworkElement)sender).DataContext, control: true);

    async Task ConnectAsync(Computer c, bool control)
    {
        if (_session is not null) return;
        _session = c;
        ComputersNotice.Visibility = Visibility.Collapsed;
        SessionHost.Text = c.Host;
        SessionDetail.Text = "מתחבר...";
        SessionPlaceholder.Text = "פותח Shadow...";
        _control = control;
        (ModeControl.IsChecked, ModeView.IsChecked) = (control, !control);
        Shell.Visibility = Visibility.Hidden;
        SessionView.Visibility = Visibility.Visible;
        try
        {
            await OpenShadowAsync(c);
            StartLink();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            EndSession(ex.Message);
        }
    }

    /// <summary>Finds the active session (it changes when someone else logs on) and shadows it.</summary>
    async Task OpenShadowAsync(Computer c)
    {
        var epoch = _sessionEpoch;
        (int Id, string? User)? session;
        try { session = await Task.Run(() => Wts.FindActiveSession(c.Host)); }
        catch (Win32Exception) { session = (1, null); } // AllowRemoteRPC off: the console session is 1 on client Windows
        if (_session != c || _sessionEpoch != epoch) throw new OperationCanceledException(); // disconnected while looking up the session
        if (session is not { } s) throw new InvalidOperationException("אין משתמש מחובר במחשב הזה");

        _sessionId = s.Id;
        SessionDetail.Text = s.User is null ? $"סשן {s.Id}" : $"{s.User} · סשן {s.Id}";
        await Shadow.ConnectAsync(c.Host, s.Id, _control);
        SessionPlaceholder.Text = "";
        _connectedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The shadow ended on its own (network blip, remote logoff/logon): keep the session screen and the clipboard link,
    /// and retry for a minute. The link reconnects by itself.
    /// </summary>
    async Task ReconnectAsync()
    {
        var c = _session;
        var epoch = _sessionEpoch;
        if (c is null) return;
        // Dropped right after opening = refused, not a blip; retrying would just loop.
        if (DateTime.UtcNow - _connectedAt < TimeSpan.FromSeconds(10)) { EndSession("החיבור נסגר מהצד המרוחק"); return; }

        var deadline = DateTime.UtcNow.AddMinutes(1);
        string? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            SessionDetail.Text = SessionPlaceholder.Text = "החיבור נפל. מתחבר מחדש...";
            await Task.Delay(3000);
            if (_sessionEpoch != epoch) return; // user disconnected meanwhile
            // Unreachable host: skip, or mstsc sits in its error dialog until the 30 s viewer timeout.
            if (!await Reachable(c.Host, 3389)) { lastError = "המחשב לא עונה"; continue; }
            if (_sessionEpoch != epoch) return;
            try
            {
                await OpenShadowAsync(c);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { lastError = ex.Message; }
        }
        if (_sessionEpoch == epoch) EndSession($"החיבור נפל ולא הצליח להתחבר מחדש ({lastError})");
    }

    void StartLink()
    {
        if (_session is null || _link is not null) return;
        if (!_settings.Clipboard.Enabled) { UpdatePill(null, "לוח כבוי"); return; }
        UpdatePill(ClipboardLink.State.Connecting, null); // not the previous session's state while the first attempt runs
        var link = _link = new ClipboardLink(_session.Host, _watcher, _settings.Clipboard);
        // Events queued by a link that was stopped meanwhile must not repaint the pill.
        link.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => { if (_link == link) UpdatePill(state, message); });
        link.Synced += (direction, what) => Dispatcher.BeginInvoke(() => { if (_link == link) OnSynced(direction, what); });
        link.Transfer += (direction, done, total) => Dispatcher.BeginInvoke(() => { if (_link == link) OnTransfer(direction, done, total); });
        link.SetPaused(WindowState == WindowState.Minimized);
    }

    void StopLink()
    {
        var link = _link;
        _link = null;
        if (link is not null) _ = link.DisposeAsync().AsTask();
    }

    void EndSession(string? notice = null)
    {
        StopLink();
        Shadow.Disconnect();
        if (_fullScreen) FullScreen_Click(this, new RoutedEventArgs());
        Toast.IsOpen = false;
        var computer = _session;
        _session = null;
        _sessionEpoch++;
        SessionView.Visibility = Visibility.Hidden;
        Shell.Visibility = Visibility.Visible;
        ComputersNotice.Text = notice ?? "";
        ComputersNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (computer is not null) _ = ProbeAsync(computer);
    }

    void Disconnect_Click(object sender, RoutedEventArgs e) => EndSession();

    async void Mode_Click(object sender, RoutedEventArgs e)
    {
        var control = ModeControl.IsChecked == true;
        if (_session is null || !Shadow.IsConnected || control == _control)
        {
            // Same mode (no reconnect needed) or a switch already running: undo the click.
            (ModeControl.IsChecked, ModeView.IsChecked) = (_control, !_control);
            return;
        }
        _control = control;
        SessionPlaceholder.Text = "מחליף מצב...";
        try
        {
            await Shadow.ConnectAsync(_session.Host, _sessionId, control);
            SessionPlaceholder.Text = "";
            _connectedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { EndSession(ex.Message); }
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Shadow.ToggleSmartSizing();

    void ColorButton_Click(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = true;

    void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Shadow is null) return; // fires during InitializeComponent (Minimum/Value set), before Shadow exists
        Shadow.SetColor((int)BrightnessSlider.Value, (int)ContrastSlider.Value);
    }

    void ColorReset_Click(object sender, RoutedEventArgs e) => (BrightnessSlider.Value, ContrastSlider.Value) = (0, 100);

    void ColorPopup_Closed(object? sender, EventArgs e)
    {
        (_settings.Brightness, _settings.Contrast) = ((int)BrightnessSlider.Value, (int)ContrastSlider.Value);
        _settings.Save();
    }

    void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximize to cover the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
        FullScreenIcon.Text = _fullScreen ? "" : "";
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        _link?.SetPaused(WindowState == WindowState.Minimized);
        if (WindowState == WindowState.Minimized) Toast.IsOpen = false;
    }

    void UpdatePill(ClipboardLink.State? state, string? message)
    {
        (_pillState, _pillMessage) = (state, message); // restored when a transfer's progress ends
        ClipProgress.Visibility = Visibility.Collapsed;
        (ClipPillText.Text, ClipPillIcon.Text, var brush) = state switch
        {
            ClipboardLink.State.Connected => ("לוח מסונכרן", "", "SystemFillColorSuccessBrush"),
            ClipboardLink.State.Connecting => ("לוח: מתחבר...", "", "TextFillColorSecondaryBrush"),
            ClipboardLink.State.Unavailable => ("לוח לא זמין", "", "SystemFillColorCautionBrush"),
            _ => (message ?? "", "", "TextFillColorSecondaryBrush"),
        };
        ClipPillText.Foreground = ClipPillIcon.Foreground = (Brush)FindResource(brush);
        ClipPill.ToolTip = message;
    }

    void OnTransfer(string direction, long done, long total)
    {
        if (done >= total) { UpdatePill(_pillState, _pillMessage); return; }
        ClipProgress.Visibility = Visibility.Visible;
        ClipProgress.Value = 100.0 * done / total;
        ClipPillText.Text = $"{(direction == "in" ? "מקבל" : "שולח")} {done >> 20}/{total >> 20}MB";
        ClipPillIcon.Text = direction == "in" ? "" : ""; // Download / Upload
        ClipPillText.Foreground = ClipPillIcon.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
    }

    void OnSynced(string direction, string what)
    {
        var host = _session?.Host ?? "";
        ClipLastSync.Text = $"סנכרון אחרון: {DateTime.Now:HH:mm:ss} · {what} · " + (direction == "in" ? $"מ-{host} אליך" : $"ממך ל-{host}");
        if (WindowState == WindowState.Minimized || !IsActive) return;
        ShowToast(direction == "in" ? $"הועתק מ-{host}" : $"נשלח ל-{host}",
            direction == "in" ? $"{what} · מוכן להדבקה אצלך (גם ב-Win+V)" : $"{what} · מוכן להדבקה שם");
    }

    void ShowToast(string title, string text)
    {
        (ToastTitle.Text, ToastText.Text) = (title, text);
        Toast.HorizontalOffset = 20;
        Toast.VerticalOffset = Math.Max(0, SessionView.ActualHeight - 90);
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        _probeTimer.Stop();
        EndSession();
        _watcher.Dispose();
    }

    // ================= clipboard settings =================

    void LoadClipSettingsUi()
    {
        _loadingUi = true;
        var s = _settings.Clipboard;
        ClipEnabled.IsChecked = s.Enabled;
        ClipDirectionBox.SelectedIndex = (int)s.Direction;
        ClipText.IsChecked = s.Text;
        ClipImages.IsChecked = s.Images;
        ClipFiles.IsChecked = s.Files;
        ClipMaxBox.Text = s.MaxMegabytes.ToString();
        _loadingUi = false;
    }

    void ClipSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || !IsInitialized) return;
        // 500: one item travels as a single frame, capped by Wire.MaxFrame (512MB); bigger would be cut off mid-way.
        if (!int.TryParse(ClipMaxBox.Text, out var max) || max is < 1 or > 500) max = _settings.Clipboard.MaxMegabytes;
        ClipMaxBox.Text = max.ToString();
        _settings.Clipboard = new ClipSettings
        {
            Enabled = ClipEnabled.IsChecked == true,
            Direction = (ClipDirection)Math.Max(0, ClipDirectionBox.SelectedIndex),
            Text = ClipText.IsChecked == true,
            Images = ClipImages.IsChecked == true,
            Files = ClipFiles.IsChecked == true,
            MaxMegabytes = max,
        };
        _settings.Save();
        ApplyWatcherSettings();
        // Off = drop the link (StartLink then shows "off"), so the pill never claims a sync that isn't happening.
        if (!_settings.Clipboard.Enabled) StopLink();
        _link?.UpdateSettings(_settings.Clipboard);
        StartLink();
    }

    void ApplyWatcherSettings()
    {
        var s = _settings.Clipboard;
        _watcher.MaxBytes = s.MaxMegabytes * 1024L * 1024;
        _watcher.IncludeText = s.Text;
        _watcher.IncludeImages = s.Images;
        _watcher.IncludeFiles = s.Files;
    }

    // ================= quality =================

    async void QualityHost_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QualityHostBox.SelectedItem is not Computer c) return;
        var s = await Task.Run(() => RemoteAgent.ReadSettings(c.Host));
        _loadingUi = true;
        QFps60.IsChecked = s.Fps60 == true;
        QAvc444.IsChecked = s.Avc444 == true;
        QHardware.IsChecked = s.HardwareEncode == true;
        _loadingUi = false;
        MatchProfile();
    }

    void Profile_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _loadingUi = true;
        (QFps60.IsChecked, QAvc444.IsChecked, QHardware.IsChecked) =
            sender == ProfileMax ? (true, true, true) : sender == ProfileBalanced ? (false, false, true) : (false, false, false);
        _loadingUi = false;
    }

    void QualityToggle_Click(object sender, RoutedEventArgs e) => MatchProfile();

    void MatchProfile()
    {
        _loadingUi = true;
        var key = (QFps60.IsChecked == true, QAvc444.IsChecked == true, QHardware.IsChecked == true);
        ProfileMax.IsChecked = key == (true, true, true);
        ProfileBalanced.IsChecked = key == (false, false, true);
        ProfileEco.IsChecked = key == (false, false, false);
        _loadingUi = false;
    }

    async void QualityApply_Click(object sender, RoutedEventArgs e)
    {
        if (QualityHostBox.SelectedItem is not Computer c) return;
        QualityApplyButton.IsEnabled = false;
        QualityStatus.Text = "מחיל...";
        try
        {
            var (fps, avc, hw) = (QFps60.IsChecked == true, QAvc444.IsChecked == true, QHardware.IsChecked == true);
            await Task.Run(() =>
            {
                if (!RemoteAgent.IsInstalled(c.Host)) throw new InvalidOperationException("צריך להתקין את ה-Agent קודם (בעמוד \"הכנת מחשב\")");
                RemoteAgent.Apply(c.Host, RemoteAgent.ReadSettings(c.Host) with { Fps60 = fps, Avc444 = avc, HardwareEncode = hw });
            });
            QualityStatus.Text = $"הוחל על {c.Host}. ייכנס לתוקף בחיבור הבא.";
        }
        catch (Exception ex) when (IsRemoteError(ex)) { QualityStatus.Text = RemoteErrorText(ex); }
        finally { QualityApplyButton.IsEnabled = true; }
    }

    // ================= prepare =================

    void PrepareHost_Changed(object sender, SelectionChangedEventArgs e) => _ = RefreshPrepareAsync();
    void PrepareRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshPrepareAsync();

    async Task RefreshPrepareAsync()
    {
        if (PrepareHostBox.SelectedItem is not Computer c) return;
        PrepareStatus.Text = "בודק...";
        var status = await Task.Run(() => RemoteAgent.ReadStatus(c.Host));
        var alive = await Reachable(c.Host, Wire.Port);
        var installed = status is not null || await Task.Run(() => RemoteAgent.IsInstalled(c.Host));
        var settings = await Task.Run(() => RemoteAgent.ReadSettings(c.Host));
        var outdated = installed && await Task.Run(() => RemoteAgent.IsOutdated(c.Host));
        if (PrepareHostBox.SelectedItem != c) return; // user switched meanwhile

        Mark(PAgent, outdated ? (false, alive ? "פועל, יש גרסה חדשה" : "מותקן, לא פעיל, יש גרסה חדשה") : alive ? (true, "פועל") : installed ? (false, "מותקן, לא פעיל") : (false, "לא מותקן"));
        Mark(PRdp, status is null ? null : (status.RdpEnabled, status.RdpEnabled ? "תקין" : "כבוי"));
        Mark(PRpc, status is null ? null : (status.RemoteRpc, status.RemoteRpc ? "תקין" : "כבוי"));
        Mark(PFirewall, status is null ? null : (status.FirewallRule, status.FirewallRule ? "תקין" : "חסר"));
        var shadow = status?.Shadow ?? settings.Shadow;
        PShadowBox.SelectedIndex = Math.Clamp(shadow, 1, 4) - 1;
        PShadowNow.Text = status?.Shadow is { } now ? $"כרגע: {((ComboBoxItem)PShadowBox.Items[Math.Clamp(now, 1, 4) - 1]).Content}" : "המצב הנוכחי יוצג אחרי התקנת ה-Agent";
        PClipHistory.IsChecked = settings.ClipboardHistory;
        var user = Credentials.SavedUser(c.Host);
        CredStatus.Text = user is null ? "לא נשמרו פרטים. בדומיין משתמשים בחשבון שלך אוטומטית." : $"שמור: {user}";
        FixAllButton.Content = outdated ? "עדכן Agent ותקן" : installed ? "החל ותקן" : "התקן Agent ותקן הכל";
        PrepareStatus.Text = status?.Error is { } err ? $"שגיאה אחרונה ב-Agent: {err}" : "";
    }

    void Mark(TextBlock target, (bool Ok, string Text)? state)
    {
        target.Text = state is { } s ? (s.Ok ? "✓ " : "⚠ ") + s.Text : "לא ידוע (דרוש Agent)";
        target.Foreground = (Brush)FindResource(state is null ? "TextFillColorSecondaryBrush"
            : state.Value.Ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    async void FixAll_Click(object sender, RoutedEventArgs e)
    {
        if (PrepareHostBox.SelectedItem is not Computer c) return;
        FixAllButton.IsEnabled = false;
        PrepareStatus.Text = "מתקין ומגדיר...";
        var shadow = PShadowBox.SelectedIndex + 1;
        var history = PClipHistory.IsChecked == true;
        try
        {
            await Task.Run(() => RemoteAgent.Install(c.Host, RemoteAgent.ReadSettings(c.Host) with { Shadow = shadow, ClipboardHistory = history }));
            PrepareStatus.Text = "הושלם.";
            await Task.Delay(4000); // service writes status.json on its first tick
            await RefreshPrepareAsync();
            _ = ProbeAsync(c);
        }
        catch (Exception ex) when (IsRemoteError(ex)) { PrepareStatus.Text = RemoteErrorText(ex); }
        finally { FixAllButton.IsEnabled = true; }
    }

    void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (PrepareHostBox.SelectedItem is not Computer c || CredUser.Text.Trim().Length == 0 || CredPassword.Password.Length == 0) return;
        try
        {
            Credentials.Save(c.Host, CredUser.Text.Trim(), CredPassword.Password);
            CredPassword.Clear();
            _ = RefreshPrepareAsync();
        }
        catch (Win32Exception ex) { CredStatus.Text = $"השמירה נכשלה: {ex.Message}"; }
    }

    static bool IsRemoteError(Exception ex) => ex is Win32Exception or IOException or UnauthorizedAccessException
        or InvalidOperationException or System.ServiceProcess.TimeoutException;

    static string RemoteErrorText(Exception ex) => ex switch
    {
        UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => "אין הרשאת מנהל על המחשב. שמור פרטי מנהל למטה ונסה שוב.",
        FileNotFoundException => ex.Message,
        System.ServiceProcess.TimeoutException => "השירות במחשב המרוחק לא הגיב בזמן. נסה שוב.",
        _ => ex.Message,
    };
}
