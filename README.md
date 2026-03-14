<div align="center">

<br/>

```
███╗   ███╗███████╗███████╗
████╗ ████║██╔════╝██╔════╝
██╔████╔██║███████╗█████╗  
██║╚██╔╝██║╚════██║██╔══╝  
██║ ╚═╝ ██║███████║███████╗
╚═╝     ╚═╝╚══════╝╚══════╝
Multiplayer Scene Editor
```

**Real-time collaborative scene editing for Unity — like Google Docs, but for your hierarchy.**

<br/>

![Unity](https://img.shields.io/badge/Unity-2021.3%2B-black?style=flat-square&logo=unity)
![C#](https://img.shields.io/badge/C%23-.NET%20Standard%202.1-239120?style=flat-square&logo=csharp)
![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)
![Status](https://img.shields.io/badge/Status-Alpha-orange?style=flat-square)

<br/>

</div>

---

## What is this?

**Multiplayer Scene Editor (MSE)** is a Unity Editor package that lets multiple developers work in the same scene simultaneously — seeing each other's transforms, selections, cursor positions, and hierarchy changes in real time over a local TCP connection.

No cloud. No subscription. Just connect and collaborate.

<br/>

## Features

| | |
|---|---|
| 🔴 **Live presence** | See every collaborator's cursor moving in 3D space |
| 🔒 **Object locking** | Select an object to claim it — others can't edit it while you do |
| 🌳 **Hierarchy sync** | Create, delete, rename, reparent — changes propagate instantly |
| 📐 **Transform sync** | Position, rotation, scale streamed in real time |
| ✅ **Join approval** | Host explicitly approves or denies incoming connections |
| 💬 **In-editor chat** | Text chat in the MSE panel, no external tools needed |
| 🎨 **Color-coded users** | Each participant gets a unique hue for visual disambiguation |
| 🏓 **Heartbeat / kick** | Dead connections detected and cleaned up automatically |

<br/>

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                     Unity Editor                        │
│                                                         │
│   ┌──────────────┐        ┌──────────────────────────┐  │
│   │  MSE Window  │◄──────►│    SceneSyncManager      │  │
│   │  (UI / Chat) │        │  (central coordinator)   │  │
│   └──────────────┘        └────────┬─────────────────┘  │
│                                    │                     │
│   ┌──────────────┐        ┌────────▼─────────────────┐  │
│   │HierarchyOver-│        │   ObjectTracker          │  │
│   │lay / SceneV. │        │   LockManager            │  │
│   └──────────────┘        └────────┬─────────────────┘  │
│                                    │                     │
│                    ┌───────────────▼────────────────┐    │
│                    │     NetworkServer / Client      │    │
│                    │   TCP  ·  length-prefixed JSON  │    │
│                    └────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

<br/>

## Quick Start

### Installation

1. Copy the `MultiplayerSceneEditor/` folder into your project's `Assets/` or `Packages/` directory.
2. Unity will compile the package automatically.
3. Open **Window → Multiplayer Scene Editor**.

### Host a session

```
Window → Multiplayer Scene Editor → Session tab
  [Your Name]  →  [Port: 7700]  →  [ Host ]
```

Share your **local IP** with teammates (shown in the panel).

### Join a session

```
Window → Multiplayer Scene Editor → Session tab
  [Your Name]  →  [Host IP : Port]  →  [ Join ]
```

The host will see a join request and must **Approve** it before you enter.

<br/>

## Project Structure

```
MultiplayerSceneEditor/
├── Editor/
│   ├── Core/
│   │   ├── Protocol.cs          # Wire format, message types, serialisation helpers
│   │   ├── NetworkServer.cs     # TCP listener + per-peer connection (host side)
│   │   ├── NetworkClient.cs     # TCP client with reconnect logic (guest side)
│   │   ├── SceneSyncManager.cs  # Central coordinator, Unity event loop
│   │   ├── ObjectTracker.cs     # Detects scene changes, fires sync events
│   │   └── LockManager.cs       # GUID → userId ownership table
│   └── UI/
│       ├── MultiplayerEditorWindow.cs  # Dockable panel (Session / Users / Chat)
│       ├── HierarchyOverlay.cs         # Colour badges in the Hierarchy window
│       └── SceneViewOverlay.cs         # Cursor labels in the Scene View
└── Runtime/
    └── StableGuid.cs            # Hidden MonoBehaviour — stable GUID per GameObject
```

<br/>

## Protocol

All messages are framed as:

```
┌──────────────┬──────────────────────────────────┐
│  4 bytes     │  N bytes                         │
│  Big-endian  │  UTF-8 JSON  (Envelope)          │
│  length      │                                  │
└──────────────┴──────────────────────────────────┘
```

**Envelope**
```json
{
  "type":      5,
  "userId":    "a3f9...",
  "payload":   "{ ... }",
  "timestamp": 1710000000000
}
```

**Message types**

| # | Name | Direction | Purpose |
|---|------|-----------|---------|
| 1 | `Handshake` | Client → Server | Join request |
| 2 | `HandshakeAck` | Server → Client | Welcome + scene snapshot |
| 3 | `UserJoined` | Server → All | New participant |
| 4 | `UserLeft` | Server → All | Participant disconnected |
| 5 | `TransformUpdate` | Any → All | Position / rotation / scale |
| 6 | `HierarchyChange` | Any → All | Create / delete / rename / reparent |
| 7 | `SelectionUpdate` | Any → All | What a user has selected |
| 8–11 | `Lock*` | Any ↔ Server | Exclusive edit requests |
| 12 | `CursorUpdate` | Any → All | 3D mouse position |
| 13 | `ComponentUpdate` | Any → All | Non-transform property change |
| 14 | `ChatMessage` | Any → All | Text chat |
| 17 | `JoinDenied` | Server → Client | Host rejected |
| 18 | `JoinPending` | Server → Client | Waiting for approval |

<br/>

## Requirements

- **Unity 2021.3 LTS** or newer
- All participants on the **same local network** (LAN / VPN)
- No additional dependencies — uses only Unity's built-in APIs and .NET sockets

<br/>

## Known Limitations

- **No undo sync** — Ctrl+Z is local only; use with care in shared sessions
- **No Prefab sync** — Prefab mode is not yet broadcast
- **Scene save is independent** — each user saves their own copy
- **LAN only** — no relay server; use a VPN (e.g. Tailscale) for remote teams

<br/>

## Contributing

Issues and pull requests are welcome. Please open an issue first to discuss significant changes.

<br/>

## License

MIT © 2024
