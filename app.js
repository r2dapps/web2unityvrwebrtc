// Web2RVR WebRTC Engine - Proven PeerJS + Firebase Presence Architecture (From WalkieTalkie Engine)

const FIREBASE_DB_URL = 'https://web2rvrwebrtc-default-rtdb.firebaseio.com';

const ICE_SERVERS = [
  { urls: 'stun:stun.l.google.com:19302' },
  { urls: 'stun:stun1.l.google.com:19302' },
  { urls: 'stun:stun.cloudflare.com:3478' }
];

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
  let peer = null;
  let firebaseDb = null;
  let localStream = null;
  let myPeerId = '';
  let currentRoomId = '';
  let userRole = 'doctor';
  let isAudioMuted = false;
  let isVideoOff = false;
  let facingMode = 'user';
  let connectedCalls = {};
  let presenceRef = null;

  // Initialize Firebase Realtime DB
  try {
    if (typeof firebase !== 'undefined' && !firebase.apps.length) {
      firebase.initializeApp({ databaseURL: FIREBASE_DB_URL });
    }
    if (typeof firebase !== 'undefined') {
      firebaseDb = firebase.database();
    }
  } catch (e) {
    console.warn("Firebase Init Exception:", e);
  }

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

    await initPeerEngine(role);
  }

  // --- PROVEN WALKIE-TALKIE PEER & PRESENCE ENGINE ---
  async function initPeerEngine(role) {
    await acquireLocalCamera();

    if (typeof Peer === 'undefined') {
      alert("PeerJS SDK error. Please check your network connection.");
      return;
    }

    // Generate Unique Peer ID (e.g. w2r-DOC8921-doctor-x9a2k)
    const sRoom = currentRoomId.replace(/[^A-Z0-9]/g, '');
    const rand5 = Math.random().toString(36).substring(2, 7);
    myPeerId = `w2r-${sRoom}-${role}-${rand5}`;

    console.log("⚡ My Unique Peer ID:", myPeerId);

    peer = new Peer(myPeerId, {
      config: { iceServers: ICE_SERVERS },
      debug: 1
    });

    peer.on('open', (id) => {
      console.log("✅ Peer Connected to Server. ID:", id);
      registerPresenceAndListen();
    });

    // Handle Incoming Call
    peer.on('call', (call) => {
      console.log("📞 Incoming Peer Call from:", call.peer);
      call.answer(localStream);
      handleCallStream(call);
    });

    // Handle Incoming Data Connection
    peer.on('connection', (conn) => {
      console.log("💬 Incoming Data Connection from:", conn.peer);
      conn.on('open', () => {
        if (connectingOverlay) connectingOverlay.style.display = 'none';
      });
    });

    peer.on('error', (err) => {
      console.error("Peer Engine Error:", err);
    });
  }

  function handleCallStream(call) {
    connectedCalls[call.peer] = call;

    call.on('stream', (remoteStream) => {
      console.log("🎥 REMOTE STREAM BOND SUCCESSFUL! Received from:", call.peer);
      if (remoteVideo) {
        remoteVideo.srcObject = remoteStream;
      }
      if (connectingOverlay) {
        connectingOverlay.style.display = 'none';
      }
    });
  }

  function dialPeer(targetPeerId) {
    if (!peer || targetPeerId === myPeerId || connectedCalls[targetPeerId]) return;
    console.log("🚀 Dialing Target Peer:", targetPeerId);

    // Establish Data & Media Call
    const conn = peer.connect(targetPeerId, { reliable: true });
    if (conn) {
      conn.on('open', () => {
        if (connectingOverlay) connectingOverlay.style.display = 'none';
      });
    }

    const call = peer.call(targetPeerId, localStream);
    if (call) {
      handleCallStream(call);
    }
  }

  // Register Peer Presence in Firebase Room Directory
  function registerPresenceAndListen() {
    if (!firebaseDb) return;

    const sRoom = currentRoomId.replace(/[^A-Z0-9]/g, '');
    const roomPeersRef = firebaseDb.ref(`telemedicine_rooms/${sRoom}/peers`);
    presenceRef = roomPeersRef.child(myPeerId);

    // Register presence & cleanup on disconnect
    presenceRef.set({
      peerId: myPeerId,
      role: userRole,
      timestamp: Date.now()
    });
    presenceRef.onDisconnect().remove();

    // Listen for existing and new peers joining room
    roomPeersRef.on('child_added', (snapshot) => {
      const data = snapshot.val();
      if (data && data.peerId && data.peerId !== myPeerId) {
        console.log("👤 Room Presence Detected Peer:", data.peerId);
        dialPeer(data.peerId);
      }
    });

    roomPeersRef.on('child_removed', (snapshot) => {
      const data = snapshot.val();
      if (data && data.peerId && connectedCalls[data.peerId]) {
        delete connectedCalls[data.peerId];
      }
    });
  }

  async function acquireLocalCamera() {
    try {
      localStream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: facingMode, width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: true
      });
      if (localVideo) localVideo.srcObject = localStream;
    } catch (err) {
      console.warn("Camera busy or unavailable. Using synthetic fallback stream for testing...", err);
      localStream = createFallbackCanvasStream();
      if (localVideo) localVideo.srcObject = localStream;
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
      ctx.fillText(userRole === 'doctor' ? 'Doctor Stream' : 'Patient/VR Stream', canvas.width / 2, canvas.height / 2);
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
      if (!localStream) return;
      isAudioMuted = !isAudioMuted;
      localStream.getAudioTracks().forEach(t => t.enabled = !isAudioMuted);
      btnToggleMic.classList.toggle('off', isAudioMuted);
      btnToggleMic.innerHTML = `<i class="fa-solid ${isAudioMuted ? 'fa-microphone-slash' : 'fa-microphone'}"></i>`;
    });
  }

  if (btnToggleCam) {
    btnToggleCam.addEventListener('click', () => {
      if (!localStream) return;
      isVideoOff = !isVideoOff;
      localStream.getVideoTracks().forEach(t => t.enabled = !isVideoOff);
      btnToggleCam.classList.toggle('off', isVideoOff);
      btnToggleCam.innerHTML = `<i class="fa-solid ${isVideoOff ? 'fa-video-slash' : 'fa-video'}"></i>`;
    });
  }

  if (btnFlipCam) {
    btnFlipCam.addEventListener('click', async () => {
      facingMode = facingMode === 'user' ? 'environment' : 'user';
      if (localStream) localStream.getTracks().forEach(t => t.stop());
      await acquireLocalCamera();
    });
  }

  if (btnEndSession) {
    btnEndSession.addEventListener('click', () => {
      if (presenceRef) presenceRef.remove();
      if (peer) peer.destroy();
      if (localStream) localStream.getTracks().forEach(t => t.stop());
      window.location.href = window.location.pathname;
    });
  }
});
