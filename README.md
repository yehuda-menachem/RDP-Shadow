<div align="center">

<img src="src/RdpShadow.App/Assets/icon_128.png" width="96" alt="RDP Shadow icon" />

### RDP Shadow
**Windows' built-in RDP Shadow, embedded in one app, with two-way clipboard sync.**<br>
A WPF desktop app that views or controls the live session on another Windows PC through `mstsc /shadow`,<br>
and adds what Shadow lacks: clipboard sync, one-click machine preparation and display-quality controls.

<br>

<a href="#-quick-start"><img src="https://img.shields.io/badge/🚀_Quick_Start-06B6D4?style=for-the-badge&logoColor=white" alt="Quick Start"></a>
<a href="#-features"><img src="https://img.shields.io/badge/✨_Features-D98324?style=for-the-badge&logoColor=white" alt="Features"></a>
<a href="#-architecture"><img src="https://img.shields.io/badge/🧠_Architecture-0D1117?style=for-the-badge&logoColor=white" alt="Architecture"></a>
<a href="#-security"><img src="https://img.shields.io/badge/🔒_Security-22C55E?style=for-the-badge&logoColor=white" alt="Security"></a>

<br><br>

![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows_x64-0078D4?style=flat-square)
![UI](https://img.shields.io/badge/UI-WPF_Fluent-5C2D91?style=flat-square)
![Language](https://img.shields.io/badge/UI_language-Hebrew_(RTL)-0088CC?style=flat-square)
![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)

</div>

---

## 📑 Table of Contents

<table>
<tr>
<td valign="top" width="33%">

**Getting Started**
- [✨ Features](#-features)
- [🧠 Architecture](#-architecture)
- [🚀 Quick Start](#-quick-start)
- [🎮 Interactive Workflow](#-interactive-workflow)

</td>
<td valign="top" width="33%">

**Operations**
- [📦 Installing and Deploying](#-installing-and-deploying)
- [🧱 Project Structure](#-project-structure)
- [🔧 Configuration Matrix](#-configuration-matrix)

</td>
<td valign="top" width="33%">

**Reference**
- [🔒 Security](#-security)
- [💡 Design Decisions](#-design-decisions--reliability)
- [🚧 Known Limitations](#-known-limitations)
- [📜 License & Credits](#-license--credits)

</td>
</tr>
</table>

---

## ✨ Features

<table>
<tr>
<td width="33%" valign="top">

### 🖥 Embedded Shadow
Starts `mstsc /shadow`, strips the viewer window's frame and re-parents it into the app.
View or Control, switchable from the session bar.

</td>
<td width="33%" valign="top">

### 🔎 Automatic Session Lookup
Finds the active remote session through `WTSEnumerateSessions`. Falls back to
session 1 when remote session listing (`AllowRemoteRPC`) is off.

</td>
<td width="33%" valign="top">

### 🔁 Auto-Reconnect
If an established shadow drops, it retries every 3 s for one minute, finding the
active session again each time. A drop within 10 s of opening is treated as a refusal.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### 📋 Two-Way Clipboard
Text with formatting (every HGLOBAL format minus a blocklist), images and files/folders.
Direction: both, remote→me, or me→remote. Synced items land in Win+V history.

</td>
<td width="33%" valign="top">

### 🔄 No Sync Loops
Every write carries a private marker format, and both sides compare a SHA-256 hash of
the content, so an item never bounces back.

</td>
<td width="33%" valign="top">

### 🙈 Private Copies Stay Private
Copies flagged `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`
or `CanIncludeInClipboardHistory=0` (password managers) are never sent.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### 📦 Remote Agent Install
Copies the agent over the `C$` share and creates the service through the remote
Service Control Manager. An **Update Agent** button appears when a newer build ships with the app.

</td>
<td width="33%" valign="top">

### 🛠 Prepare Machine
Shows RDP, remote session listing, firewall and Shadow-policy status from the agent,
and fixes all of them in one click.

</td>
<td width="33%" valign="top">

### 🎞 Display Quality
Economy / Balanced / Maximum profiles over three switches: 60 fps (`DWMFRAMEINTERVAL`),
AVC 4:4:4 and hardware encoding.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### 🌗 Brightness & Contrast
A click-through Magnification API window redraws the remote picture with a color
matrix. Nothing is created at neutral values.

</td>
<td width="33%" valign="top">

### 🎨 Appearance
System / Light / Dark theme, a background image with adjustable transparency for the
image and for cards, and interface zoom 80–160% (Ctrl + wheel, Ctrl +/−/0).

</td>
<td width="33%" valign="top">

### 🔑 Per-Machine Credentials
Saves a remote admin login in Windows Credential Manager, the same entry `cmdkey /add`
writes. SMB, the service manager and the agent login all use it.

</td>
</tr>
</table>

---

## 🧠 Architecture

The solution has two programs and a folder of shared code. **`RdpShadow.App`** is the WPF app you run.
**`RdpShadow.Agent`** is one self-contained exe with two roles: a Windows service (SYSTEM, session 0)
that owns the network port and applies machine settings, and a `--helper` process the service
launches in the logged-on user's session, because only a process in that session can touch its
clipboard. **`src/Shared`** (protocol, clipboard watcher, settings records) is compiled into both.

```
┌──────────── Your computer ────────────┐              ┌──────────────── Remote computer ────────────────┐
│ RdpShadow.App (WPF, .NET 10)          │              │                                                 │
│  ├─ ShadowHost ◄──────────────────────┼─ RDP Shadow ─┼─► user session (picture + input)                │
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

One copy, from your machine to the remote one:

```
Ctrl+C locally → WM_CLIPBOARDUPDATE (ClipboardWatcher) → ClipItem → hash check
  → NegotiateStream frame [int32 length][byte type][payload], 1 MB chunks
  → service authenticates, relays bytes over the pipe → helper → remote clipboard (+ Win+V)
```

---

## 🚀 Quick Start

Requires the **.NET 10 SDK** to build, and Inno Setup 6 if you also want the installer.

```powershell
git clone https://github.com/yehuda-menachem/RDP-Shadow.git
cd RDP-Shadow
.\build.ps1
```

`build.ps1` publishes the agent to `dist\Agent\`, then the app to `dist\`, and stops on the first
failure so `dist` never mixes old and new binaries. If Inno Setup 6 is installed, it then compiles
`setup.iss` into `installer\RdpShadow-Setup-1.0.0.exe`; otherwise it skips that step with a warning.

Run `dist\RdpShadow.App.exe`, or install with the generated setup.

---

## 🎮 Interactive Workflow

The UI is Hebrew and right-to-left. Pages (left nav): **Computers** (מחשבים), **Clipboard** (לוח),
**Display quality** (איכות תצוגה), **Prepare machine** (הכנת מחשב), **Appearance** (מראה).

1. **Add a computer.** Type an IP address or host name and press Enter. A leading `\\` is
   stripped; names with spaces or slashes are silently ignored. The list has a search box and a
   remove button per row (not for the computer in an open session).
2. **Watch its status.** Every 10 s the list checks TCP 3389 (no ping: ICMP is often blocked),
   the active session and user, and whether the agent answers on 47800. Probing pauses during
   a session and while minimized.
3. **Prepare it.** On **Prepare machine**, save the remote local administrator's credentials for
   workgroup machines (domain machines use your own identity), choose the Shadow permission, then
   **Fix all** (תקן הכל). This installs or updates the agent and applies every setting.
4. **Connect.** Back on **Computers**, click **View** (צפייה) or **Control** (שליטה). The session
   bar shows the user and session, a clipboard status pill, fit-to-window, brightness/contrast,
   full screen and disconnect.
5. **Copy and paste** across machines. Items over 4 MB show transfer progress in the pill; a copy
   larger than the size limit shows a toast instead of silently not arriving.

While the window is minimized, sync pauses. When it is restored, anything you copied locally in
the meantime is sent right away.

---

## 📦 Installing and Deploying

**Your computer:** Windows x64 with the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The installer
(`setup.iss`) needs admin rights, checks for that runtime and offers its download page if missing.

**Remote computers:**
- Windows edition with an RDP host (Pro / Enterprise), and a logged-on user. Shadow cannot attach
  to the logon screen.
- An account that is a local administrator there.
- Reachable ports: **3389** (RDP), **445** (SMB, agent install/update), **47800** (agent).
- No .NET needed: the agent is published self-contained, single-file, `win-x64`.

The agent is installed to `C:\Program Files\RdpShadow Agent\` as the auto-start service
`RdpShadowAgent`, with an unrestricted service SID so its firewall rule can be bound to it.
There is no uninstall button in the app. From an elevated prompt:

```powershell
sc.exe \\<host> stop RdpShadowAgent
sc.exe \\<host> delete RdpShadowAgent
Remove-Item "\\<host>\C$\Program Files\RdpShadow Agent" -Recurse
```

---

## 🧱 Project Structure

```
RdpShadow.slnx
├─ build.ps1                  publishes agent + app into .\dist, then the installer
├─ setup.iss                  Inno Setup script (Hebrew, admin, .NET 10 runtime check)
├─ src/RdpShadow.App/         WPF app
│   ├─ MainWindow.xaml(.cs)   all pages and the session view
│   ├─ ShadowHost.cs          mstsc launch, window adoption, resize hook, smart sizing
│   ├─ ClipboardLink.cs       app side of the clipboard channel, reconnect, pause
│   ├─ RemoteAgent.cs         install/update/apply over SMB + remote SCM
│   ├─ ColorFilter.cs         brightness/contrast overlay (Magnification API)
│   ├─ Credentials.cs         Windows Credential Manager
│   ├─ Models.cs              app settings, computer row
│   └─ Native.cs              P/Invoke (user32, WTS)
├─ src/RdpShadow.Agent/       service + per-session helper, one exe
│   ├─ AgentService.cs        settings, firewall, helper lifecycle, TCP → pipe relay
│   └─ Helper.cs              clipboard side in the user's session (--helper)
├─ src/Shared/                compiled into both projects
│   ├─ ClipboardWatcher.cs    listener, format filtering, loop prevention, file materializing
│   ├─ ClipItem.cs            clipboard item and its serialization
│   ├─ Wire.cs                framing, port, pipe name, protection level
│   └─ AgentConfig.cs         settings.json / status.json / link config records
└─ tools/IconBuilder/         renders app.ico and the PNG sizes with WPF
```

---

## 🔧 Configuration Matrix

Everything is set from the UI. These are the stored values and their effective defaults.

**App** — `%AppData%\RdpShadow\settings.json` (`AppSettings`, `ClipSettings`):

| Setting | What it does | Default |
|---|---|---|
| `Computers` | Saved host list | `[]` |
| `Clipboard.Enabled` | Clipboard sync on/off | `true` |
| `Clipboard.Direction` | `Both`, `ToMe` (remote→me), `ToRemote` (me→remote) | `Both` |
| `Clipboard.Text` / `Images` / `Files` | Which content kinds are synced | `true` |
| `Clipboard.MaxMegabytes` | Per-item size limit; clamped to 1–500 | `100` |
| `Brightness` / `Contrast` | Remote-picture filter, −50..50 / 50..150 % | `0` / `100` |
| `UiScale` | Interface zoom of pages and session bar, 0.8–1.6 | `1` |
| `Theme` | `System`, `Light`, `Dark` | `System` |
| `BackgroundImage` | Picture behind the pages (full path) | `null` |
| `BackgroundTransparency` | Image transparency, 0–95 % | `70` |
| `SurfaceTransparency` | Card/button transparency over an image, 0–90 % | `30` |
| `Window` | Last restored window bounds and maximized state | `null` |

**Agent** — `C:\Program Files\RdpShadow Agent\settings.json` on each remote machine (`MachineSettings`),
written by the app and applied when the service starts:

| Setting | What it does | Default |
|---|---|---|
| `Shadow` | Policy: 1 control+consent, 2 control, 3 view+consent, 4 view | `2` |
| `Fps60` | `true` sets `DWMFRAMEINTERVAL=15`, `false` deletes it | `null` (untouched) |
| `Avc444` | `AVC444ModePreferred` | `null` (untouched) |
| `HardwareEncode` | `AVCHardwareEncodePreferred` | `null` (untouched) |
| `ClipboardHistory` | Helper sets `EnableClipboardHistory=1` for the user | `true` |

The three quality values stay `null` until **Apply** on the quality page, which always writes all three
(Economy writes explicit `0`s and removes `DWMFRAMEINTERVAL`).

On every start the service also sets `fDenyTSConnections=0` and `AllowRemoteRPC=1`, adds the
`RdpShadow Agent` firewall rule, and enables the Remote Desktop rule group, narrowing rules open
to everyone (`*`) to the local subnet. It reports back in `status.json` next to the settings file.

---

## 🔒 Security

- **Authentication:** NTLM over `NegotiateStream` with `EncryptAndSign`, in a domain too. The app
  passes a deliberately unresolvable SPN so Negotiate never picks Kerberos, which the agent could
  not validate. Only the logged-on user's own account or an administrator is accepted.
- **The port belongs to the SYSTEM service, not the helper.** The helper runs as the remote user;
  if it listened, that user could capture the admin's NTLM exchange. The service binds 47800
  exclusively on IPv4 and IPv6 and relays decrypted bytes over a named pipe that allows only SYSTEM
  and the logged-on user and is created with `FirstPipeInstance`. The helper connects at
  *Identification* level and checks that the pipe server is in session 0.
- **Firewall:** the agent rule is limited to `localsubnet` and bound to the service
  (`service=RdpShadowAgent`). Admin-defined address ranges on Remote Desktop rules are kept.
- **Incoming data is untrusted.** File paths may not leave the temp folder, and only formats the
  receiver would send itself are written: no raw `CF_HDROP` (UNC paths → NTLM leak on paste), no
  GDI handles, no OLE or Shell formats.
- **Disk:** only received files are written, under `%TEMP%\RdpShadow`; folders older than a day
  are removed on start and on each file paste.

---

## 💡 Design Decisions / Reliability

- **Hosting the mstsc window instead of an ActiveX control** — `mstsc /shadow` gets its invitation
  through an undocumented RPC and shows it in the Desktop Sharing viewer
  (`SrApiViewerAxContainerClass`); no public API accepts that invitation. A `SetWinEventHook` keeps
  the adopted window sized to the app.
- **One retry on a quick mstsc exit** — the first `/shadow` launch sometimes exits within seconds.
- **Reclaiming Win+V** — mstsc's own Shadow clipboard re-publishes the helper's writes on your side
  flagged `CanIncludeInClipboardHistory=0`, dropping your copy from Win+V. When that happens the app
  rewrites the last item it sent, once. Files are not rewritten.
- **Dead-link detection** — the helper pings every 5 s; 15 s of silence drops the link. TCP
  keepalive covers a link that dies mid-frame. The app reconnects every 3 s.
- **Service resilience** — the 3 s service tick catches its own errors, the listener retries if the
  port is taken, and each connection is its own failure boundary. `status.json` is rewritten only
  when something changes.
- **Agent updates without version numbers** — the installed exe keeps the build's write time
  (`File.Copy`), so "outdated" means the exe next to the app is newer by more than 2 s.

---

## 🚧 Known Limitations

- **Files don't appear in Win+V.** Windows history keeps text, HTML and images only.
- **Local subnet only.** Firewall rules block connections from another network (VPN, RD Gateway).
- **One session at a time**, and no uninstall from the app (see the commands above).
- **No Ctrl+Alt+Del button.**
- **System drive is assumed to be `C:`** for the agent install path.
- **Image quality (`ImageQuality`) is not offered**: its values could not be verified for Shadow.
- **Send progress counts bytes handed to the connection**, not bytes the other side has received.
- **Quality settings apply on the next connection**, after the service restarts.

---

## 📜 License & Credits

MIT, see [LICENSE](LICENSE). Author: [@yehuda-menachem](https://github.com/yehuda-menachem).
