using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using RdpShadow.Shared;

namespace RdpShadow.App;

/// <summary>
/// App side of clipboard sync with one remote agent. Reconnects on its own while alive; paused while the shadow
/// window is minimized (spec §10). Events are raised on thread-pool threads.
/// </summary>
sealed class ClipboardLink : IAsyncDisposable
{
    public enum State { Connecting, Connected, Unavailable }

    readonly string _host;
    readonly ClipboardWatcher _watcher;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly Task _loop;
    NegotiateStream? _stream;
    string? _lastHash;
    bool _paused;
    uint _seqAtPause;
    ClipSettings _settings;
    ClipItem? _reclaim; // last local copy we sent, put back once if the shadow's own clipboard channel takes it over

    public event Action<State, string?>? StateChanged;
    /// <summary>(direction, description): "in" when a remote copy landed here, "out" when a local copy was sent.</summary>
    public event Action<string, string>? Synced;
    /// <summary>(direction, bytes done, total) for items worth a progress bar; done == total also when the transfer failed.</summary>
    public event Action<string, long, long>? Transfer;
    const long ProgressMin = 4 * 1024 * 1024; // smaller items arrive before a bar could be seen

    public ClipboardLink(string host, ClipboardWatcher watcher, ClipSettings settings)
    {
        _host = host;
        _watcher = watcher;
        _settings = settings;
        Watch();
        _watcher.Reclaimed += OnReclaimed;
        _loop = Task.Run(RunAsync);
    }

    // mstsc /shadow relays the helper's write back here as CanIncludeInClipboardHistory=0, and Win+V then drops the user's
    // copy. Writing it again as ours puts it back in history; mstsc passes that on to the remote, which ignores our marker.
    // ponytail: once per item, so a relay that ever echoed our rewrite too would stop after one round.
    async void OnReclaimed()
    {
        if (Interlocked.Exchange(ref _reclaim, null) is not { } item) return;
        try { await _watcher.WriteAsync(item); }
        catch (IOException) { } // clipboard held by another app: the copy stays pasteable via mstsc, just not in Win+V
    }

    /// <summary>Listen to local copies only while they would be sent: the watcher skips reading when nobody listens.</summary>
    void Watch()
    {
        _watcher.Copied -= OnLocalCopy;
        if (CanSend) _watcher.Copied += OnLocalCopy;
    }

    public void UpdateSettings(ClipSettings settings)
    {
        _settings = settings;
        Watch();
        _ = SendConfigAsync();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        // The remote clipboard may change unseen while paused, so "already synced" no longer holds.
        if (paused) (_seqAtPause, _lastHash) = (ClipboardWatcher.Sequence, null);
        Watch();
        _ = SendConfigAsync();
        // Back from minimized: whatever was copied here meanwhile should be pasteable over there right away.
        if (!paused && CanSend && ClipboardWatcher.Sequence != _seqAtPause)
            _ = Task.Run(async () => { if (await _watcher.ReadCurrentAsync() is { } item) await SendClipAsync(item); });
    }

    bool CanSend => _settings.Enabled && !_paused && _settings.Direction != ClipDirection.ToMe;
    bool CanReceive => _settings.Enabled && !_paused && _settings.Direction != ClipDirection.ToRemote;

    async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            StateChanged?.Invoke(State.Connecting, null);
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(_host, Wire.Port, _cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await using var neg = new NegotiateStream(tcp.GetStream());
                // Made-up SPN on purpose: Kerberos finds no such service and Negotiate falls back to NTLM, which the
                // helper (running as a user, not the machine account) can validate. cmdkey-saved credentials apply.
                await neg.AuthenticateAsClientAsync(CredentialCache.DefaultNetworkCredentials, $"RdpShadowAgent/{_host}",
                    Wire.Protection, Wire.Impersonation).WaitAsync(TimeSpan.FromSeconds(15), _cts.Token);
                // The idle timeout below only guards the gap between frames; keepalive catches a link that dies mid-frame.
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
                _stream = neg;
                _lastHash = null;
                await SendConfigAsync();
                StateChanged?.Invoke(State.Connected, null);

                while (true)
                {
                    // Helper pings every 5 s; silence for 15 s means the link is dead.
                    // A link dropped mid-frame ends in StateChanged(Unavailable), which also clears the bar.
                    var (type, payload) = await Wire.ReceiveAsync(neg, _cts.Token, TimeSpan.FromSeconds(15), (done, total) => Report("in", done, total));
                    if (type != Wire.Msg.Clip || !CanReceive) continue;
                    var item = Wire.Unpack(payload);
                    _lastHash = item.ContentHash();
                    try { await _watcher.WriteAsync(item); }
                    catch (IOException) { _lastHash = null; continue; } // clipboard held by another app or disk full: skip this item only
                    Synced?.Invoke("in", item.Describe());
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            // Anything else (e.g. a malformed item from the remote side) must drop this connection, not end the loop for good.
            catch (Exception ex)
            {
                _stream = null;
                StateChanged?.Invoke(State.Unavailable, Explain(ex));
            }
            try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    static string Explain(Exception ex) => ex switch
    {
        SocketException or TimeoutException => "ה-Agent לא עונה (לא מותקן, או שאין משתמש מחובר)",
        System.Security.Authentication.AuthenticationException => "ההזדהות מול ה-Agent נכשלה",
        _ => "החיבור ל-Agent נותק",
    };

    void OnLocalCopy(ClipItem item)
    {
        if (CanSend) _ = SendClipAsync(item);
    }

    async Task SendClipAsync(ClipItem item)
    {
        var hash = item.ContentHash();
        if (hash == _lastHash) return; // just arrived from the remote side
        _lastHash = hash;
        // Files never reach Win+V, and rewriting them would copy them into temp for nothing.
        _reclaim = item.Files.Count == 0 ? item : null;
        var payload = Wire.Pack(item);
        var sent = await SendAsync(Wire.Msg.Clip, payload, (done, total) => Report("out", done, total));
        Report("out", payload.Length, payload.Length); // a send that failed midway must not leave the bar up
        if (sent) Synced?.Invoke("out", item.Describe());
    }

    void Report(string direction, long done, long total)
    {
        if (total >= ProgressMin) Transfer?.Invoke(direction, done, total);
    }

    Task SendConfigAsync()
    {
        var config = new LinkConfig
        {
            Send = CanReceive, // the helper sends only what we would accept
            MaxBytes = _settings.MaxMegabytes * 1024L * 1024,
            Text = _settings.Text, Images = _settings.Images, Files = _settings.Files,
        };
        return SendAsync(Wire.Msg.Hello, JsonSerializer.SerializeToUtf8Bytes(config));
    }

    async Task<bool> SendAsync(Wire.Msg type, ReadOnlyMemory<byte> payload, Action<long, long>? progress = null)
    {
        var stream = _stream;
        if (stream is null) return false;
        await _sendLock.WaitAsync();
        try { await Wire.SendAsync(stream, type, payload, _cts.Token, progress); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { return false; }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Copied -= OnLocalCopy;
        _watcher.Reclaimed -= OnReclaimed;
        _cts.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
