using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using RdpShadow.Shared;

namespace RdpShadow.Agent;

sealed class AgentService : ServiceBase
{
    public const string Name = "RdpShadowAgent";
    const string FirewallRule = "RdpShadow Agent";
    static readonly string Dir = AppContext.BaseDirectory;
    static readonly string Exe = Environment.ProcessPath!;

    Timer? _timer;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _relayGate = new(1, 1);
    CancellationTokenSource? _activeRelay;
    string? _error, _helperError, _listenError;
    bool _firewall;
    AgentStatus? _written;

    public AgentService() => ServiceName = Name;

    protected override void OnStart(string[] args)
    {
        // Registry/netsh failures must not stop the clipboard helper: record them for the app's status page.
        try { ApplySettings(MachineSettings.Load(Path.Combine(Dir, MachineSettings.FileName))); }
        catch (Exception ex) { _error = ex.Message; }
        _firewall = Netsh($"advfirewall firewall show rule name=\"{FirewallRule}\"");
        _timer = new Timer(_ => Tick(), null, 0, 3000);
        _ = ListenAsync(_stop.Token);
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        // Wait out a tick in progress: it could relaunch a helper after the kill below and keep the exe locked during an update.
        using (var done = new ManualResetEvent(false))
            if (_timer?.Dispose(done) == true) done.WaitOne(TimeSpan.FromSeconds(10));
        foreach (var p in Helpers()) using (p) KillQuietly(p);
    }

    void Tick()
    {
        // Timer callback boundary: an exception here would kill the service, and nothing restarts it before a reboot.
        try
        {
            var session = Wts.ActiveSession();
            bool running;
            // Every tick gets fresh Process objects: dispose them, or their handles pile up for the finalizer.
            var helpers = Helpers();
            try
            {
                // A helper in a switched-away session still owns that user's clipboard: only the active session's helper may live.
                foreach (var p in helpers.Where(p => p.SessionId != session)) KillQuietly(p);
                running = helpers.Any(p => p.SessionId == session);
            }
            finally { foreach (var p in helpers) p.Dispose(); }
            if (session is { } s && !running)
            {
                // Fails for a moment right after logon; cleared on success so the status page doesn't show it forever.
                try { LaunchInSession(s); running = true; _helperError = null; }
                catch (Win32Exception ex) { _helperError = $"helper: {ex.Message}"; }
            }
            WriteStatus(session, running);
        }
        catch (Exception ex) { _helperError = ex.Message; }
    }

