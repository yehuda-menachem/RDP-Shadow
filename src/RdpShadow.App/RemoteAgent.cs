using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using RdpShadow.Shared;

namespace RdpShadow.App;

/// <summary>
/// Installs and configures the agent on a remote machine the PsExec way: files over the C$ admin share, service via
/// the remote Service Control Manager. Both ride the SMB session, so credentials saved with cmdkey are used.
/// </summary>
static class RemoteAgent
{
    const string ServiceName = "RdpShadowAgent";
    const string Folder = @"Program Files\RdpShadow Agent";
    const string ExeName = "RdpShadow.Agent.exe";

    static string Unc(string host, string file = "") => $@"\\{host}\C$\{Folder}\{file}";

    /// <summary>Agent binary shipped next to the app (dist\Agent\RdpShadow.Agent.exe).</summary>
    public static string LocalAgentExe => Path.Combine(AppContext.BaseDirectory, "Agent", ExeName);

    public static bool IsInstalled(string host)
    {
        try { using var sc = new ServiceController(ServiceName, host); _ = sc.Status; return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The agent next to the app is a newer build than the installed one. File.Copy keeps the write time, so the
    /// installed exe carries the time of the build it came from; no version numbers to maintain.
    /// </summary>
    public static bool IsOutdated(string host)
    {
        try
        {
            var remote = File.GetLastWriteTimeUtc(Unc(host, ExeName)); // missing file: year 1601
            return remote.Year > 1601 && File.GetLastWriteTimeUtc(LocalAgentExe) - remote > TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static AgentStatus? ReadStatus(string host)
    {
        try { return JsonSerializer.Deserialize<AgentStatus>(File.ReadAllText(Unc(host, MachineSettings.StatusFileName))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static MachineSettings ReadSettings(string host) => MachineSettings.Load(Unc(host, MachineSettings.FileName));

    /// <summary>Copies the agent, writes settings, (re)creates and starts the service. Also used to update.</summary>
    public static void Install(string host, MachineSettings settings)
    {
        if (!File.Exists(LocalAgentExe)) throw new FileNotFoundException("קובץ ה-Agent לא נמצא ליד האפליקציה", LocalAgentExe);
        var scm = OpenSCManager(host, null, SC_MANAGER_ALL_ACCESS);
        if (scm == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "אין גישה למנהל השירותים במחשב המרוחק");
        try
        {
            var service = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
            try
            {
                if (service != 0) Stop(host);

                Directory.CreateDirectory(Unc(host));
                CopyWithRetry(LocalAgentExe, Unc(host, ExeName)); // helper may take a moment to release the exe after stop
                settings.Save(Unc(host, MachineSettings.FileName));

                if (service == 0)
                {
                    service = CreateService(scm, ServiceName, "RdpShadow Agent", SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                        SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, $"\"C:\\{Folder}\\{ExeName}\"", null, 0, null, null, null);
                    if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "יצירת השירות נכשלה");
                }
                // A service SID lets the agent's firewall rule admit only this service on the port (spec §7).
                var sidType = SERVICE_SID_TYPE_UNRESTRICTED;
                if (!ChangeServiceConfig2(service, SERVICE_CONFIG_SERVICE_SID_INFO, ref sidType))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "הגדרת השירות נכשלה");
            }
            finally { if (service != 0) CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
        Start(host);
    }

    /// <summary>Saves machine settings and restarts the service so it applies them.</summary>
    public static void Apply(string host, MachineSettings settings)
    {
        settings.Save(Unc(host, MachineSettings.FileName));
        Stop(host);
        Start(host);
    }

    static void Stop(string host)
    {
        using var sc = new ServiceController(ServiceName, host);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    static void Start(string host)
    {
        using var sc = new ServiceController(ServiceName, host);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    static void CopyWithRetry(string from, string to)
    {
        for (var i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); }
        }
    }

    const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SERVICE_ALL_ACCESS = 0xF01FF, SERVICE_WIN32_OWN_PROCESS = 0x10,
        SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1, SERVICE_CONFIG_SERVICE_SID_INFO = 5, SERVICE_SID_TYPE_UNRESTRICTED = 1;

    [DllImport("advapi32", SetLastError = true)] static extern bool ChangeServiceConfig2(nint service, uint level, ref uint info);

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenSCManager(string machine, string? database, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenService(nint scm, string name, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateService(nint scm, string name, string display, uint access, uint type, uint start, uint error,
        string path, string? group, nint tag, string? deps, string? account, string? password);
    [DllImport("advapi32")] static extern bool CloseServiceHandle(nint h);
}
