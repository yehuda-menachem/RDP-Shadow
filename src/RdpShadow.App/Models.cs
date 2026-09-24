using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpShadow.App;

public enum ClipDirection { Both, ToMe, ToRemote }

public sealed record ClipSettings
{
    public bool Enabled { get; init; } = true;
    public ClipDirection Direction { get; init; } = ClipDirection.Both;
    public bool Text { get; init; } = true;
    public bool Images { get; init; } = true;
    public bool Files { get; init; } = true;
    public int MaxMegabytes { get; init; } = 100;
}

/// <summary>Local app settings, %AppData%\RdpShadow\settings.json.</summary>
public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpShadow", "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<string> Computers { get; set; } = [];
    public ClipSettings Clipboard { get; set; } = new();
    public int Brightness { get; set; }
    public int Contrast { get; set; } = 100;

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>One row on the computers page; status filled in by a background probe.</summary>
public sealed class Computer(string host) : INotifyPropertyChanged
{
    public string Host { get; } = host;

    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string ShadowText { get => _shadowText; set => Set(ref _shadowText, value); }
    public string ShadowColor { get => _shadowColor; set => Set(ref _shadowColor, value); }
    public string AgentText { get => _agentText; set => Set(ref _agentText, value); }
    public string AgentColor { get => _agentColor; set => Set(ref _agentColor, value); }
    public bool CanConnect { get => _canConnect; set => Set(ref _canConnect, value); }
    public bool NeedsAgent { get => _needsAgent; set => Set(ref _needsAgent, value); }
    public bool AgentOutdated { get => _agentOutdated; set => Set(ref _agentOutdated, value); }

    string _detail = "בודק...", _shadowText = "בודק", _shadowColor = Gray, _agentText = "", _agentColor = Gray;
    bool _canConnect, _needsAgent, _agentOutdated;

    public const string Green = "#6CCB5F", Yellow = "#FCE100", Gray = "#9A9A9A";

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
