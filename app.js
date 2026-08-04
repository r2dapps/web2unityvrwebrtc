// Pure Native WebRTC (RTCPeerConnection) + Real Firebase Signaling Engine with Auto-Cleanup

const DEFAULT_FIREBASE_DB_URL = 'https://walkietalkie-c0f03-default-rtdb.asia-southeast1.firebasedatabase.app';

const ICE_SERVERS = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    { urls: 'stun:stun1.l.google.com:19302' },
    { urls: 'stun:stun2.l.google.com:19302' },
    { urls: 'stun:stun3.l.google.com:19302' },
    { urls: 'stun:stun4.l.google.com:19302' },
    { urls: 'stun:stun.cloudflare.com:3478' },
    { urls: 'stun:global.stun.twilio.com:3478' },
    { urls: 'turn:openrelay.metered.ca:80', username: 'openrelayproject', credential: 'openrelayproject' },
    { urls: 'turn:openrelay.metered.ca:443', username: 'openrelayproject', credential: 'openrelayproject' },
    { urls: 'turn:openrelay.metered.ca:443?transport=tcp', username: 'openrelayproject', credential: 'openrelayproject' },
    { urls: 'turn:relay.metered.ca:80', username: 'openrelayproject', credential: 'openrelayproject' },
    { urls: 'turn:relay.metered.ca:443', username: 'openrelayproject', credential: 'openrelayproject' },
    { urls: 'turn:relay.metered.ca:443?transport=tcp', username: 'openrelayproject', credential: 'openrelayproject' }
  ],
  iceCandidatePoolSize: 10
};

