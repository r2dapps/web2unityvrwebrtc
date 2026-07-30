# Real WebRTC Doctor VR Consultation Setup & Deployment Guide

This app is a **100% Real, Functional WebRTC P2P Video Application** connecting a Doctor's Mobile Device (GitHub Pages) and Unity VR (Laptop / Meta Quest 3).

---

## 📖 Complete Beginner's Guide: How to Create a FREE Firebase Database (Pin-to-Pin)

If you have **never used Firebase before**, follow these exact step-by-step instructions. It takes less than 2 minutes, is 100% free forever, and requires no credit card!

### Step 1: Open Firebase Console
1. Open your web browser and go to [https://console.firebase.google.com/](https://console.firebase.google.com/).
2. Log in with any standard Google (Gmail) account.

### Step 2: Create a New Project
1. Click the big **"+ Add project"** button (or "Create a project").
2. Type a name for your project (e.g., `doctor-vr-app` or `my-telemedicine-app`).
3. Click **Continue**.
4. Disable **Google Analytics** (toggle switch off) to keep it simple, then click **Create project**.
5. Wait 10 seconds for Google to set up your project, then click **Continue**.

### Step 3: Create a Realtime Database Instance
1. In the left-hand sidebar, click **Build** -> then select **Realtime Database**.
2. Click the blue **"Create Database"** button in the center of the page.
3. **Database Location**: Choose `Asia South (Mumbai)` or `United States` (closest to you), then click **Next**.
4. **Security Rules**: Select **Start in test mode**, then click **Enable**.

### Step 4: Copy Your Realtime Database URL
1. You will now see your database dashboard.
2. At the top of the page, copy the URL string that looks like this:
   `https://your-project-name-default-rtdb.firebaseio.com` (or ending in `.firebasedatabase.app`).
3. This is your **Database URL**!

### Step 5: Make Database Rules Permanent (So It Never Expires)
1. Click on the **Rules** tab at the top of your Realtime Database dashboard.
2. Replace the text in the code editor with this exact JSON block:
   ```json
   {
     "rules": {
       ".read": true,
       ".write": true
     }
   }
   ```
3. Click **Publish** at the top right. 
4. Done! Your database is now active 24/7/365 with zero cost!

---

## 📡 Dual Signaling Engine Architecture (Firebase Multi-Network + Local PeerJS)

This project supports **Dual-Signaling Modes**:

1. **Firebase Engine (Multi-Network - Recommended)**:
   - Uses Realtime Database for SDP offer/answer exchange.
   - Works across **cellular 4G/5G, different Wi-Fi networks, and strict firewalls**.
   - Includes automatic room cleanup on disconnect (`.onDisconnect().remove()`) so storage remains at 0 MB forever!

2. **PeerJS Engine (Same Local Wi-Fi)**:
   - Preserves PeerJS signaling for local testing over the same Wi-Fi router.
   - Configured with explicit Google STUN servers (`stun:stun.l.google.com:19302`) for local peer discovery.

---

## 📱 How to Host & Test on GitHub Pages (Mobile Device)

1. Push this repository to GitHub.
2. Go to **Repo Settings > Pages**.
3. Under **Source**, select `main` branch and `/ (root)`.
4. Open the GitHub Pages URL on your mobile phone:
   `https://r2dapps.github.io/web2rvrwebrtc/`
5. Select **Doctor Role** -> Click **Create Room & Start Camera**.
6. Open on **2nd Device / Patient / VR** -> Select **Patient Role** -> Enter Room Key -> Click **Join Consultation Room**!

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
