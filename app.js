// Multi-Signaling WebRTC Logic for Doctor VR App (Firebase / PeerJS / Dual Web Device / Auto-Join QR Code)

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
  const roomInput = document.getElementById('roomInput');
  const signalingModeSelect = document.getElementById('signalingModeSelect');
  const firebaseConfigBox = document.getElementById('firebaseConfigBox');
  const firebaseUrlInput = document.getElementById('firebaseUrlInput');
  const btnGenerateKey = document.getElementById('btnGenerateKey');
  const btnJoinRoom = document.getElementById('btnJoinRoom');
  const expiryTimeText = document.getElementById('expiryTimeText');
  const displayRoomKey = document.getElementById('displayRoomKey');
  const activeRoomTag = document.getElementById('activeRoomTag');
  const btnCopyLink = document.getElementById('btnCopyLink');
  const qrCodeImg = document.getElementById('qrCodeImg');
  const qrLinkText = document.getElementById('qrLinkText');
  
  const localDoctorVideo = document.getElementById('localDoctorVideo');
  const remoteVrVideo = document.getElementById('remoteVrVideo');
  const vrConnectingOverlay = document.getElementById('vrConnectingOverlay');

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
  let isAudioMuted = false;
  let isVideoOff = false;
  let facingMode = 'user';

  // Parse URL Parameters to check if opening via scanned QR Code or shared link
  const urlParams = new URLSearchParams(window.location.search);
  const roomFromUrl = urlParams.get('room') || urlParams.get('join');
  let isAutoJoin = false;

  if (roomFromUrl && roomInput) {
    roomInput.value = roomFromUrl.toUpperCase();
    isAutoJoin = true;
  }

  // Generate & Render QR Code for Room URL
  function updateQRCode() {
    if (!roomInput || !qrCodeImg || !qrLinkText) return;
    const roomId = roomInput.value.trim().toUpperCase() || 'DOC-8921';
    
    // Construct full shareable URL
    const baseUrl = window.location.origin + window.location.pathname;
    const shareableUrl = `${baseUrl}?room=${roomId}`;

    qrLinkText.textContent = shareableUrl;

    // Free QR Server API
    const qrApiUrl = `https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=${encodeURIComponent(shareableUrl)}`;
    qrCodeImg.src = qrApiUrl;
  }

  if (roomInput) {
    roomInput.addEventListener('input', updateQRCode);
  }

  // Toggle Config Boxes based on Mode
  if (signalingModeSelect) {
    signalingModeSelect.addEventListener('change', (e) => {
      const mode = e.target.value;
      if (firebaseConfigBox) {
        firebaseConfigBox.style.display = mode === 'firebase' ? 'flex' : 'none';
      }
    });
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

  updateExpiryDisplay();
  updateQRCode();

  // 2. Generate Random Room Key
  if (btnGenerateKey && roomInput) {
    btnGenerateKey.addEventListener('click', () => {
      const rand = Math.floor(1000 + Math.random() * 9000);
      roomInput.value = `DOC-${rand}`;
      updateQRCode();
    });
  }

  // 3. Share / Copy Room Shareable Link
  if (btnCopyLink) {
    btnCopyLink.addEventListener('click', async () => {
      const baseUrl = window.location.origin + window.location.pathname;
      const shareableUrl = `${baseUrl}?room=${currentRoomId}`;

      if (navigator.share) {
        try {
          await navigator.share({
            title: 'Join VR Consultation',
            text: `Join VR Consultation Session with Room Key: ${currentRoomId}`,
            url: shareableUrl
          });
          return;
        } catch (e) {
          // Fallback to clipboard
        }
      }

      if (navigator.clipboard) {
        await navigator.clipboard.writeText(shareableUrl);
        btnCopyLink.innerHTML = '<i class="fa-solid fa-check"></i> Link Copied!';
        setTimeout(() => btnCopyLink.innerHTML = '<i class="fa-solid fa-share-nodes"></i> Share Link', 2000);
      }
    });
  }

  // 4. Start Doctor Session
  async function startSession() {
    currentRoomId = (roomInput ? roomInput.value.trim().toUpperCase() : '') || 'DOC-8921';
    const mode = signalingModeSelect ? signalingModeSelect.value : 'peerjs';

    if (displayRoomKey) displayRoomKey.textContent = currentRoomId;
    if (activeRoomTag) activeRoomTag.textContent = currentRoomId;

    if (startScreen) startScreen.style.display = 'none';
    if (callScreen) callScreen.style.display = 'flex';

    if (mode === 'firebase') {
      await initFirebaseWebRTC();
    } else {
      await initPeerJS();
    }
  }

  if (btnJoinRoom) {
    btnJoinRoom.addEventListener('click', startSession);
  }

  // AUTO-JOIN IF SCANNED FROM QR CODE OR OPENED VIA SHARED LINK
  if (isAutoJoin) {
    console.log("🔗 Auto-joining room from QR code / link:", roomFromUrl);
    setTimeout(startSession, 300);
  }

  // --- MODE A: FIREBASE SIGNALLING ---
  async function initFirebaseWebRTC() {
    const firebaseUrl = firebaseUrlInput ? firebaseUrlInput.value.trim() : '';
    if (!firebaseUrl || !firebaseUrl.startsWith('https://')) {
      alert('Please enter a valid Firebase Database URL (starting with https://)!');
      return;
    }

    try {
      if (typeof firebase !== 'undefined' && !firebase.apps.length) {
        firebase.initializeApp({ databaseURL: firebaseUrl });
      }
      if (typeof firebase !== 'undefined') {
        firebaseDb = firebase.database();
      }
    } catch (e) {
      console.warn("Firebase Init Exception:", e);
    }

    pc = new RTCPeerConnection(ICE_SERVERS);
    await acquireLocalCamera();

    pc.ontrack = (event) => {
      console.log("Remote Video Stream Received!", event.streams);
      if (event.streams && event.streams[0] && remoteVrVideo) {
        remoteVrVideo.srcObject = event.streams[0];
        if (vrConnectingOverlay) vrConnectingOverlay.style.display = 'none';
      }
    };

    if (!firebaseDb) return;

    const roomRef = firebaseDb.ref(`telemedicine_rooms/${currentRoomId}`);
    const doctorOffersRef = roomRef.child('offers');
    const vrAnswersRef = roomRef.child('answers');
    const doctorCandidatesRef = roomRef.child('doctor_candidates');
    const vrCandidatesRef = roomRef.child('vr_candidates');

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        doctorCandidatesRef.push(event.candidate.toJSON());
      }
    };

    const existingOfferSnap = await doctorOffersRef.once('value');
    const existingOffer = existingOfferSnap.val();

    if (!existingOffer) {
      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);
      await doctorOffersRef.set({ sdp: offer.sdp, type: offer.type, timestamp: Date.now() });

      vrAnswersRef.on('value', async (snapshot) => {
        const data = snapshot.val();
        if (data && data.sdp && !pc.currentRemoteDescription) {
          const rsd = new RTCSessionDescription(data);
          await pc.setRemoteDescription(rsd);
        }
      });

      vrCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try { await pc.addIceCandidate(new RTCIceCandidate(candidateData)); } catch (e) {}
        }
      });
    } else {
      await pc.setRemoteDescription(new RTCSessionDescription(existingOffer));
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      await vrAnswersRef.set({ sdp: answer.sdp, type: answer.type, timestamp: Date.now() });

      pc.onicecandidate = (event) => {
        if (event.candidate) {
          vrCandidatesRef.push(event.candidate.toJSON());
        }
      };

      doctorCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try { await pc.addIceCandidate(new RTCIceCandidate(candidateData)); } catch (e) {}
        }
      });
    }
  }

  // --- MODE B: PEERJS SIGNALLING (Default zero-cloud option) ---
  async function initPeerJS() {
    await acquireLocalCamera();

    if (typeof Peer === 'undefined') {
      alert("PeerJS SDK failed to load!");
      return;
    }

    peerInstance = new Peer(`${currentRoomId}-doctor`, { config: ICE_SERVERS });

    peerInstance.on('open', (id) => {
      console.log("PeerJS Doctor Ready ID:", id);
    });

    peerInstance.on('call', (call) => {
      call.answer(localStream);
      call.on('stream', (vrStream) => {
        if (remoteVrVideo) remoteVrVideo.srcObject = vrStream;
        if (vrConnectingOverlay) vrConnectingOverlay.style.display = 'none';
      });
    });

    peerInstance.on('error', (err) => {
      if (err.type === 'unavailable-id') {
        const peer2 = new Peer({ config: ICE_SERVERS });
        peer2.on('open', () => {
          const call = peer2.call(`${currentRoomId}-doctor`, localStream);
          call.on('stream', (vrStream) => {
            if (remoteVrVideo) remoteVrVideo.srcObject = vrStream;
            if (vrConnectingOverlay) vrConnectingOverlay.style.display = 'none';
          });
        });
      }
    });
  }

  async function acquireLocalCamera() {
    try {
      localStream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: facingMode, width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: true
      });
      if (localDoctorVideo) localDoctorVideo.srcObject = localStream;
      if (pc) {
        localStream.getTracks().forEach(track => pc.addTrack(track, localStream));
      }
    } catch (err) {
      console.error("Camera error:", err);
    }
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
      window.location.href = window.location.pathname; // Clear room query param
    });
  }
});
