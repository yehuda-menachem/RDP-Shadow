using System.IO;
using System.Text.Json;

namespace RdpShadow.Shared;

/// <summary>Machine settings the app writes to the agent folder over SMB; the service applies them on start.</summary>
public sealed record MachineSettings
{
    public const string FileName = "settings.json";
    public const string StatusFileName = "status.json";

    /// <summary>Shadow policy: 1 control+consent, 2 control no consent, 3 view+consent, 4 view no consent.</summary>
    public int Shadow { get; init; } = 2;
    // Quality: null = leave the machine as it is. Only the quality page sets these.
    public bool? Fps60 { get; init; }
    public bool? Avc444 { get; init; }
    public bool? HardwareEncode { get; init; }
    public bool ClipboardHistory { get; init; } = true;

    public static MachineSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<MachineSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}

/// <summary>What the service reports back (status.json next to the agent), read by the app's "prepare" page.</summary>
public sealed record AgentStatus
{
    public string Version { get; init; } = "";
    /// <summary>When the status last changed (written only on change).</summary>
    public DateTime Updated { get; init; }
    public string? ActiveUser { get; init; }
    public bool HelperRunning { get; init; }
    public bool RdpEnabled { get; init; }
    public bool RemoteRpc { get; init; }
    public int? Shadow { get; init; }
    public bool FirewallRule { get; init; }
    public string? Error { get; init; }
}

/// <summary>Sent app → helper on connect and whenever clipboard settings or pause state change.</summary>
public sealed record LinkConfig
{
    /// <summary>False while the app window is minimized or direction is "only to remote": helper keeps its copies.</summary>
    public bool Send { get; init; } = true;
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;
    public bool Text { get; init; } = true;
    public bool Images { get; init; } = true;
    public bool Files { get; init; } = true;
}
