<div align="center">

<img src="src/RdpShadow.App/Assets/icon_128.png" width="96" alt="RDP Shadow Studio icon" />

# RDP Shadow Studio

**Built-in Windows RDP Shadow, embedded in one app, with working two-way clipboard sync.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-WPF%20Fluent-5C2D91)
![Dependencies](https://img.shields.io/badge/third--party%20deps-none-2EA043)

</div>

---

RDP Shadow Studio is a Windows desktop app. It connects to a user's **live session** on another computer through the built-in RDP Shadow feature (`mstsc /shadow`) and shows it inside the app window. It also adds three things Shadow doesn't have:

- **Two-way clipboard sync.** Copy on one machine and paste on the other, as in AnyDesk or TeamViewer. Every synced item also shows up in **Win+V** history on both machines.
- **One-click machine preparation.** It applies the registry, Group Policy and firewall settings that Shadow needs, with no manual `regedit` or `gpedit`.
- **Display quality controls.** Frame rate, H.264/AVC 4:4:4 and hardware encoding, plus live brightness and contrast.

The UI is in Hebrew with a right-to-left layout and follows the Windows 11 Fluent look, including automatic light and dark themes.

## Why this exists

A normal RDP session carries the clipboard over the `cliprdr` virtual channel, which `rdpclip.exe` handles. **A Shadow connection has no such channel.** The viewer gets only the picture, keyboard and mouse, so copy and paste between the machines is unreliable or doesn't work at all. Third-party clipboard sync tools and running a second RDP session alongside the Shadow both failed to fix this.

RDP Shadow Studio takes the same approach as commercial remote-support tools. A small **agent** runs inside the remote user's session and moves the clipboard over its own authenticated, encrypted channel. The channel is tied to the Shadow connection: it runs only while the Shadow is open and visible.

## Features

### Embedded Shadow session
- One-click **View** or **Control** from the computer list. You can switch modes from the session toolbar at any time.
- The active session is found automatically through the WTS API (`WTSEnumerateSessions`). A session list appears only when there is more than one.
- The `mstsc` viewer window is adopted into the app. It has no frame, resizes with the app window, and smart sizing is always on.
- **Auto-reconnect.** If the connection drops (network loss, or the remote user logs off and back on), the app retries every 3 seconds for up to a minute. Each retry finds the new active session and reconnects in the same mode.
- **Brightness and contrast** overlay for the remote screen, using the Windows Magnification API. Clicks pass through it, and it does nothing at default values.

### Clipboard sync
| Content | Notes |
|---|---|
| Plain and rich text | Keeps formatting from Word, Excel and browsers (all HGLOBAL formats except a blocklist) |
| Images and screenshots | PNG and DIB |
| Files and folders | Copied in full to the other side, then pasted as usual. Adjustable size limit (default 100 MB, maximum 500 MB) |

- Sync can be **two-way**, **remote → me only** or **me → remote only**.
- Change detection is event-driven through `AddClipboardFormatListener`, with no polling.
- **No sync loops.** Every write carries a private marker format, and both sides also compare a hash of the full content.
- **Pause on minimize.** When the window is restored, anything copied locally in the meantime is sent right away.
- **Progress display** for items over 4 MB (sending or receiving X/Y MB).
- Content that password managers mark as private (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0`, `Clipboard Viewer Ignore`) is never sent.

### Remote machine management
- **Remote agent install and update** over SMB and the remote Service Control Manager, with nothing to do on the remote machine (tested at about 1.4 s). When a newer agent build is next to the app, an **Update Agent** button appears.
- The **Prepare machine** page shows each item's status and has a **Fix all** button:

| Setting | Where |
|---|---|
| Remote Desktop enabled | `fDenyTSConnections = 0` |
| Remote session listing | `AllowRemoteRPC = 1` |
| Shadow policy | `Shadow` = 1–4 (control/view, with or without consent). Default: full control, no prompt |
| Firewall | Remote Desktop, Shadow and the agent port, limited to `localsubnet` |
| Win+V history for the remote user | `EnableClipboardHistory = 1` |
| RDP's built-in clipboard | `fDisableClip = 1` (see [Known limitations](#known-limitations)) |

- **Quality profiles** (Economy, Balanced, Maximum) plus advanced switches: 60 fps (`DWMFRAMEINTERVAL`), AVC 4:4:4 and hardware encoding.
- **Credentials per machine** for workgroup setups, saved in Windows Credential Manager.

## Architecture

```
┌──────────── Your computer ────────────┐              ┌──────────────── Remote computer ────────────────┐
│ RdpShadow.App (WPF, .NET 10)          │              │                                                 │
│  ├─ ShadowHost ◄──────────────────────┼─ RDP Shadow ─┼─► user session (image + input)                  │
│  │   adopts the mstsc viewer window   │              │                                                 │
│  └─ ClipboardLink ◄───────────────────┼─ TCP 47800 ──┼─► RdpShadow.Agent  (Windows service, SYSTEM)    │
│      AddClipboardFormatListener       │  Negotiate-  │     • owns the port, authenticates, decrypts    │
│                                       │  Stream      │     • applies machine settings, writes status   │
│  RemoteAgent ─────────────────────────┼─ SMB + SCM ──┼─►   • launches the helper in the active session │
│   (install / update / settings.json)  │              │              │ named pipe (ACL-restricted)      │
└───────────────────────────────────────┘              │              ▼                                  │
                                                       │   RdpShadow.Agent --helper  (in user's session) │
                                                       │     reads/writes the user's clipboard → Win+V   │
                                                       └─────────────────────────────────────────────────┘
```

- **Why embed the mstsc window instead of ActiveX?** `mstsc /shadow` gets its invitation through an undocumented RPC call and shows it in the Desktop Sharing viewer (`SrApiViewerAxContainerClass`), not in the regular RDP control. No public API accepts that invitation. So the app starts `mstsc`, waits for the viewer window, removes its frame and re-parents it with `SetParent`. A `SetWinEventHook` then keeps it sized to the app window.
- **Why a service *and* a helper?** Only a process inside the user's session can access that user's clipboard. The helper is launched in the session with `WTSQueryUserToken` + `CreateProcessAsUser`. The network port, however, belongs to the SYSTEM service (see [Security](#security)).
- **Protocol:** `NegotiateStream` (Kerberos/NTLM, `EncryptAndSign`) carrying `[int32 length][byte type][payload]` frames. Payloads are sent in 1 MB chunks so progress can be reported. TCP keepalive is on, so a link that drops mid-transfer doesn't hang.

### Project layout

```
RdpShadow.slnx
├─ src/RdpShadow.App/       WPF app
│   ├─ MainWindow.xaml(.cs)   pages: Computers, Clipboard, Quality, Prepare machine, Shadow view
│   ├─ ShadowHost.cs          mstsc launch, window adoption, resize hook, smart sizing
│   ├─ ClipboardLink.cs       app side of the clipboard channel
│   ├─ RemoteAgent.cs         remote install/update over SMB + remote SCM
│   ├─ ColorFilter.cs         brightness/contrast overlay (Magnification API)
│   ├─ Credentials.cs         Windows Credential Manager
│   └─ Native.cs              P/Invoke (WTS, user32)
├─ src/RdpShadow.Agent/     single self-contained exe
│   ├─ AgentService.cs        Windows service: settings, firewall, helper lifecycle, TCP → pipe relay
│   └─ Helper.cs              per-session clipboard helper (--helper)
├─ src/Shared/              linked into both projects
│   ├─ ClipboardWatcher.cs    listener, format filtering, loop prevention
│   ├─ ClipItem.cs            clipboard item serialization
│   ├─ Wire.cs                framing and protocol constants
│   └─ AgentConfig.cs         settings.json / status.json / link config
├─ tools/IconBuilder/       renders the app icon (app.ico + PNG sizes) with WPF
└─ build.ps1                builds everything into .\dist
```

## Requirements

**Your computer**
- Windows 10/11 with the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). To build from source you need the .NET 10 SDK.

**Remote computers**
- Windows 10/11 **Pro or Enterprise** (Home has no RDP server).
- A user logged in (Shadow can't attach to the logon screen).
- Local administrator rights for the account you connect with.
- Reachable ports: **3389** (RDP), **445** (SMB, for installing and updating the agent), **47800** (agent).
- No .NET is needed. The agent is a self-contained single-file exe.

## Getting started

### Build

```powershell
git clone <repo-url>
cd "RDP Shadow"
.\build.ps1
```

Output:
```
dist\RdpShadow.App.exe          the app
dist\Agent\RdpShadow.Agent.exe  the agent, installed from here onto remote machines
```

The build stops on the first failure, so `dist` never mixes old and new binaries.

### First connection

1. Run `dist\RdpShadow.App.exe`, enter an IP address or computer name, and click **Add computer** (הוסף מחשב).
2. **Workgroup machines:** open **Prepare machine** (הכנת מחשב) and save the remote local administrator's credentials. You can also add them beforehand with `cmdkey /add:<host> /user:<host>\<admin> /pass`. **Domain machines** use your current Kerberos identity.
3. Click **Fix all** (תקן הכל). This installs the agent and applies all settings.
4. Back on **Computers**, click **View** (צפייה) or **Control** (שליטה).

The computer list checks each machine every 10 seconds on TCP 3389. ICMP is often blocked, so ping isn't used. The list shows whether the machine responds, whether the agent is installed, and whether an agent update is available.

### Uninstalling the agent

The app has no uninstall button yet. From an elevated prompt:

```powershell
sc.exe \\<host> stop RdpShadowAgent
sc.exe \\<host> delete RdpShadowAgent
Remove-Item "\\<host>\C$\Program Files\RdpShadow Agent" -Recurse
```

## Security

- **Authentication:** Windows authentication through `NegotiateStream` (Kerberos in a domain, NTLM in a workgroup). Only the logged-in user or a local administrator is authorized. All traffic is encrypted and signed.
- **The port belongs to the SYSTEM service, not the helper.** The helper runs as the remote user. If it listened on the port, that user could replace it and capture the administrator's NTLM exchange for offline cracking or relay. The service binds 47800 exclusively (IPv4 and IPv6) and passes decrypted bytes to the helper over a local named pipe. The pipe is restricted to SYSTEM and the logged-in user and uses `FirstPipeInstance`. The helper connects at *Identification* level and checks that the server is in session 0.
- **Firewall:** rules are limited to `localsubnet`, because workgroup networks are often classified as Public. The agent rule is bound to the service SID (`service=RdpShadowAgent`), so another process that grabs the port can't be reached from the network. Existing admin-defined IP ranges on the Remote Desktop rules are kept.
- **Incoming data is untrusted.** Incoming file paths can't leave the temp folder. Only formats the receiving side would send itself are written to the clipboard: no raw `CF_HDROP` (which would allow UNC paths and an NTLM leak on paste), no GDI handles, no OLE or Shell formats.
- **Nothing is written to disk** except copied files. These go to `%TEMP%\RdpShadow`, and any older than a day are deleted on startup and on every file paste.
- Password-manager content is never sent (see above).

## Known limitations

- **Files don't appear in Win+V.** Windows history keeps only text, HTML and images, up to about 4 MB per item.
- **`fDisableClip=1` disables RDP's own clipboard** on the remote machine, including for normal RDP sessions. This is required: `mstsc /shadow` has a partial clipboard redirection of its own. After each item the helper writes, it takes the local clipboard back with a copy flagged `CanIncludeInClipboardHistory=0`, so the item disappears from Win+V.
- Firewall rules are limited to the local subnet. Connections from another network (VPN, RD Gateway) are blocked.
- No Ctrl+Alt+Del button. Press **Ctrl+Alt+End** inside the Shadow view instead.
- Only one connection at a time. No uninstall from the app.
- The "Image quality" (`ImageQuality`) setting was left out because its values couldn't be verified for Shadow.
- On the sending side, transfer progress counts bytes handed to the connection, not bytes received by the other side.

## Documentation

The full design specification, decision log and change history are in [`project_spec.md`](project_spec.md) (Hebrew).