document.addEventListener('DOMContentLoaded', () => {
  // DOM Elements
  const startScreen = document.getElementById('startScreen');
  const callScreen = document.getElementById('callScreen');
  
  const tabDoctor = document.getElementById('tabDoctor');
  const tabPatient = document.getElementById('tabPatient');
  const panelDoctor = document.getElementById('panelDoctor');
  const panelPatient = document.getElementById('panelPatient');
  
  const doctorRoomInput = document.getElementById('doctorRoomInput');
  const patientRoomInput = document.getElementById('patientRoomInput');
  const shareRoomKeyTag = document.getElementById('shareRoomKeyTag');
  const expiryTimeText = document.getElementById('expiryTimeText');
  const btnNewKey = document.getElementById('btnNewKey');
  const btnCreateRoom = document.getElementById('btnCreateRoom');
  const btnJoinAsPatient = document.getElementById('btnJoinAsPatient');
  const customFirebaseInput = document.getElementById('customFirebaseInput');
  
  const displayRoomKey = document.getElementById('displayRoomKey');
  const activeRoomTag = document.getElementById('activeRoomTag');
  const btnCopyKey = document.getElementById('btnCopyKey');
  const connectingOverlay = document.getElementById('connectingOverlay');
  const connectingTitle = document.getElementById('connectingTitle');

  const localVideo = document.getElementById('localVideo');
  const remoteVideo = document.getElementById('remoteVideo');
  const localPipLabel = document.getElementById('localPipLabel');

  const btnToggleMic = document.getElementById('btnToggleMic');
  const btnToggleCam = document.getElementById('btnToggleCam');
  const btnFlipCam = document.getElementById('btnFlipCam');
  const btnEndSession = document.getElementById('btnEndSession');

  // State
  let pc = null;
  let firebaseDb = null;
  let localStream = null;
  let currentRoomId = '';
  let userRole = 'doctor';
  let isAudioMuted = false;
  let isVideoOff = false;
  let facingMode = 'user';
  let activeRoomRef = null;

  // 1. Expiry Time Calculation (12:00 AM IST)
  function updateExpiryDisplay() {
    if (!expiryTimeText) return;
    const now = new Date();
    const istOffset = 5.5 * 60 * 60 * 1000;
    const utcTime = now.getTime() + (now.getTimezoneOffset() * 60000);
    const istDate = new Date(utcTime + istOffset);
    
    const tomorrowIST = new Date(istDate);
    tomorrowIST.setDate(tomorrowIST.getDate() + 1);
    tomorrowIST.setHours(0, 0, 0, 0);

    expiryTimeText.textContent = tomorrowIST.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) + ' IST';
  }

  // 2. Generate Random Room Key
  function generateNewRoomKey() {
    const rand = Math.floor(1000 + Math.random() * 9000);
    const key = `DOC-${rand}`;
    if (doctorRoomInput) doctorRoomInput.value = key;
    if (shareRoomKeyTag) shareRoomKeyTag.textContent = key;
  }

  if (btnNewKey) btnNewKey.addEventListener('click', generateNewRoomKey);

  const btnCopyLobbyKey = document.getElementById('btnCopyLobbyKey');
  if (btnCopyLobbyKey) {
    btnCopyLobbyKey.addEventListener('click', () => {
      const currentKey = doctorRoomInput ? doctorRoomInput.value : '';
      if (currentKey) {
        navigator.clipboard.writeText(currentKey);
        btnCopyLobbyKey.innerHTML = '<i class="fa-solid fa-check"></i>';
        setTimeout(() => btnCopyLobbyKey.innerHTML = '<i class="fa-solid fa-copy"></i>', 2000);
      }
    });
  }

  updateExpiryDisplay();
  generateNewRoomKey();

  // 3. Tab Switching: Doctor vs Patient
  if (tabDoctor && tabPatient) {
    tabDoctor.addEventListener('click', () => {
      userRole = 'doctor';
      tabDoctor.classList.add('active');
      tabPatient.classList.remove('active');
      panelDoctor.style.display = 'flex';
      panelPatient.style.display = 'none';
    });

    tabPatient.addEventListener('click', () => {
      userRole = 'patient';
      tabPatient.classList.add('active');
      tabDoctor.classList.remove('active');
      panelPatient.style.display = 'flex';
      panelDoctor.style.display = 'none';
    });
  }


  // 4. URL Parameter Parsing (Auto-Join as Patient)
  const urlParams = new URLSearchParams(window.location.search);
  const roomFromUrl = urlParams.get('room') || urlParams.get('join');
  if (roomFromUrl) {
    userRole = 'patient';
    if (patientRoomInput) patientRoomInput.value = roomFromUrl.toUpperCase();
    if (tabPatient) tabPatient.click();
    
    setTimeout(() => {
      startSession('patient');
    }, 400);
  }

  // 5. Start Session Action
  if (btnCreateRoom) btnCreateRoom.addEventListener('click', () => startSession('doctor'));
  if (btnJoinAsPatient) btnJoinAsPatient.addEventListener('click', () => startSession('patient'));

  async function startSession(role) {
    userRole = role;
    if (role === 'doctor') {
      currentRoomId = doctorRoomInput.value.trim().toUpperCase() || 'DOC-8921';
      if (localPipLabel) localPipLabel.textContent = 'Doctor (You)';
    } else {
      currentRoomId = patientRoomInput.value.trim().toUpperCase();
      if (!currentRoomId) {
        alert('Please enter a valid Room Key (e.g. DOC-8921)!');
        return;
      }
      if (localPipLabel) localPipLabel.textContent = 'Patient (You)';
    }

    if (displayRoomKey) displayRoomKey.textContent = currentRoomId;
    if (activeRoomTag) activeRoomTag.textContent = currentRoomId;
    if (connectingTitle) connectingTitle.textContent = role === 'doctor' ? 'Waiting for Patient / VR...' : 'Connecting to Doctor...';

    if (startScreen) startScreen.style.display = 'none';
    if (callScreen) callScreen.style.display = 'flex';

    const dbUrl = (customFirebaseInput && customFirebaseInput.value.trim().startsWith('https://'))
      ? customFirebaseInput.value.trim()
      : DEFAULT_FIREBASE_DB_URL;

    await initNativeWebRTC(role, dbUrl);
  }

  
// ==========================================
// AETHERCARE MESH MANAGER BACKEND
// ==========================================
// ---------------------------------------------------------------------------
// Config — TURN is what actually gets media through across networks/mobile
// data (see docs/ARCHITECTURE_AND_SETUP.md §1, §8). The actual mode is
// picked centrally in config.js (AETHERCARE_CONFIG.turnMode) — see that
// file's comments for which mode fits which of your scenarios.
// ---------------------------------------------------------------------------

const STUN_ONLY = [
  { urls: 'stun:stun.l.google.com:19302' },
  { urls: 'stun:stun.cloudflare.com:3478' },
];

const OPENRELAY_FALLBACK = [
  ...STUN_ONLY,
  { urls: 'turn:openrelay.metered.ca:80', username: 'openrelayproject', credential: 'openrelayproject' },
  { urls: 'turn:openrelay.metered.ca:443', username: 'openrelayproject', credential: 'openrelayproject' },
  { urls: 'turn:openrelay.metered.ca:443?transport=tcp', username: 'openrelayproject', credential: 'openrelayproject' },
];

// Fetches a fresh ICE server list according to AETHERCARE_CONFIG.turnMode.
// Called once per room join (not cached globally) so Cloudflare mode always
// gets a freshly-minted, non-expired credential for that session.
async function resolveIceServers() {
  const cfg = window.AETHERCARE_CONFIG || {};
  const mode = cfg.turnMode || 'openrelay';

  if (mode === 'none') return STUN_ONLY;

  if (mode === 'cloudflare') {
    if (!cfg.turnCredentialEndpoint) {
      console.warn('[AetherCare] turnMode is "cloudflare" but turnCredentialEndpoint is empty in config.js — falling back to OpenRelay. See cloudflare-worker/README.md.');
      return OPENRELAY_FALLBACK;
    }
    try {
      const res = await fetch(cfg.turnCredentialEndpoint);
      if (!res.ok) throw new Error('Worker returned ' + res.status);
      const data = await res.json();
      if (!data.iceServers || !data.iceServers.length) throw new Error('Worker response had no iceServers');
      // Keep a STUN fallback alongside Cloudflare's servers — cheap and harmless.
      return [...STUN_ONLY, ...data.iceServers];
    } catch (e) {
      console.warn('[AetherCare] Could not fetch Cloudflare TURN credentials (' + e.message + ') — falling back to OpenRelay for this session.');
      return OPENRELAY_FALLBACK;
    }
  }

  // mode === 'openrelay', or anything unrecognized
  return OPENRELAY_FALLBACK;
}

const $ = (id) => document.getElementById(id);
const uid = () => (crypto.randomUUID ? crypto.randomUUID().slice(0, 8) : Math.random().toString(36).slice(2, 10));
const randomRoomKey = () => 'ROOM-' + Math.floor(1000 + Math.random() * 9000);

function toast(msg, ms = 2200) {
  const el = $('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(toast._t);
  toast._t = setTimeout(() => el.classList.remove('show'), ms);
}

// ---------------------------------------------------------------------------
// Signaling adapters — both expose the same tiny interface:
//   connect({room, peerId, meta})
//   onRoster(cb)  cb(peerIds[])
//   onSignal(cb)  cb(fromPeerId, data)
//   sendSignal(toPeerId, data)
//   disconnect()
// Because the interface is identical, MeshManager never knows or cares
// which backend is behind it — same code path for cloud or LAN mode.
// ---------------------------------------------------------------------------

class FirebaseSignaling {
  constructor(databaseUrl) {
    this.databaseUrl = databaseUrl;
    this._rosterCb = null;
    this._signalCb = null;
  }

  connect({ room, peerId, meta }) {
    return new Promise((resolve, reject) => {
      try {
        if (!firebase.apps.length) {
          firebase.initializeApp({ databaseURL: this.databaseUrl });
        } else if (firebase.apps[0].options.databaseURL !== this.databaseUrl) {
          firebase.app().delete().finally(() => firebase.initializeApp({ databaseURL: this.databaseUrl }));
        }
        this.db = firebase.database();
        this.room = room;
        this.peerId = peerId;

        this.peersRef = this.db.ref(`rooms/${room}/peers`);
        this.myPeerRef = this.peersRef.child(peerId);
        this.mailboxRef = this.db.ref(`rooms/${room}/mailbox/${peerId}`);

        this.myPeerRef.set({ ...meta, joinedAt: Date.now() });
        this.myPeerRef.onDisconnect().remove();

        this.peersRef.on('value', (snap) => {
          const val = snap.val() || {};
          const ids = Object.keys(val).filter((id) => id !== peerId);
          const rolesById = {};
          const namesById = {};
          ids.forEach((id) => {
            rolesById[id] = (val[id] && val[id].role) || 'peer';
            namesById[id] = (val[id] && val[id].name) || '';
          });
          if (this._rosterCb) this._rosterCb(ids, rolesById, namesById);
        });

        this.mailboxRef.on('child_added', (snap) => {
          const msg = snap.val();
          snap.ref.remove(); // one-shot mailbox, avoid replay on reconnect
          if (msg && this._signalCb) this._signalCb(msg.from, msg.data);
        });

        resolve();
      } catch (e) {
        reject(e);
      }
    });
  }

  onRoster(cb) { this._rosterCb = cb; }
  onSignal(cb) { this._signalCb = cb; }

  sendSignal(toPeerId, data) {
    this.db.ref(`rooms/${this.room}/mailbox/${toPeerId}`).push({ from: this.peerId, data, ts: Date.now() });
  }

  disconnect() {
    if (this.myPeerRef) this.myPeerRef.remove();
    if (this.peersRef) this.peersRef.off();
    if (this.mailboxRef) this.mailboxRef.off();
  }
}

class LocalServerSignaling {
  constructor(wsUrl) {
    this.wsUrl = wsUrl;
    this._rosterCb = null;
    this._signalCb = null;
  }

  connect({ room, peerId, meta }) {
    return new Promise((resolve, reject) => {
      this.room = room;
      this.peerId = peerId;
      this.ws = new WebSocket(this.wsUrl);

      this.ws.onopen = () => {
        this.ws.send(JSON.stringify({ type: 'join', room, peerId, meta }));
      };

      this.ws.onmessage = (evt) => {
        const msg = JSON.parse(evt.data);
        if (msg.type === 'joined') resolve();
        else if (msg.type === 'roster' && this._rosterCb) this._rosterCb(msg.peers || [], msg.roles || {}, msg.names || {});
        else if (msg.type === 'signal' && this._signalCb) this._signalCb(msg.from, msg.data);
        else if (msg.type === 'ping') this.ws.send(JSON.stringify({ type: 'pong' }));
      };

      this.ws.onerror = () => reject(new Error('Could not reach local signaling server at ' + this.wsUrl));
      this.ws.onclose = () => { /* peers list will settle via the other side's leave detection */ };
    });
  }

  onRoster(cb) { this._rosterCb = cb; }
  onSignal(cb) { this._signalCb = cb; }

  sendSignal(toPeerId, data) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ type: 'signal', to: toPeerId, data }));
    }
  }

  disconnect() {
    if (this.ws) { try { this.ws.send(JSON.stringify({ type: 'leave' })); } catch (_) { } this.ws.close(); }
  }
}

