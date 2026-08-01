// Pure Native WebRTC (RTCPeerConnection) + Real Firebase Signaling Engine with Auto-Cleanup

const DEFAULT_FIREBASE_DB_URL = 'https://walkietalkie-c0f03-default-rtdb.asia-southeast1.firebasedatabase.app';

const ICE_SERVERS = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    { urls: 'stun:stun1.l.google.com:19302' },
    { urls: 'stun:stun2.l.google.com:19302' },
    { urls: 'stun:stun.cloudflare.com:3478' },
    {
      urls: [
        'turn:openrelay.metered.ca:80',
        'turn:openrelay.metered.ca:443',
        'turn:openrelay.metered.ca:443?transport=tcp'
      ],
      username: 'openrelay',
      credential: 'openrelay'
    }
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

  // --- PURE NATIVE WEBRTC (RTCPeerConnection) + AUTOMATIC FIREBASE CLEANUP ---
  async function initNativeWebRTC(role, dbUrl) {
    console.log(`🔥 Initializing Native WebRTC [Role: ${role}] using Firebase DB:`, dbUrl);

    try {
      if (typeof firebase !== 'undefined' && !firebase.apps.length) {
        firebase.initializeApp({ databaseURL: dbUrl });
      }
      if (typeof firebase !== 'undefined') {
        firebaseDb = firebase.database();
      }
    } catch (e) {
      console.error("Firebase Init Error:", e);
    }

    pc = new RTCPeerConnection(ICE_SERVERS);
    
    pc.oniceconnectionstatechange = () => {
      console.log("🌐 ICE Connection State:", pc.iceConnectionState);
      if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
        if (connectingOverlay) connectingOverlay.style.display = 'none';
      } else if (pc.iceConnectionState === 'failed' || pc.iceConnectionState === 'disconnected') {
        console.warn("⚠️ ICE Connection disconnected or failed. Attempting ICE restart...");
        if (pc.restartIce) {
          pc.restartIce();
        }
      }
    };

    await acquireLocalCamera();

    if (localStream) {
      localStream.getTracks().forEach(track => pc.addTrack(track, localStream));
    }

    pc.ontrack = (event) => {
      console.log("🎉 SUCCESS! Native WebRTC Remote Track Received!", event.track.kind, event.streams);
      const stream = (event.streams && event.streams[0]) ? event.streams[0] : new MediaStream([event.track]);
      if (remoteVideo) {
        remoteVideo.srcObject = stream;
        remoteVideo.play().catch((e) => {
          console.warn("Autoplay blocked, attempting muted autoplay fallback:", e);
          remoteVideo.muted = true;
          remoteVideo.play().catch(() => {});
        });
      }
      if (connectingOverlay) connectingOverlay.style.display = 'none';
    };

    if (!firebaseDb) {
      alert("Firebase Database connection failed!");
      return;
    }

    const sRoom = currentRoomId.replace(/[^A-Z0-9]/g, '');
    activeRoomRef = firebaseDb.ref(`web2rvr_rooms/${sRoom}`);
    
    // AUTO CLEANUP: When Doctor closes browser tab, auto-delete room data from Firebase!
    if (role === 'doctor') {
      activeRoomRef.onDisconnect().remove();
    }

    const offerRef = activeRoomRef.child('offer');
    const answerRef = activeRoomRef.child('answer');
    const doctorCandidatesRef = activeRoomRef.child('doctor_candidates');
    const patientCandidatesRef = activeRoomRef.child('patient_candidates');

    if (role === 'doctor') {
      pc.onicecandidate = (event) => {
        if (event.candidate) {
          doctorCandidatesRef.push(event.candidate.toJSON());
        }
      };

      const offer = await pc.createOffer();
      await pc.setLocalDescription(offer);

      await offerRef.set({
        sdp: offer.sdp,
        type: offer.type,
        timestamp: Date.now()
      });

      answerRef.on('value', async (snapshot) => {
        const data = snapshot.val();
        if (data && data.sdp && !pc.currentRemoteDescription) {
          await pc.setRemoteDescription(new RTCSessionDescription(data));
        }
      });

      patientCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try {
            await pc.addIceCandidate(new RTCIceCandidate(candidateData));
          } catch (e) { console.error("ICE Candidate Error:", e); }
        }
      });

    } else {
      pc.onicecandidate = (event) => {
        if (event.candidate) {
          patientCandidatesRef.push(event.candidate.toJSON());
        }
      };

      const offerSnap = await offerRef.once('value');
      const offerData = offerSnap.val();

      if (!offerData || !offerData.sdp) {
        alert(`Room "${currentRoomId}" not found or Doctor has not created the room yet! Please check the key.`);
        location.reload();
        return;
      }

      await pc.setRemoteDescription(new RTCSessionDescription(offerData));
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);

      await answerRef.set({
        sdp: answer.sdp,
        type: answer.type,
        timestamp: Date.now()
      });

      doctorCandidatesRef.on('child_added', async (snapshot) => {
        const candidateData = snapshot.val();
        if (candidateData) {
          try {
            await pc.addIceCandidate(new RTCIceCandidate(candidateData));
          } catch (e) { console.error("ICE Candidate Error:", e); }
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
    } catch (err) {
      console.warn("Camera busy or blocked. Using synthetic stream fallback...", err);
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
      if (activeRoomRef) activeRoomRef.remove(); // INSTANT CLEANUP ON SESSION END
      if (pc) pc.close();
      if (localStream) localStream.getTracks().forEach(t => t.stop());
      window.location.href = window.location.pathname;
    });
  }
});
