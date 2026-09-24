using System.IO;
using System.Net.Security;
using System.Security.Principal;

namespace RdpShadow.Shared;

/// <summary>App ↔ agent protocol: NegotiateStream (Kerberos/NTLM, encrypted), then [int length][byte type][payload] frames.</summary>
public static class Wire
{
    public const int Port = 47800;
    /// <summary>Local pipe the service relays an authenticated app connection over to the helper.</summary>
    public const string Pipe = "RdpShadowAgent";
    public const int MaxFrame = 512 * 1024 * 1024; // hard ceiling against a corrupt length; real limit is the user's file size setting

    public enum Msg : byte { Clip = 1, Ping = 2, Hello = 3 }

    const int Chunk = 1024 * 1024; // progress granularity; the byte stream is the same either way

    /// <param name="progress">(bytes done, total) after each chunk of the payload.</param>
    public static async Task SendAsync(Stream s, Msg type, ReadOnlyMemory<byte> payload, CancellationToken ct, Action<long, long>? progress = null)
    {
        var header = new byte[5];
        BitConverter.TryWriteBytes(header, payload.Length);
        header[4] = (byte)type;
        await s.WriteAsync(header, ct);
        for (var done = 0; done < payload.Length;)
        {
            var n = Math.Min(Chunk, payload.Length - done);
            await s.WriteAsync(payload.Slice(done, n), ct);
            done += n;
            progress?.Invoke(done, payload.Length);
        }
        await s.FlushAsync(ct);
    }

    /// <param name="idle">Max wait for the next frame to start; a large frame in flight is not cut off.</param>
    /// <param name="progress">(bytes done, total) after each chunk of the payload.</param>
    public static async Task<(Msg Type, byte[] Payload)> ReceiveAsync(Stream s, CancellationToken ct, TimeSpan? idle = null, Action<long, long>? progress = null)
    {
        var header = new byte[5];
        var read = s.ReadExactlyAsync(header, ct).AsTask();
        await (idle is { } t ? read.WaitAsync(t, ct) : read);
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > MaxFrame) throw new InvalidDataException($"frame length {length}");
        var payload = new byte[length];
        for (var done = 0; done < length;)
        {
            var n = Math.Min(Chunk, length - done);
            await s.ReadExactlyAsync(payload.AsMemory(done, n), ct);
            done += n;
            progress?.Invoke(done, length);
        }
        return ((Msg)header[4], payload);
    }

    /// <summary>Serialized item. Sized up front and handed out without ToArray: a 100MB item would otherwise pass
    /// through ~2x its size in growing buffers plus a full extra copy.</summary>
    public static ReadOnlyMemory<byte> Pack(ClipItem item)
    {
        // Payload plus headroom for names and length prefixes; a rare longer path just lets the stream grow.
        var ms = new MemoryStream((int)Math.Min(item.Size + 8 + (item.Formats.Count + item.Files.Count) * 1024L, Array.MaxLength));
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) item.Write(w);
        return ms.GetBuffer().AsMemory(0, (int)ms.Length);
    }

    public static ClipItem Unpack(byte[] payload)
    {
        using var r = new BinaryReader(new MemoryStream(payload));
        return ClipItem.Read(r);
    }

    public const ProtectionLevel Protection = ProtectionLevel.EncryptAndSign;
    public const TokenImpersonationLevel Impersonation = TokenImpersonationLevel.Identification;
}