// ---------------------------------------------------------------------------
// Mesh manager — one RTCPeerConnection per remote peer. Initiator for each
// pair is decided deterministically (lower peerId offers) so both sides
// never race to create duplicate offers.
// ---------------------------------------------------------------------------

class MeshManager {
  constructor({ signaling, myId, myRole, topology, localStream, iceServers, callbacks }) {
    this.signaling = signaling;
    this.myId = myId;
    this.myRole = myRole || 'peer';       // 'peer' | 'hub' | 'spoke'
    this.topology = topology || 'mesh';   // 'mesh' | 'hub-spoke'
    this.localStream = localStream;
    this.iceServers = iceServers || OPENRELAY_FALLBACK;
    this.cb = callbacks;
    this.pcs = new Map();
    this.dataChannels = new Map();
    this.peerRoles = new Map();
    this.statsTimer = null;

    signaling.onSignal((from, data) => this._handleSignal(from, data));
    signaling.onRoster((ids, rolesById, namesById) => {
      // Store peer names before reconciling so new tiles get the correct name.
      if (namesById) {
        Object.entries(namesById).forEach(([id, name]) => {
          if (name) {
            // state.peerNames.set(id, name);
            // updateTileNameIfExists(id, name);
          }
        });
      }
      this._reconcileRoster(ids, rolesById);
    });

    this.statsTimer = setInterval(() => this._pollConnectionTypes(), 3000);
  }

