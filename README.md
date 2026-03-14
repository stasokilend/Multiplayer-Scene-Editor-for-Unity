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

# 🎮 Multiplayer Scene Editor

> A Unity Editor plugin for **real-time collaborative scene editing** — like Google Docs, but inside the Unity Editor.

![Unity](https://img.shields.io/badge/Unity-2021.3%2B-black?logo=unity)
![Language](https://img.shields.io/badge/Language-C%23-239120?logo=csharp)
![Status](https://img.shields.io/badge/Status-Early%20Development-orange)
![Network](https://img.shields.io/badge/Network-LAN%20Only-blue)

---

## ⚠️ Important Warnings

> **Please read carefully before using the plugin.**

---

### 🌐 Local Network Only

This plugin currently works **only over a local network (LAN)**.  
Remote collaboration over the internet is **not supported** yet.  
All participants must be on the **same Wi-Fi or wired network**.

---

### 🔗 Unity Version Control Required

You **must** connect all collaborators to the **project owner's Unity Version Control** repository **before** starting a session.

> Working without version control puts your project at serious risk of data loss or merge conflicts.

---

### 🚧 Early Development — Use With Caution

This plugin is in its **early stages of development**.

**Known risks:**
- 🐛 Bugs and unexpected behaviour are likely
- 💥 Project files or scene structure may become corrupted
- 🔧 GameObjects may lose their scripts or have components reset
- 📋 Extensive testing is still required

> ❌ **Not recommended for serious or production projects.**  
> Use only in test projects or with a full version control backup.

---

## ✨ Features

| Feature | Description |
|---|---|
| 🖥️ Host / Join Session | One user hosts, others connect via TCP over LAN |
| 🔐 Join Approval | Host manually approves or denies each connection request |
| 🔄 Transform Sync | Real-time sync of position, rotation, and scale |
| 🔒 Object Locking | Selecting an object locks it — others cannot edit it simultaneously |
| 🌳 Hierarchy Sync | Create, delete, rename, and reparent objects in real time |
| 🖱️ 3D Cursors | See where each collaborator's mouse is in the Scene View |
| 💬 Chat | Built-in text chat inside the editor |
| 🎨 User Colors | Each participant gets a unique color for easy identification |
| 🏷️ Hierarchy Overlay | Selected objects are highlighted in the Hierarchy window |

---

## 📦 Installation

1. Clone or download this repository
2. Copy the `MultiplayerSceneEditor` folder into your Unity project's `Assets/` directory
3. Wait for Unity to compile the scripts
4. Open the plugin via **Window → Multiplayer Scene Editor**

---

## 🚀 Quick Start

### Hosting a Session

1. Open **Window → Multiplayer Scene Editor**
2. Enter your display name and choose a port (default: `7700`)
3. Click **Host**
4. Share your **local IP address** with collaborators
5. Approve incoming connection requests in the **Users** tab

### Joining a Session

1. Open **Window → Multiplayer Scene Editor**
2. Enter your display name and the host's local IP address
3. Click **Join** and wait for the host to approve your request

---

## 🏗️ Project Structure

```
MultiplayerSceneEditor/
├── Editor/
│   ├── Core/
│   │   ├── Protocol.cs          # Network protocol, message types, serialization
│   │   ├── NetworkServer.cs     # TCP server (host side)
│   │   ├── NetworkClient.cs     # TCP client (joining side)
│   │   ├── SceneSyncManager.cs  # Central session coordinator
│   │   ├── LockManager.cs       # Per-object edit locking
│   │   └── ObjectTracker.cs     # Detects and broadcasts scene changes
│   └── UI/
│       ├── MultiplayerEditorWindow.cs  # Main dockable editor window
│       ├── HierarchyOverlay.cs         # Hierarchy window color tinting
│       └── SceneViewOverlay.cs         # Scene View cursors and indicators
└── Runtime/
    └── StableGuid.cs            # Persistent GUID component for GameObjects
```

---

## 🔧 How It Works

The plugin uses a **TCP client-server architecture** with a binary framing protocol (4-byte length prefix + JSON payload).

```
Client A (Host)          Client B                Client C
     │                      │                       │
     │◄──── Handshake ───────┤                       │
     │──── JoinPending ─────►│                       │
     │                      │  [Host approves]       │
     │──── HandshakeAck ────►│                       │
     │◄──── Handshake ───────────────────────────────┤
     │──────────────────────────── HandshakeAck ────►│
     │                      │                       │
     │◄──── TransformUpdate ─┤──────────────────────►│
     │◄──── LockRequest ─────┤                       │
     │──── LockGrant ───────►│                       │
```

Each GameObject is assigned a **StableGuid** component (hidden from the Inspector) so objects can be reliably identified and synced across all editors.

---

## 📡 Network Protocol

The plugin communicates using 18 message types:

| Message | Direction | Purpose |
|---|---|---|
| `Handshake` | Client → Server | Join request |
| `HandshakeAck` | Server → Client | Welcome + full scene snapshot |
| `JoinPending` | Server → Client | Waiting for host approval |
| `JoinDenied` | Server → Client | Host rejected the request |
| `TransformUpdate` | Any → All | Object moved/rotated/scaled |
| `HierarchyChange` | Any → All | Object created/deleted/renamed/reparented |
| `SelectionUpdate` | Any → All | User changed their selection |
| `LockRequest/Grant/Deny/Release` | Both | Object lock management |
| `CursorUpdate` | Any → All | 3D mouse position in Scene View |
| `ComponentUpdate` | Any → All | Non-transform property changed |
| `ChatMessage` | Any → All | Text chat |
| `Ping / Pong` | Any → Any | Connection health check |

---

## ⚙️ Requirements

- Unity **2021.3 LTS** or newer
- All collaborators on the **same local network**
- Project connected to **Unity Version Control** before starting

---

## 🤝 Contributing

Contributions, bug reports, and feedback are welcome!  
This project is in active early development — please open an issue before submitting large pull requests.

---

## 📄 License

MIT License — see [LICENSE](LICENSE) for details.