    static List<Process> Helpers()
    {
        var self = Environment.ProcessId;
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)).Where(p => p.Id != self && p.SessionId != 0).ToList();
    }

    static void KillQuietly(Process p)
    {
        try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    static void LaunchInSession(int session)
    {
        if (!WTSQueryUserToken(session, out var token)) throw new Win32Exception();
        try
        {
            if (!CreateEnvironmentBlock(out var env, token, false)) throw new Win32Exception();
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                if (!CreateProcessAsUser(token, Exe, $"\"{Exe}\" --helper", 0, 0, false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Dir, ref si, out var pi))
                    throw new Win32Exception();
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            finally { DestroyEnvironmentBlock(env); }
        }
        finally { CloseHandle(token); }
    }

    static void ApplySettings(MachineSettings s)
    {
        const string ts = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        const string policy = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        using (var k = Registry.LocalMachine.CreateSubKey(ts))
        {
            k.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
            k.SetValue("AllowRemoteRPC", 1, RegistryValueKind.DWord);
        }
        using (var k = Registry.LocalMachine.CreateSubKey(policy))
        {
            k.SetValue("Shadow", s.Shadow, RegistryValueKind.DWord);
            if (s.Avc444 is { } avc) k.SetValue("AVC444ModePreferred", avc ? 1 : 0, RegistryValueKind.DWord);
            if (s.HardwareEncode is { } hw) k.SetValue("AVCHardwareEncodePreferred", hw ? 1 : 0, RegistryValueKind.DWord);
        }
        if (s.Fps60 is { } fps60)
            using (var k = Registry.LocalMachine.CreateSubKey(ts + @"\WinStations"))
            {
                // 15 = 1000/60 ms per frame; absent = Windows default (30 fps).
                if (fps60) k.SetValue("DWMFRAMEINTERVAL", 15, RegistryValueKind.DWord);
                else k.DeleteValue("DWMFRAMEINTERVAL", false);
            }
        // Local subnet, not a profile filter: workgroup LANs are often classified Public, but a café or the internet is not our subnet.
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        // service=: only this service (its SID, set at install) is reachable on the port, even if another process got it first.
        Netsh($"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={Wire.Port} remoteip=localsubnet service={Name}");
        // Group "Remote Desktop" incl. the Shadow rule, locale independent. Rule by rule through the firewall API, because
        // `netsh set rule group=... remoteip=` fails on this group as a whole (seen 2026-09-24). Last, so a failure here lands
        // in the status page without skipping the rest. An admin's own scope (e.g. an IP range) is left alone.
        dynamic firewall = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (dynamic rule in firewall.Rules)
        {
            if (rule.Grouping != "@FirewallAPI.dll,-28752") continue;
            rule.Enabled = true;
            if (rule.RemoteAddresses == "*") rule.RemoteAddresses = "LocalSubnet";
        }
    }

    static bool Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    void WriteStatus(int? session, bool helperRunning)
    {
        try
        {
            using var ts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services");
            var status = new AgentStatus
            {
                Version = typeof(AgentService).Assembly.GetName().Version?.ToString() ?? "",
                ActiveUser = session is { } s ? Wts.UserName(s) : null,
                HelperRunning = helperRunning,
                RdpEnabled = ts?.GetValue("fDenyTSConnections") is 0,
                RemoteRpc = ts?.GetValue("AllowRemoteRPC") is 1,
                Shadow = policy?.GetValue("Shadow") as int?,
                FirewallRule = _firewall,
                Error = _error ?? _listenError ?? _helperError,
            };
            // Tick runs every 3 s for weeks: touch the disk only when something changed. Updated = time of that change.
            if (status == _written) return;
            File.WriteAllText(Path.Combine(Dir, MachineSettings.StatusFileName), JsonSerializer.Serialize(status with { Updated = DateTime.UtcNow }, MachineSettings.Json));
            _written = status;
        }
        catch (IOException) { } // app reading it at the same moment; next tick rewrites
    }

    // ---------- network ----------
    // The service, not the helper, owns the port: the app's NTLM login must never reach a process the (possibly
    // untrusted) remote user controls, where it could be cracked offline or relayed (spec §7).

    async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Exclusive + dual-mode: nobody can bind this port next to us, on any address, IPv4 or IPv6.
            var listener = new TcpListener(IPAddress.IPv6Any, Wire.Port) { ExclusiveAddressUse = true };
            try
            {
                listener.Server.DualMode = true;
                listener.Start();
                _listenError = null;
                while (true) _ = RelayAsync(await listener.AcceptTcpClientAsync(ct), ct);
            }
            catch (OperationCanceledException) { return; }
            // Port taken, or anything else: retry. Letting a non-socket error escape would end listening for good, silently.
            catch (Exception ex) { _listenError = $"port {Wire.Port}: {ex.Message}"; }
            finally { listener.Stop(); }
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Authenticates one app connection, then pipes its decrypted bytes to and from the helper unchanged
    /// (frames, pings and all: the protocol stays between app and helper).</summary>
    async Task RelayAsync(TcpClient tcp, CancellationToken ct)
    {
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        NamedPipeServerStream? pipe = null;
        var gated = false;
        // Cancelling closes both ends, which is what actually unblocks the copies below.
        using var close = mine.Token.Register(() => { tcp.Dispose(); pipe?.Dispose(); });
        try
        {
            tcp.NoDelay = true;
            await using var neg = new NegotiateStream(tcp.GetStream());
            await neg.AuthenticateAsServerAsync(CredentialCache.DefaultNetworkCredentials, Wire.Protection, Wire.Impersonation)
                .WaitAsync(TimeSpan.FromSeconds(15), mine.Token);
            if (Wts.ActiveSession() is not { } session || SessionUser(session) is not { } user
                || !Allowed((WindowsIdentity)neg.RemoteIdentity, user)) return;

            // Newest connection wins (app reconnect after a network drop); the pipe has one instance at a time.
            try { Interlocked.Exchange(ref _activeRelay, mine)?.Cancel(); }
            catch (ObjectDisposedException) { } // it ended on its own meanwhile
            await _relayGate.WaitAsync(mine.Token);
            gated = true;

            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            // FirstPipeInstance: never join a pipe of this name someone else created. The previous connection's pipe
            // lingers until the helper closes its end, so give it a moment.
            for (var i = 0; ; i++)
            {
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(Wire.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < 10) { await Task.Delay(200, mine.Token); }
            }
            // No helper within 10 s (logon screen, helper starting): drop; the app reconnects.
            await pipe.WaitForConnectionAsync(mine.Token).WaitAsync(TimeSpan.FromSeconds(10), mine.Token);
            await Task.WhenAny(neg.CopyToAsync(pipe, mine.Token), pipe.CopyToAsync(neg, mine.Token));
        }
        // Per-connection boundary: anything a bad or dropped client causes ends this connection only.
        catch (Exception) { }
        finally
        {
            pipe?.Dispose();
            tcp.Dispose();
            Interlocked.CompareExchange(ref _activeRelay, null, mine);
            if (gated) _relayGate.Release();
        }
    }

    /// <summary>The logged-on user's own account, or a real (unfiltered) administrator.</summary>
    static bool Allowed(WindowsIdentity remote, SecurityIdentifier sessionUser) =>
        remote.User == sessionUser || new WindowsPrincipal(remote).IsInRole(WindowsBuiltInRole.Administrator);

    static SecurityIdentifier? SessionUser(int session)
    {
        if (!WTSQueryUserToken(session, out var token)) return null;
        try { using var id = new WindowsIdentity(token); return id.User; }
        finally { CloseHandle(token); }
    }

    const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("wtsapi32", SetLastError = true)] static extern bool WTSQueryUserToken(int session, out nint token);
    [DllImport("userenv", SetLastError = true)] static extern bool CreateEnvironmentBlock(out nint env, nint token, bool inherit);
    [DllImport("userenv")] static extern bool DestroyEnvironmentBlock(nint env);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUser(nint token, string app, string cmd, nint procAttr, nint threadAttr, bool inherit,
        uint flags, nint env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
}

static class Wts
{
    [StructLayout(LayoutKind.Sequential)]
    struct WTS_SESSION_INFO { public int SessionId; public nint WinStationName; public int State; }

    [DllImport("wtsapi32", SetLastError = true)] static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);
    [DllImport("wtsapi32")] static extern void WTSFreeMemory(nint memory);
    [DllImport("wtsapi32", CharSet = CharSet.Unicode)] static extern bool WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out int bytes);

    /// <summary>The session with a logged-on, active (possibly locked) user; null at the logon screen.</summary>
    public static int? ActiveSession()
    {
        if (!WTSEnumerateSessions(0, 0, 1, out var p, out var count)) return null;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(p + i * size);
                if (s.State == 0 && s.SessionId != 0) return s.SessionId;
            }
            return null;
        }
        finally { WTSFreeMemory(p); }
    }

    public static string? UserName(int session)
    {
        const int WTSUserName = 5;
        if (!WTSQuerySessionInformation(0, session, WTSUserName, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