  // In 'mesh' topology every peer connects to every other peer — right for
  // 1-to-1 and small groups. In 'hub-spoke' topology (classroom / Unity
  // simulation with many students, one instructor), a spoke ONLY connects
  // to the hub, never to other spokes, so each spoke opens exactly one
  // connection no matter how many students are in the room. This is what
  // lets the classroom case scale well past mesh's ~4-6 peer ceiling.
  _shouldConnectTo(peerId) {
    if (this.topology !== 'hub-spoke') return true;
    if (this.myRole === 'hub') return true; // hub connects to everyone
    const theirRole = this.peerRoles.get(peerId);
    return theirRole === 'hub'; // spokes only connect to the hub
  }

  _reconcileRoster(ids, rolesById) {
    if (rolesById) this.peerRoles = new Map(Object.entries(rolesById));
    const idSet = new Set(ids);
    for (const id of ids) {
      if (!this._shouldConnectTo(id)) continue;
      if (!this.pcs.has(id)) {
        this._createPeer(id, this.myId < id);
        // const name = state.peerNames.get(id) || id.slice(0, 8);
        // appendSystemChatMessage(`🟢 ${name} joined the room`);
        // attachRemoteStream(id, null); // Render tile immediately with avatar badge
      }
    }
    for (const id of Array.from(this.pcs.keys())) {
      if (!idSet.has(id) || !this._shouldConnectTo(id)) this._removePeer(id);
    }
  }

