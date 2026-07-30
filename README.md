# Real WebRTC Doctor VR Consultation Setup & Deployment Guide

This app is a **100% Real, Functional WebRTC P2P Video Application** connecting a Doctor's Mobile Device (GitHub Pages) and Unity VR (Laptop / Meta Quest 3).

---

## ❓ Frequently Asked Questions & Error Fixes

### Q1: Why did I get `Unsafe attempt to load URL file:///` or `TypeError: Cannot read properties of null`?

> [!WARNING]
> **Cause**: You double-clicked `index.html` directly from your hard drive (`file:///E:/...`).
> Modern web browsers (Chrome, Safari, Edge) **block camera/mic permissions** (`getUserMedia`) and cross-origin Firebase DB scripts when opened as a local `file:///` for security reasons!

**The Fix**:
- Host the folder on **GitHub Pages** (over HTTPS).
- OR run a local web server (e.g. `npx serve`, VS Code Live Server, or Python `python -m http.server 8000`).

---

### Q2: How do I create a NEW dedicated Firebase Database so I don't mess up AetherTalk?

To create a separate, 100% free Firebase Realtime Database in 2 minutes:

1. Go to [console.firebase.google.com](https://console.firebase.google.com) and click **"Add project"**.
2. Name it (e.g. `telemedicine-vr-db`) and click **Continue** (you can disable Google Analytics).
3. In the left sidebar, click **Build > Realtime Database**.
4. Click **Create Database** -> Select location (**Asia South / Mumbai** or **US Central**) -> Select **Start in Test Mode** (enables read/write for 30 days).
5. Copy your Database URL (e.g. `https://telemedicine-vr-db-default-rtdb.firebaseio.com`).
6. Paste this URL into the Doctor Mobile App input and Unity `UnityVRWebRTC.cs` script!

---

### Q3: How can we bypass Signaling completely WITHOUT Node.js OR Firebase? (Just GitHub Pages & Unity)

If you don't want to use Firebase or Node.js at all, you have 2 zero-cloud signaling options:

#### Option A: Free Public PeerJS Server (`0.peerjs.com`) — 100% Zero-Cloud Setup
- PeerJS is a free open-source WebRTC wrapper.
- Uses public server `0.peerjs.com` (Requires 0 accounts, 0 databases, 0 Node servers).
- In JS: `const peer = new Peer('DOC-8921')`
- In Unity C#: Use Unity PeerJS wrapper.

#### Option B: QR Code / WebRTC Direct SDP Link
- Doctor mobile app generates a QR Code containing the WebRTC SDP offer string.
- Meta Quest 3 camera scans the QR code directly, generates an answer QR code, and connects P2P!

---

## 📱 How to Host & Test on GitHub Pages (Mobile Device)

1. Push this repository to GitHub.
2. Go to **Repo Settings > Pages**.
3. Under **Source**, select `main` branch and `/ (root)` or `/vr-doctor-consultation-demo`.
4. Open the GitHub Pages URL on your mobile phone:
   `https://<your-username>.github.io/walkietalkie/vr-doctor-consultation-demo/`
5. Enter a Doctor Room Key (e.g. `DOC-8921`) and click **Start Doctor Session**.
6. The Doctor Mobile screen turns into a **Full-Screen Video Call View**:
   - Doctor Camera = Top-Left Floating Picture-in-Picture.
   - Full Screen = Waits for live Unity VR video stream.

---

## 💻 How to Connect Unity (Laptop / Meta Quest 3 / Quest 3S)

1. Open your Unity Project (2022.3 LTS or 2023).
2. Install **Unity WebRTC** (`com.unity.webrtc`) in Unity Package Manager.
3. Copy `UnityVRWebRTC.cs` into your Unity `Assets/Scripts/` folder.
4. Create a 3D Quad in your scene (representing the floating doctor screen).
5. Attach `UnityVRWebRTC.cs` to a GameObject.
6. In the Unity Inspector:
   - Set `Firebase Database Url` to your new Firebase URL.
   - Set `Room Key` to `DOC-8921` (matching your mobile phone!).
   - Drag 3D Quad's `Material` to `Doctor Display Material`.
   - Drag Main Camera to `VR Stream Camera`.
7. Press **PLAY in Unity**!
