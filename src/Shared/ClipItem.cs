using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RdpShadow.Shared;

/// <summary>One clipboard snapshot: raw HGLOBAL formats by name plus copied files.</summary>
public sealed class ClipItem
{
    /// <summary>Format key: "#13" for predefined ids, registered name otherwise ("HTML Format").</summary>
    public List<(string Format, byte[] Data)> Formats { get; } = [];

    /// <summary>Relative paths ("dir/a.txt"); Data == null marks a directory.</summary>
    public List<(string Path, byte[]? Data)> Files { get; } = [];

    public long Size => Formats.Sum(f => (long)f.Data.Length) + Files.Sum(f => (long)(f.Data?.Length ?? 0));

    /// <summary>What a person would call "the same copy": text, else image, else everything. Used to stop echo loops.</summary>
    public string ContentHash()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var key = Formats.FirstOrDefault(f => f.Format == "#13").Data
            ?? Formats.FirstOrDefault(f => f.Format == "PNG").Data
            ?? Formats.FirstOrDefault(f => f.Format == "#8").Data;
        if (key is not null) h.AppendData(key);
        else
        {
            // No text or picture: hash everything, or all file / app-only copies would look alike and be dropped as echoes.
            foreach (var (path, data) in Files) { h.AppendData(Encoding.UTF8.GetBytes(path + "\0")); if (data is not null) h.AppendData(data); }
            foreach (var (format, data) in Formats) { h.AppendData(Encoding.UTF8.GetBytes(format + "\0")); h.AppendData(data); }
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    public string Describe()
    {
        if (Files.Count > 0) return Files.Count(f => !f.Path.Contains('/')) is var n && n == 1 ? "קובץ" : $"{n} פריטים";
        if (Formats.Any(f => f.Format is "PNG" or "#8" or "#17") && !Formats.Any(f => f.Format == "#13")) return "תמונה";
        return "טקסט";
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Formats.Count);
        foreach (var (format, data) in Formats) { w.Write(format); w.Write(data.Length); w.Write(data); }
        w.Write(Files.Count);
        foreach (var (path, data) in Files)
        {
            w.Write(path);
            w.Write(data?.Length ?? -1);
            if (data is not null) w.Write(data);
        }
    }

    public static ClipItem Read(BinaryReader r)
    {
        var item = new ClipItem();
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
            item.Formats.Add((r.ReadString(), r.ReadBytes(r.ReadInt32())));
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            var path = r.ReadString();
            var len = r.ReadInt32();
            item.Files.Add((path, len < 0 ? null : r.ReadBytes(len)));
        }
        return item;
    }
}