  _createPeer(peerId, iAmInitiator) {
    const pc = new RTCPeerConnection({ iceServers: this.iceServers });
    this.pcs.set(peerId, pc);
    this.cb.onPeerState(peerId, 'pending');

    if (this.localStream) {
      for (const track of this.localStream.getTracks()) pc.addTrack(track, this.localStream);
    }

    pc.onicecandidate = (e) => {
      if (e.candidate) this.signaling.sendSignal(peerId, { kind: 'ice', candidate: e.candidate.toJSON() });
    };

    pc.ontrack = (e) => {
      let stream = (e.streams && e.streams[0]) ? e.streams[0] : null;
      if (!stream) {
        if (!this.remoteStreams) this.remoteStreams = new Map();
        stream = this.remoteStreams.get(peerId);
        if (!stream) {
          stream = new MediaStream();
          this.remoteStreams.set(peerId, stream);
        }
        if (!stream.getTracks().some(t => t.id === e.track.id)) {
          stream.addTrack(e.track);
        }
      }
      this.cb.onRemoteTrack(peerId, stream);
    };

    pc.onconnectionstatechange = () => {
      if (pc.connectionState === 'connected') this.cb.onPeerState(peerId, 'direct'); // refined by stats poll
      else if (pc.connectionState === 'connecting') this.cb.onPeerState(peerId, 'pending');
      else if (['failed', 'disconnected', 'closed'].includes(pc.connectionState)) this.cb.onPeerState(peerId, 'pending');
    };

    pc.ondatachannel = (e) => this._wireDataChannel(peerId, e.channel);

    if (iAmInitiator) {
      const dc = pc.createDataChannel('chat');
      this._wireDataChannel(peerId, dc);

      pc.createOffer()
        .then((offer) => pc.setLocalDescription(offer))
        .then(() => this.signaling.sendSignal(peerId, { kind: 'offer', sdp: pc.localDescription.sdp }))
        .catch((e) => console.error('offer failed', e));
    }

    return pc;
  }

