// Web2RVR WebRTC Logic (Zero-Setup PeerJS Default + Optional Custom Firebase)

const ICE_SERVERS = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    { urls: 'stun:stun1.l.google.com:19302' },
    { urls: 'stun:stun.cloudflare.com:3478' }
  ]
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
  let peerInstance = null;
  let firebaseDb = null;
  let localStream = null;
  let currentRoomId = '';
  let userRole = 'doctor';
  let isAudioMuted = false;
  let isVideoOff = false;
  let facingMode = 'user';

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

    const customDbUrl = customFirebaseInput ? customFirebaseInput.value.trim() : '';

    if (customDbUrl && customDbUrl.startsWith('https://')) {
      // Use Custom Firebase Database if provided
      await initFirebaseWebRTC(role, customDbUrl);
    } else {
      // DEFAULT: Zero-Setup PeerJS Signaling (Works Out of the Box!)
      await initPeerJS(role);
    }
  }

  // --- ZERO-SETUP PEERJS ENGINE (Default) ---
  async function initPeerJS(role) {
    await acquireLocalCamera();

    if (typeof Peer === 'undefined') {
      alert("PeerJS SDK loading error. Please check your internet connection.");
      return;
    }

    const hostPeerId = `web2rvr-${currentRoomId}-host`;

    if (role === 'doctor') {
      // Doctor creates the host peer
      peerInstance = new Peer(hostPeerId, { config: ICE_SERVERS });

      peerInstance.on('open', (id) => {
        console.log("Doctor Host Peer Created:", id);
      });

      peerInstance.on('call', (call) => {
        console.log("Incoming call from Patient/VR!");
        call.answer(localStream);
        call.on('stream', (remoteStream) => {
          if (remoteVideo) remoteVideo.srcObject = remoteStream;
          if (connectingOverlay) connectingOverlay.style.display = 'none';
        });
      });

      peerInstance.on('error', (err) => {
        if (err.type === 'unavailable-id') {
          // Room ID already taken, recreate
          console.warn("Room ID already active.");
        }
      });
    } else {
      // Patient / VR joins the doctor host peer
      const joinerId = `web2rvr-${currentRoomId}-guest-${Math.floor(Math.random() * 1000)}`;
      peerInstance = new Peer(joinerId, { config: ICE_SERVERS });

      peerInstance.on('open', () => {
        console.log("Patient Joiner Peer Created. Calling Doctor Host:", hostPeerId);
        const call = peerInstance.call(hostPeerId, localStream);

        call.on('stream', (remoteStream) => {
          console.log("Connected to Doctor Stream!");
          if (remoteVideo) remoteVideo.srcObject = remoteStream;
          if (connectingOverlay) connectingOverlay.style.display = 'none';
        });
      });
    }
  }

  // --- OPTIONAL CUSTOM FIREBASE ENGINE ---
  async function initFirebaseWebRTC(role, dbUrl) {
    try {
      if (typeof firebase !== 'undefined' && !firebase.apps.length) {
        firebase.initializeApp({ databaseURL: dbUrl });
      }
      if (typeof firebase !== 'undefined') {
        firebaseDb = firebase.database();
      }
    } catch (e) {
      console.warn("Firebase Exception:", e);
    }

    pc = new RTCPeerConnection(ICE_SERVERS);
    await acquireLocalCamera();

    pc.ontrack = (event) => {
      if (event.streams && event.streams[0] && remoteVideo) {
        remoteVideo.srcObject = event.streams[0];
        if (connectingOverlay) connectingOverlay.style.display = 'none';
      }
    };

    if (!firebaseDb) return;

    const roomRef = firebaseDb.ref(`telemedicine_rooms/${currentRoomId}`);
    const offersRef = roomRef.child('offers');
    const answersRef = roomRef.child('answers');
    const doctorCandidatesRef = roomRef.child('doctor_candidates');
    const vrCandidatesRef = roomRef.child('vr_candidates');

    if (role === 'doctor') {
      pc.onicecandidate = (event) => {
        if (event.candidate) doctorCandidatesRef.push(event.candidate.toJSON());
      };

      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      await offersRef.set({ sdp: offer.sdp, type: offer.type, timestamp: Date.now() });

      answersRef.on('value', async (snapshot) => {
        const data = snapshot.val();
        if (data && data.sdp && !pc.currentRemoteDescription) {
          await pc.setRemoteDescription(new RTCSessionDescription(data));
        }
      });

      vrCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try { await pc.addIceCandidate(new RTCIceCandidate(candidateData)); } catch (e) {}
        }
      });
    } else {
      pc.onicecandidate = (event) => {
        if (event.candidate) vrCandidatesRef.push(event.candidate.toJSON());
      };

      const offerSnap = await offersRef.once('value');
      const offerData = offerSnap.val();

      if (!offerData || !offerData.sdp) {
        alert(`Room ${currentRoomId} not found! Please check if Doctor has created the room.`);
        location.reload();
        return;
      }

      await pc.setRemoteDescription(new RTCSessionDescription(offerData));
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);

      await answersRef.set({ sdp: answer.sdp, type: answer.type, timestamp: Date.now() });

      doctorCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try { await pc.addIceCandidate(new RTCIceCandidate(candidateData)); } catch (e) {}
        }
      });
    }
  }

  async function acquireLocalCamera() {
    try {
      localStream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: facingMode, width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: true
      });
      if (localVideo) localVideo.srcObject = localStream;
      if (pc) {
        localStream.getTracks().forEach(track => pc.addTrack(track, localStream));
      }
    } catch (err) {
      console.error("Camera error:", err);
    }
  }

  // 6. Copy Room Key
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
      if (pc) pc.close();
      if (peerInstance) peerInstance.destroy();
      if (localStream) localStream.getTracks().forEach(t => t.stop());
      window.location.href = window.location.pathname;
    });
  }
});
