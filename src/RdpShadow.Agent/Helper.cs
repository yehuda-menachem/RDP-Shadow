using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RdpShadow.Shared;

namespace RdpShadow.Agent;

/// <summary>
/// Runs in the user's session and owns the clipboard. The network side belongs to the service (SYSTEM): the app's
/// login must never reach a process the remote user controls (spec §7). The service relays each authenticated app
/// connection to us over a local pipe.
/// </summary>
static class Helper
{
    static readonly SemaphoreSlim SendLock = new(1, 1);
    static Stream? _client;
    static LinkConfig _config = new();
    static string? _lastHash;

    public static async Task RunAsync()
    {
        if (MachineSettings.Load(Path.Combine(AppContext.BaseDirectory, MachineSettings.FileName)).ClipboardHistory)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Clipboard"))
                k.SetValue("EnableClipboardHistory", 1, RegistryValueKind.DWord);

        using var watcher = new ClipboardWatcher(Path.Combine(Path.GetTempPath(), "RdpShadow"));

        while (true)
        {
            // Identification only: whoever serves the pipe may learn who we are, never act as us.
            var pipe = new NamedPipeClientStream(".", Wire.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                // The pipe exists only while an app is connected; poll gently instead of ConnectAsync's busy wait.
                await pipe.ConnectAsync(0);
                // Session 0 = a service. Another logged-on user could have created a pipe by this name; don't talk to it.
                if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) || session != 0) throw new IOException("pipe not served by the service");
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(500);
                continue;
            }
            await ServeAsync(pipe, watcher);
        }
    }

    static async Task ServeAsync(NamedPipeClientStream pipe, ClipboardWatcher watcher)
    {
        _client = pipe;
        _lastHash = null;
        try
        {
            _ = PingLoop(pipe);
            while (true)
            {
                var (type, payload) = await Wire.ReceiveAsync(pipe, default);
                switch (type)
                {
                    case Wire.Msg.Hello:
                        _config = JsonSerializer.Deserialize<LinkConfig>(payload) ?? new();
                        watcher.MaxBytes = _config.MaxBytes;
                        watcher.IncludeText = _config.Text;
                        watcher.IncludeImages = _config.Images;
                        watcher.IncludeFiles = _config.Files;
                        // Subscribed only while the app takes our copies: with nobody listening the watcher doesn't read.
                        watcher.Copied -= OnCopied;
                        if (_config.Send) watcher.Copied += OnCopied;
                        break;
                    case Wire.Msg.Clip:
                        var item = Wire.Unpack(payload);
                        _lastHash = item.ContentHash();
                        try { await watcher.WriteAsync(item); }
                        catch (IOException) { _lastHash = null; } // clipboard held by another app or disk full: skip this item only
                        break;
                }
            }
        }
        // Per-connection boundary: anything a bad or dropped client causes ends this connection only.
        catch (Exception) { }
        finally
        {
            watcher.Copied -= OnCopied;
            _client = null;
            await pipe.DisposeAsync();
        }
    }

    static void OnCopied(ClipItem item) => _ = SendAsync(item);

    static async Task SendAsync(ClipItem item)
    {
        var client = _client;
        if (client is null || !_config.Send) return;
        var hash = item.ContentHash();
        if (hash == _lastHash) return; // echo of what the app just sent us
        _lastHash = hash;
        await SendAsync(client, Wire.Msg.Clip, Wire.Pack(item));
    }

    static async Task SendAsync(Stream client, Wire.Msg type, ReadOnlyMemory<byte> payload)
    {
        await SendLock.WaitAsync();
        try { await Wire.SendAsync(client, type, payload, default); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { SendLock.Release(); }
    }

    // Keeps NAT/firewall state alive and lets the app notice a dead agent within seconds.
    static async Task PingLoop(Stream client)
    {
        while (_client == client)
        {
            await Task.Delay(5000);
            if (_client == client) await SendAsync(client, Wire.Msg.Ping, ReadOnlyMemory<byte>.Empty);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetNamedPipeServerSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);
}