  _wireDataChannel(peerId, dc) {
    this.dataChannels.set(peerId, dc);
    dc.onmessage = (e) => {
      try {
        const msg = JSON.parse(e.data);
        this.cb.onChatMessage(peerId, msg);
      } catch (_) { }
    };
  }

  async _handleSignal(from, data) {
    if (data.kind === 'host-closed') {
      toast('The Host closed the room session.');
      leaveCall(false);
      return;
    }

    let pc = this.pcs.get(from);
    if (!pc) pc = this._createPeer(from, false); // reactive create if roster event hasn't arrived yet

    if (data.kind === 'offer') {
      await pc.setRemoteDescription({ type: 'offer', sdp: data.sdp });
      if (pc._iceQueue) {
        for (const c of pc._iceQueue) pc.addIceCandidate(c).catch(e => console.warn('queued ICE fail', e));
        pc._iceQueue = null;
      }
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      this.signaling.sendSignal(from, { kind: 'answer', sdp: pc.localDescription.sdp });
    } else if (data.kind === 'answer') {
      await pc.setRemoteDescription({ type: 'answer', sdp: data.sdp });
      if (pc._iceQueue) {
        for (const c of pc._iceQueue) pc.addIceCandidate(c).catch(e => console.warn('queued ICE fail', e));
        pc._iceQueue = null;
      }
    } else if (data.kind === 'ice') {
      if (pc.remoteDescription) {
        try { await pc.addIceCandidate(data.candidate); } catch (e) { console.warn('ICE add failed', e); }
      } else {
        if (!pc._iceQueue) pc._iceQueue = [];
        pc._iceQueue.push(data.candidate);
      }
    }
  }

  broadcastSignal(data) {
    for (const peerId of this.pcs.keys()) {
      this.signaling.sendSignal(peerId, data);
    }
  }

  _removePeer(peerId) {
    // const name = state.peerNames.get(peerId) || peerId.slice(0, 8);
    // appendSystemChatMessage(`🔴 ${name} left the room`);
    const pc = this.pcs.get(peerId);
    if (pc) pc.close();
    this.pcs.delete(peerId);
    this.dataChannels.delete(peerId);
    this.cb.onPeerLeft(peerId);
  }

  async _pollConnectionTypes() {
    for (const [peerId, pc] of this.pcs.entries()) {
      if (pc.connectionState !== 'connected') continue;
      try {
        const stats = await pc.getStats();
        let selectedPair = null;
        stats.forEach((report) => {
          if (report.type === 'transport' && report.selectedCandidatePairId) {
            selectedPair = stats.get(report.selectedCandidatePairId);
          }
        });
        if (!selectedPair) {
          stats.forEach((report) => { if (report.type === 'candidate-pair' && report.state === 'succeeded') selectedPair = report; });
        }
        if (selectedPair) {
          const local = stats.get(selectedPair.localCandidateId);
          const remote = stats.get(selectedPair.remoteCandidateId);
          const isRelay = (local && local.candidateType === 'relay') || (remote && remote.candidateType === 'relay');
          this.cb.onPeerState(peerId, isRelay ? 'relay' : 'direct');
        }
      } catch (_) { /* getStats shape varies by browser, degrade quietly */ }
    }
  }

  broadcastChat(text) {
    const payload = JSON.stringify({ text, ts: Date.now() });
    for (const dc of this.dataChannels.values()) {
      if (dc.readyState === 'open') dc.send(payload);
    }
  }

  replaceVideoTrack(newTrack) {
    for (const pc of this.pcs.values()) {
      const sender = pc.getSenders().find((s) => s.track && s.track.kind === 'video');
      if (sender) sender.replaceTrack(newTrack);
    }
  }

