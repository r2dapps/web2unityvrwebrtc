# WebRTC Network Architecture: Wi-Fi vs Mobile 4G/5G Cellular Data & Solutions Guide

## 📌 Executive Summary

This document explains why WebRTC voice and video streaming in **Sunofy**, **Web2RVRWebRTC**, and **WalkieTalkie** work seamlessly across **different Wi-Fi networks** (e.g. Home Wi-Fi to Office Wi-Fi / Friend's Wi-Fi), but experience ICE disconnects on **Mobile 4G/5G Cellular Data** networks.

It provides immediate, step-by-step solutions to enable 100% cross-network connectivity across all mobile carriers worldwide.

---

## 🔍 Why WebRTC Works Over Wi-Fi but Fails Over Mobile Data (CGNAT)

### 1. Wi-Fi Networks (Full-Cone / Restricted Cone NAT)
- Standard home and office Wi-Fi routers assign local IP addresses (e.g. `192.168.x.x`).
- When a WebRTC call starts, **STUN servers** (like Google STUN `stun:stun.l.google.com:19302`) successfully discover each router's public IP address and port mapping.
- Because Wi-Fi routers permit inbound UDP packets once STUN binding completes, **direct peer-to-peer (P2P) audio/video streaming works 100% across different Wi-Fi routers**.

### 2. Mobile 4G/5G Cellular Networks (Symmetric NAT / CGNAT)
- Mobile network operators (Jio, Airtel, Vi, T-Mobile, AT&T, Verizon) route millions of mobile phones through **Carrier-Grade NAT (CGNAT)**.
- CGNAT assigns dynamic, unpredictable external ports for every outgoing connection. Inbound UDP packets from external peers are **blocked by carrier firewalls**.
- Standard STUN candidate discovery fails over CGNAT. Without a **dedicated TURN (Traversal Using Relays around NAT) relay server**, WebRTC ICE candidate gathering state transitions from `checking` -> `disconnected`.

---

## 🛠️ Step-by-Step Solutions for All 3 Projects

### Solution Option A: Free Dedicated Metered TURN API Key (Recommended — 0 Cost)

1. Go to [https://www.metered.ca/stun-turn](https://www.metered.ca/stun-turn) and sign up for a free account (100% free, includes **50 GB of free relay bandwidth every month**).
2. Go to your Dashboard and click **"Turn Server"**.
3. Copy your TURN credentials (`username`, `credential`, and domain `YOUR_APP.rel.metered.ca`).

---

### Implementation in Code:

#### 1. Sunofy (`src/services/syncPartySocket.ts`)
Update `WEBRTC_ICE_SERVERS`:
```typescript
export const WEBRTC_ICE_SERVERS: RTCConfiguration = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    { urls: 'stun:stun.cloudflare.com:3478' },
    {
      urls: 'turn:YOUR_APP.rel.metered.ca:443',
      username: 'YOUR_METERED_USERNAME',
      credential: 'YOUR_METERED_PASSWORD'
    },
    {
      urls: 'turn:YOUR_APP.rel.metered.ca:443?transport=tcp',
      username: 'YOUR_METERED_USERNAME',
      credential: 'YOUR_METERED_PASSWORD'
    }
  ],
  iceCandidatePoolSize: 10
};
```

#### 2. Web2RVRWebRTC (`app.js` & `UnityVRWebRTC.cs`)
In `app.js`:
```javascript
const ICE_SERVERS = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    {
      urls: 'turn:YOUR_APP.rel.metered.ca:443',
      username: 'YOUR_METERED_USERNAME',
      credential: 'YOUR_METERED_PASSWORD'
    }
  ]
};
```

In `UnityVRWebRTC.cs` (Unity C#):
```csharp
RTCConfiguration config = new RTCConfiguration
{
    iceServers = new[]
    {
        new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
        new RTCIceServer 
        { 
            urls = new[] { "turn:YOUR_APP.rel.metered.ca:443" },
            username = "YOUR_METERED_USERNAME",
            credential = "YOUR_METERED_PASSWORD"
        }
    }
};
```

#### 3. WalkieTalkie (`src/services/peerManager.ts`)
In `peerManager.ts`:
```typescript
const ICE_SERVERS = [
  { urls: 'stun:stun.l.google.com:19302' },
  {
    urls: 'turn:YOUR_APP.rel.metered.ca:443',
    username: 'YOUR_METERED_USERNAME',
    credential: 'YOUR_METERED_PASSWORD'
  }
];
```

---

### Solution Option B: Self-Hosted Coturn Docker VPS (Unlimited Bandwidth)

If you have a $3/month VPS (DigitalOcean, Hetzner, or Oracle Cloud Free Tier):
1. Run Coturn docker container:
   ```bash
   docker run -d --name coturn --net=host \
     -v /etc/coturn/turnserver.conf:/etc/coturn/turnserver.conf \
     coturn/coturn
   ```
2. Set `turnserver.conf`:
   ```conf
   listening-port=3478
   tls-listening-port=5349
   realm=yourdomain.com
   user=admin:securepassword123
   lt-cred-mech
   ```
3. Use your VPS IP address in `iceServers` array across all 3 projects.

---

## 📊 Summary Table

| Connection Type | STUN Needed? | TURN Needed? | Current Status | Fixed Status with Dedicated TURN |
| :--- | :---: | :---: | :---: | :---: |
| Same Wi-Fi Network | Yes | No | ✅ Working | ✅ Working |
| Different Wi-Fi Networks | Yes | No | ✅ Working | ✅ Working |
| 4G/5G Cellular Data (Jio/Airtel/T-Mobile) | Yes | **YES (Port 443 Relay)** | ⚠️ ICE Disconnect | ✅ 100% Working |