  replaceAudioTrack(newTrack) {
    for (const pc of this.pcs.values()) {
      const sender = pc.getSenders().find((s) => s.track && s.track.kind === 'audio');
      if (sender) sender.replaceTrack(newTrack);
    }
  }

  destroyAll() {
    clearInterval(this.statsTimer);
    for (const pc of this.pcs.values()) pc.close();
    this.pcs.clear();
    this.dataChannels.clear();
  }
}

// ---------------------------------------------------------------------------
// App state + UI wiring
// ---------------------------------------------------------------------------

const state = {
  myId: uid(),
  displayName: '',
  peerType: 'web',
  callMode: 'video',
  signalingKind: (window.AETHERCARE_CONFIG && window.AETHERCARE_CONFIG.defaultSignaling) || 'firebase',
  topology: (window.AETHERCARE_CONFIG && window.AETHERCARE_CONFIG.defaultTopology) || 'mesh',
  myRole: (window.AETHERCARE_CONFIG && window.AETHERCARE_CONFIG.defaultRole) || 'peer',
  room: null,
  localStream: null,
  mesh: null,
  signaling: null,
  peerNames: new Map(),   // peerId -> display name
  peerTypes: new Map(),   // peerId -> 'web' | 'vr'
  peerAvatars: new Map(), // peerId -> { initials, color }
  peerStates: new Map(),  // peerId -> 'pending' | 'direct' | 'relay'
  chatOpen: false,
  unreadChat: 0,
  facingMode: 'user',
};

// --- Avatar helpers ----------------------------------------------------------
const AVATAR_COLORS = [
  '#2dd4bf', '#818cf8', '#f472b6', '#fb923c', '#34d399', '#60a5fa', '#a78bfa', '#f87171'
];


// ==========================================
// AETHERCARE ADAPTER
// ==========================================
let meshManager = null;
let signaling = null;
let localStreamRef = null;

async function initNativeWebRTC(role, dbUrl) {
    console.log(`🔥 Initializing AetherCare Engine [Role: ${role}]...`);
    
    // Resolve STUN/TURN servers via config (uses AETHERCARE_CONFIG.turnMode)
    const iceServers = await resolveIceServers();
    
    signaling = new FirebaseSignaling(dbUrl);
    
    // Attempt local media
    await acquireLocalCamera();
    
    // Connect to room
    try {
        await signaling.connect({
            room: currentRoomId,
            peerId: uid(),
            meta: { name: userRole === 'doctor' ? 'Doctor' : 'Patient', type: 'web', role: role === 'doctor' ? 'hub' : 'spoke' }
        });
    } catch (e) {
        alert('Signaling connection failed: ' + e.message);
        return;
    }
    
    meshManager = new MeshManager({
        signaling: signaling,
        myId: signaling.peerId,
        myRole: role === 'doctor' ? 'hub' : 'spoke',
        topology: 'hub-spoke', // Hub and spoke fits doctor(hub)-patient(spoke) nicely
        localStream: localStreamRef,
        iceServers: iceServers,
        callbacks: {
            onRemoteTrack: (peerId, stream) => {
                console.log("🎉 Remote Stream Received!");
                const remoteVideo = document.getElementById('remoteVideo');
                if (remoteVideo && remoteVideo.srcObject !== stream) {
                    remoteVideo.srcObject = stream;
                }
                const connectingOverlay = document.getElementById('connectingOverlay');
                if (connectingOverlay) connectingOverlay.style.display = 'none';
            },
            onRemoteTrackRemoved: (peerId, trackId) => {},
            onChatMessage: (peerId, msg) => {},
            onPeerState: (peerId, state, role) => {
                console.log("🌐 Connection state for", peerId, ":", state);
            },
            onTopologyUpdate: (map) => {},
            onPeerLeft: (peerId) => {
                console.log("🔴 Peer Left:", peerId);
            }
        }
    });
}

// Override acquireLocalCamera to save stream to localStreamRef
async function acquireLocalCamera() {
    try {
      localStreamRef = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: facingMode, width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: true
      });
      const localVideo = document.getElementById('localVideo');
      if (localVideo) {
          localVideo.srcObject = localStreamRef;
          if (facingMode === 'user') {
              localVideo.style.transform = 'scaleX(-1)'; // Selfie flip!
          } else {
              localVideo.style.transform = 'none';
          }
      }
    } catch (err) {
      console.warn("Camera busy or blocked. Using synthetic stream fallback...", err);
      localStreamRef = createFallbackCanvasStream();
      const localVideo = document.getElementById('localVideo');
      if (localVideo) localVideo.srcObject = localStreamRef;
    }
}

function createFallbackCanvasStream() {
    const canvas = document.createElement('canvas');
    canvas.width = 640;
    canvas.height = 360;
    const ctx = canvas.getContext('2d');
    
    let hue = userRole === 'doctor' ? 180 : 120;
    setInterval(() => {
      ctx.fillStyle = `hsl(${hue}, 60%, 15%)`;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#ffffff';
      ctx.font = 'bold 22px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(userRole === 'doctor' ? 'Doctor Webcam Stream' : 'Patient/VR Stream', canvas.width / 2, canvas.height / 2);
    }, 100);

    return canvas.captureStream(30);
}



  // Copy Room Key
  if (btnCopyKey) {
    btnCopyKey.addEventListener('click', () => {
      navigator.clipboard.writeText(currentRoomId);
      btnCopyKey.innerHTML = '<i class="fa-solid fa-check"></i> Key Copied!';
      setTimeout(() => btnCopyKey.innerHTML = '<i class="fa-solid fa-copy"></i> Copy Key', 2000);
    });
  }

  // Controls Logic
  if (btnToggleMic) {
    btnToggleMic.addEventListener('click', () => {
      if (!localStreamRef) return;
      isAudioMuted = !isAudioMuted;
      localStreamRef.getAudioTracks().forEach(t => t.enabled = !isAudioMuted);
      btnToggleMic.classList.toggle('off', isAudioMuted);
      btnToggleMic.innerHTML = `<i class="fa-solid ${isAudioMuted ? 'fa-microphone-slash' : 'fa-microphone'}"></i>`;
    });
  }

  if (btnToggleCam) {
    btnToggleCam.addEventListener('click', () => {
      if (!localStreamRef) return;
      isVideoOff = !isVideoOff;
      localStreamRef.getVideoTracks().forEach(t => t.enabled = !isVideoOff);
      btnToggleCam.classList.toggle('off', isVideoOff);
      btnToggleCam.innerHTML = `<i class="fa-solid ${isVideoOff ? 'fa-video-slash' : 'fa-video'}"></i>`;
    });
  }

  if (btnFlipCam) {
    btnFlipCam.addEventListener('click', async () => {
      facingMode = facingMode === 'user' ? 'environment' : 'user';
      if (localStreamRef) localStreamRef.getTracks().forEach(t => t.stop());
      await acquireLocalCamera();
      // Inform MeshManager of track change
      if (meshManager && localStreamRef) {
          const videoTrack = localStreamRef.getVideoTracks()[0];
          const audioTrack = localStreamRef.getAudioTracks()[0];
          if (videoTrack) meshManager.replaceVideoTrack(videoTrack);
          if (audioTrack) meshManager.replaceAudioTrack(audioTrack);
          
          // Apply current mute state to the new track
          if (audioTrack) audioTrack.enabled = !isAudioMuted;
          if (videoTrack) videoTrack.enabled = !isVideoOff;
      }
    });
  }

  if (btnEndSession) {
    btnEndSession.addEventListener('click', () => {
      if (meshManager) meshManager.destroyAll();
      if (signaling) signaling.disconnect();
      if (localStreamRef) localStreamRef.getTracks().forEach(t => t.stop());
      window.location.href = window.location.pathname;
    });
  }
});
