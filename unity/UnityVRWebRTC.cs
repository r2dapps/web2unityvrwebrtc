using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;

/// <summary>
/// Unified Unity WebRTC peer for AetherCare.
///
/// Speaks the SAME room contract as the web app (web/app.js), so a Unity
/// instance is just another peer in the mesh — Unity-to-Unity, Unity-to-Web,
/// and Web-to-Web all work without any special-cased logic here:
///
///   rooms/{room}/peers/{peerId}                 presence
///   rooms/{room}/mailbox/{toPeerId}/{pushId}     { from, data, ts }  (Firebase)
///   { type: "join"/"signal"/"roster", ... }                          (Local WS server)
///
/// Two signaling backends, pick one in the Inspector:
///   - Firebase   : REST polling against a Firebase Realtime Database
///                  (works from anywhere, matches web app's cloud mode).
///   - LocalServer: WebSocket connection to signaling_server.py
///                  (LAN-only, zero internet, matches web app's local mode).
///
/// Mesh: for N peers in a room this opens N-1 RTCPeerConnections, exactly
/// like the web client's MeshManager. Initiator per pair is decided the
/// same deterministic way (lower peerId ordinal offers) so both sides never
/// race to create duplicate offers — no server-side coordination needed.
///
/// NOTE: LocalServer mode uses System.Net.WebSockets.ClientWebSocket, which
/// works on PC/Mac/Quest (Android) builds. It is NOT available on WebGL
/// builds — WebGL builds should use Firebase mode, or a JS-interop socket.
/// </summary>
public class UnityVRWebRTC : MonoBehaviour
{
    /// <summary>
    /// Which signaling backend to use.
    /// Firebase  = REST polling against a cloud Realtime Database. Works from anywhere. Matches the web app's cloud mode.
    /// LocalServer = WebSocket to signaling_server.py on the LAN. Zero internet required. NOT supported in WebGL builds.
    /// LanTcp    = Direct TCP socket (LanTcpSignaling.cs). LAN-only, lowest latency, no relay server at all.
    /// </summary>
    public enum SignalingMode { Firebase, LocalServer, LanTcp }

    /// <summary>
    /// How TURN relay credentials are provided.
    /// Cloudflare = Fetches short-lived credentials from your own Cloudflare Worker (see cloudflare-worker/README.md). Most secure.
    /// OpenRelay  = Uses the free openrelay.metered.ca TURN server. Good for testing; not for production.
    /// None       = STUN only. Calls may fail behind symmetric NATs (e.g. mobile data, corporate firewalls).
    /// </summary>
    public enum TurnMode { Cloudflare, OpenRelay, None }

    /// <summary>
    /// Room connection topology.
    /// Mesh      = Every peer connects to every other peer (full mesh). Best for small groups (2–4 peers).
    /// HubSpoke  = Only Spoke peers connect to the Hub; Spokes never connect to each other.
    ///             Best for classrooms / large groups where one host broadcasts to many viewers.
    /// </summary>
    public enum Topology { Mesh, HubSpoke }

    /// <summary>
    /// This peer's role in a HubSpoke topology. Ignored when topology == Mesh.
    /// Hub   = The broadcaster / host. Accepts connections from all Spokes.
    ///         Exactly ONE peer per room should be Hub.
    /// Spoke = A viewer / participant. Connects only to the Hub.
    /// </summary>
    public enum PeerRole { Hub, Spoke }

    // ---------------------------------------------------------------
    // Internal message envelope used by the generic event API.
    // ---------------------------------------------------------------
    [Serializable] private class SDPPayload { public string sdp; public string type; }
    [Serializable] private class IceCandidatePayload { public string candidate; public string sdpMid; public int sdpMLineIndex; }

    #region Inspector Config
    [Header("── Signaling Backend ──────────────────────────")]
    [Tooltip("Firebase: cloud relay, works anywhere.\nLocalServer: WebSocket to signaling_server.py on LAN (not WebGL).\nLanTcp: direct TCP socket, LAN-only.")]
    public SignalingMode signalingMode = SignalingMode.Firebase;

    [Tooltip("Your Firebase Realtime Database URL (used when signalingMode = Firebase).\nFormat: https://<project>-default-rtdb.<region>.firebasedatabase.app")]
    public string firebaseDatabaseUrl = "https://walkietalkie-c0f03-default-rtdb.asia-southeast1.firebasedatabase.app";

    [Tooltip("WebSocket URL of signaling_server.py (used when signalingMode = LocalServer).\nRun 'python signaling_server.py' and copy the printed address here.")]
    public string signalingServerUrl = "ws://192.168.1.42:8765";

    [Tooltip("All peers that share the same Room Key join the same call.\nUse a unique key per session (e.g. 'CLINIC-ROOM-1').")]
    public string roomKey = "ROOM-8921";

    [Header("── LanTcp Settings (only used if SignalingMode = LanTcp) ──")]
    [Tooltip("If true, this device listens on the port. If false, it connects to lanHostIp.")]
    public bool isLanHost = false;

    [Tooltip("The IP address to connect to (only used if isLanHost is false).")]
    public string lanHostIp = "127.0.0.1";

    [Tooltip("The port to listen/connect on.")]
    public int lanHostPort = 9091;

    [Header("── This Peer's Media ───────────────────────────")]
    [Tooltip("If true, captures this device's camera and streams it to all remote peers.\nLeave false if this peer is receive-only (e.g. a monitoring station).")]
    public bool sendCamera = true;

    [Tooltip("If true, captures the microphone and streams audio to all remote peers.\nLeave false if this peer should be mute-only.")]
    public bool sendMicrophone = true;

    [Tooltip("If true, incoming remote video tracks are rendered to remoteDisplayMaterial / remoteCameraRawImage.")]
    public bool receiveVideo = true;

    [Header("── Audio Controls ──────────────────────────────")]
    [Range(0f, 1f)]
    [Tooltip("Master volume for ALL incoming remote audio. Can also be set per-peer at runtime via SetRemoteVolume(peerId, vol).")]
    public float remoteAudioVolume = 1.0f;

    [Tooltip("Starts with the outgoing microphone muted. Toggle at runtime via SetMicrophoneMuted(bool).")]
    public bool isMicrophoneMuted = false;

    [Header("── Room Topology ───────────────────────────────")]
    [Tooltip("Mesh: every peer connects to every other peer (2–4 peers recommended).\nHubSpoke: Spokes only connect to the Hub — ideal for classrooms or large groups.")]
    public Topology topology = Topology.Mesh;

    [Tooltip("Only used when Topology = HubSpoke.\nHub: this is the broadcaster/host (exactly ONE Hub per room).\nSpoke: this is a viewer/participant — connects only to the Hub.")]
    public PeerRole myRole = PeerRole.Spoke;
    #endregion // Inspector Config

    #region Local Media Fields (Camera, Microphone & TURN)
    [Header("── Local Camera (sendCamera) ───────────────────")]
    [Tooltip("The Unity Camera to capture and stream. Leave empty to auto-assign Camera.main.")]
    public Camera vrStreamCamera;

    [Tooltip("Outgoing video width in pixels. Higher = more bandwidth. 1280×720 is a good default.")]
    public int streamWidth = 1280;

    [Tooltip("Outgoing video height in pixels.")]
    public int streamHeight = 720;
    [Header("── Remote Video Display ────────────────────────")]
    [Tooltip("Material whose mainTexture is set to the first remote peer's video. Assign a plane/quad's material here.")]
    public Material remoteDisplayMaterial;

    [Header("── UI Canvas Bindings (optional) ────────────────")]
    [Tooltip("RawImage that previews the local camera output. Optional — for on-screen local preview.")]
    public UnityEngine.UI.RawImage localCameraRawImage;

    [Tooltip("RawImage that shows the first incoming remote video. Optional — for on-screen remote preview.")]
    public UnityEngine.UI.RawImage remoteCameraRawImage;

    [Header("── TURN Relay ─────────────────────────────────")]
    [Tooltip("Cloudflare: fetches short-lived credentials from your Cloudflare Worker (most secure, requires setup).\nOpenRelay: uses the free openrelay.metered.ca server (testing only).\nNone: STUN only — calls may fail on mobile data or behind corporate firewalls.")]
    public TurnMode turnMode = TurnMode.OpenRelay;

    [Tooltip("Your Cloudflare Worker URL that returns { iceServers:[...] }. Only used when TurnMode = Cloudflare.\nSee cloudflare-worker/README.md for setup instructions.")]
    public string turnCredentialEndpoint = "";

    [Header("OpenRelay Credentials (TurnMode.OpenRelay or Cloudflare fallback)")]
    [Tooltip("TURN server URL. Default uses the free openrelay.metered.ca server.")]
    public string turnUrl = "turn:openrelay.metered.ca:443";

    [Tooltip("TURN username for the above server.")]
    public string turnUsername = "openrelayproject";

    [Tooltip("TURN credential/password for the above server.")]
    public string turnCredential = "openrelayproject";

    [Header("── System Behaviors ─────────────────────────────")]
    [Tooltip("If true, automatically joins the room when Start() runs. Set to false if you have a Lobby UI and want to wait for ConnectNow().")]
    public bool autoConnectOnStart = false;
    #endregion // Local Media Fields

    #region Private Runtime State
    private List<RTCIceServer> dynamicIceServers = null; // populated by FetchIceServers() when TurnMode.Cloudflare succeeds
    private string myPeerId;
    private VideoStreamTrack localVideoTrack;
    private AudioStreamTrack localAudioTrack;
    private readonly Dictionary<string, RTCPeerConnection> peerConnections = new Dictionary<string, RTCPeerConnection>();
    private readonly Dictionary<string, RTCDataChannel> dataChannels = new Dictionary<string, RTCDataChannel>();
    private readonly Dictionary<string, VideoStreamTrack> remoteVideoTracks = new Dictionary<string, VideoStreamTrack>();
    private readonly Dictionary<string, AudioSource> remoteAudioSources = new Dictionary<string, AudioSource>(); // per-peer AudioSources
    private readonly HashSet<string> knownPeers = new HashSet<string>();
    private ClientWebSocket ws;
    private bool running = true;
    private bool _cleanedUp = false;
    #endregion // Private Runtime State

    #region Public Events
    /// <summary>Fired when a remote video track delivers a new frame texture (useful for UI Toolkit/RawImage binding).</summary>
    public event Action<Texture> OnRemoteTextureReceived;

    /// <summary>
    /// Fired when a generic event message arrives over the data channel.
    /// Parameters: (fromPeerId, eventName, jsonPayload)
    /// </summary>
    public event Action<string, string, string> OnEventReceived;

    /// <summary>
    /// Fired when a text chat message arrives over the data channel.
    /// Parameters: (fromPeerId, chatText)
    /// </summary>
    public event Action<string, string> OnChatReceived;

    /// <summary>
    /// Fired if the peer connection drops unexpectedly, or if the Host closes the room.
    /// </summary>
    public event Action OnDisconnectedFromRoom;
    #endregion // Public Events

    #region Lifecycle & Diagnostics
    void Start()
    {
        _cleanedUp = false;
        running = true;

        myPeerId = roomKey.GetHashCode().ToString("x8") + "-" + SystemInfo.deviceUniqueIdentifier.GetHashCode().ToString("x4");

        if (sendCamera)
        {
            if (vrStreamCamera != null)
            {
                // Let WebRTC internally capture the camera without forcing a targetTexture, 
                // which preserves the camera's ability to render to the main display!
                localVideoTrack = vrStreamCamera.CaptureStreamTrack(streamWidth, streamHeight, RenderTextureDepth.Depth24);
                Debug.Log($"[AetherCare] 🎥 Local Video Track active, capturing camera: {vrStreamCamera.name}");
            }
        }
        StartCoroutine(WebRTC.Update());

        StartCoroutine(LogMediaStatsLoop()); // runs silently in background

        if (sendMicrophone)
        {
            if (autoConnectOnStart) StartCoroutine(SetupMicrophoneThenConnect());
            else StartCoroutine(SetupMicrophoneTrack()); // just setup for PIP/lobby
        }
        else
        {
            if (autoConnectOnStart) ConnectNow();
        }
    }

    private IEnumerator SetupMicrophoneThenConnect()
    {
        yield return SetupMicrophoneTrack(); // must finish before any peer connection is created
        ConnectNow();
    }

    private IEnumerator LogMediaStatsLoop()
    {
        while (running)
        {
            yield return new WaitForSeconds(3f);
            foreach (var kv in peerConnections)
            {
                string peerId = kv.Key;
                var pc = kv.Value;
                if (pc == null) continue;

                var statsOp = pc.GetStats();
                yield return statsOp;
                if (statsOp.IsError) continue;

                var report = statsOp.Value;
                foreach (var stat in report.Stats.Values)
                {
                    if (stat is RTCOutboundRTPStreamStats outbound)
                        Debug.Log($"[Stats→{peerId}] SENDING {outbound.kind}: bytesSent={outbound.bytesSent} packetsSent={outbound.packetsSent}");
                    if (stat is RTCInboundRTPStreamStats inbound)
                        Debug.Log($"[Stats→{peerId}] RECEIVING {inbound.kind}: bytesReceived={inbound.bytesReceived} packetsReceived={inbound.packetsReceived} packetsLost={inbound.packetsLost}");
                }
                report.Dispose();
            }
        }
    }
    #endregion // Lifecycle & Diagnostics

    #region Local Media & Microphone Setup
    private IEnumerator SetupMicrophoneTrack()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[AetherCare] No microphone device found — sendMicrophone will produce no audio.");
            yield break;
        }
        string micName = Microphone.devices[0];
        var clip = Microphone.Start(micName, true, 1, 48000);
        yield return new WaitUntil(() => Microphone.GetPosition(micName) > 0);

        var micSource = gameObject.GetComponent<AudioSource>();
        if (micSource == null) micSource = gameObject.AddComponent<AudioSource>();
        micSource.clip = clip;
        micSource.loop = true;
        micSource.mute = isMicrophoneMuted;
        micSource.volume = 0.001f;
        micSource.Play();

        try
        {
            localAudioTrack = new AudioStreamTrack(micSource);
            Debug.Log($"[AetherCare] Microphone track created from device '{micName}'.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AetherCare] AudioStreamTrack(micSource) failed: {e.Message}");
        }
    }

    [ContextMenu("Connect Now")]
    public void ConnectNow()
    {
        if (!running)
        {
            _cleanedUp = false;
            running = true;
            StartCoroutine(LogMediaStatsLoop());
        }

        Debug.Log($"[AetherCare] Manually connecting to Room: '{roomKey}' via {signalingMode}...");
        StartCoroutine(Bootstrap());
    }

    /// <summary>
    /// Switches the outgoing video to a different Unity Camera at runtime.
    /// Creates a new VideoStreamTrack from newCamera, replaces the sender track on every
    /// active RTCPeerConnection, and disposes the old track. Safe to call while connected.
    /// </summary>
    public void SwitchCamera(Camera newCamera)
    {
        if (newCamera == null)
        {
            Debug.LogWarning("[AetherCare] SwitchCamera called with null camera — ignoring.");
            return;
        }

        var oldTrack = localVideoTrack;
        var newTrack = newCamera.CaptureStreamTrack(streamWidth, streamHeight, RenderTextureDepth.Depth24);
        localVideoTrack = newTrack;
        vrStreamCamera = newCamera;

        // Replace the sender's track on every active peer connection.
        foreach (var kv in peerConnections)
        {
            var pc = kv.Value;
            foreach (var sender in pc.GetSenders())
            {
                if (sender.Track is VideoStreamTrack)
                {
                    sender.ReplaceTrack(newTrack);
                    Debug.Log($"[AetherCare] Replaced video track on peer '{kv.Key}' with camera '{newCamera.name}'.");
                    break;
                }
            }
        }

        // Dispose the old track after all senders have been updated.
        oldTrack?.Dispose();
        Debug.Log($"[AetherCare] SwitchCamera complete — now streaming '{newCamera.name}'.");
    }

    #region Public Audio Controls
    /// <summary>Mutes/unmutes the outgoing microphone track.</summary>
    public void SetMicrophoneMuted(bool mute)
    {
        isMicrophoneMuted = mute;

        // Setting track.Enabled = false can cause native crashes in some Unity WebRTC versions.
        // Instead, we directly mute the Unity AudioSource. Unity will still pump the WebRTC 
        // capture callback, but with an array of zeros (silence), which works perfectly.
        var micSource = gameObject.GetComponent<AudioSource>();
        if (micSource != null)
        {
            micSource.mute = mute;
        }

        Debug.Log($"[AetherCare] Microphone muted: {mute}");
        BroadcastMuteState(mute);
    }

    /// <summary>Sets the volume for ALL remote peers at once.</summary>
    public void SetRemoteVolume(float vol)
    {
        remoteAudioVolume = Mathf.Clamp01(vol);
        foreach (var src in remoteAudioSources.Values)
        {
            if (src != null) src.volume = remoteAudioVolume;
        }
        Debug.Log($"[AetherCare] Global remote audio volume set to: {remoteAudioVolume:P0}");
    }

    /// <summary>Sets the volume for a specific remote peer by peerId.</summary>
    public void SetRemoteVolume(string peerId, float vol)
    {
        float clamped = Mathf.Clamp01(vol);
        if (remoteAudioSources.TryGetValue(peerId, out var src) && src != null)
        {
            src.volume = clamped;
            Debug.Log($"[AetherCare] Volume for peer '{peerId}' set to: {clamped:P0}");
        }
    }

    /// <summary>Broadcasts a mute command over the data channel so the remote peer silences this sender.</summary>
    public void BroadcastMuteState(bool muted)
    {
        string payload = "{\"kind\":\"event\",\"event\":\"mute\",\"peerId\":\"" + myPeerId + "\",\"muted\":" + (muted ? "true" : "false") + "}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        foreach (var dc in dataChannels.Values)
        {
            if (dc.ReadyState == RTCDataChannelState.Open) dc.Send(bytes);
        }
        Debug.Log($"[AetherCare] Broadcast mute={muted} to all peers.");
    }
    #endregion // Public Audio Controls
    #endregion // Local Media & Microphone Setup

    #region Connection Bootstrap
    private IEnumerator Bootstrap()
    {
        // No-op unless turnMode == Cloudflare; otherwise BuildConfig() falls
        // back to OpenRelay/None immediately. We wait for this before
        // starting signaling so the very first peer connection already has
        // a fresh, non-expired TURN credential instead of racing it.
        yield return FetchIceServers();

        if (signalingMode == SignalingMode.Firebase)
            StartCoroutine(FirebaseSignalingLoop());
        else if (signalingMode == SignalingMode.LocalServer)
            _ = LocalServerSignalingLoop();
        else if (signalingMode == SignalingMode.LanTcp)
            LanTcpSignalingLoop();
    }

    private string RoleString() => topology == Topology.HubSpoke ? (myRole == PeerRole.Hub ? "hub" : "spoke") : "peer";

    private bool ShouldConnectTo(string peerId, Dictionary<string, string> roles)
    {
        if (topology != Topology.HubSpoke) return true;
        if (myRole == PeerRole.Hub) return true; // hub connects to everyone
        string theirRole = (roles != null && roles.TryGetValue(peerId, out var r)) ? r : "peer";
        return theirRole == "hub"; // spokes only connect to the hub
    }

    // Mints/fetches short-lived Cloudflare TURN credentials from your own
    // Worker (see cloudflare-worker/README.md). Silently leaves
    // dynamicIceServers null on any failure — BuildConfig() then falls back
    // to the OpenRelay fields below, so a misconfigured endpoint never
    // blocks the app from at least attempting calls.
    private IEnumerator FetchIceServers()
    {
        if (turnMode != TurnMode.Cloudflare) yield break;
        if (string.IsNullOrEmpty(turnCredentialEndpoint))
        {
            Debug.LogWarning("[AetherCare] turnMode is Cloudflare but turnCredentialEndpoint is empty — falling back to OpenRelay. See cloudflare-worker/README.md.");
            yield break;
        }

        using (var www = UnityWebRequest.Get(turnCredentialEndpoint))
        {
            yield return www.SendWebRequest();
            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[AetherCare] Could not fetch Cloudflare TURN credentials ({www.error}) — falling back to OpenRelay for this session.");
                yield break;
            }

            string json = www.downloadHandler.text;
            var objs = ExtractObjectArray(json, "iceServers");
            var servers = new List<RTCIceServer>();
            foreach (var obj in objs)
            {
                var urls = ExtractStringArrayField(obj, "urls");
                if (urls.Count == 0) continue;
                string username = ExtractStringField(obj, "username");
                string credential = ExtractStringField(obj, "credential");
                servers.Add(new RTCIceServer { urls = urls.ToArray(), username = username, credential = credential });
            }

            if (servers.Count > 0) dynamicIceServers = servers;
            else Debug.LogWarning("[AetherCare] Cloudflare Worker response had no usable iceServers — falling back to OpenRelay.");
        }
    }
    #endregion // Connection Bootstrap

    #region Peer Connection Management
    // -----------------------------------------------------------------
    // Peer connection lifecycle (shared by both signaling backends)
    // -----------------------------------------------------------------

    private RTCConfiguration BuildConfig()
    {
        var servers = new List<RTCIceServer>
        {
            new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
            new RTCIceServer { urls = new[] { "stun:stun.cloudflare.com:3478" } },
        };

        if (turnMode == TurnMode.Cloudflare && dynamicIceServers != null && dynamicIceServers.Count > 0)
        {
            servers.AddRange(dynamicIceServers);
        }
        else if (turnMode != TurnMode.None)
        {
            // OpenRelay: used directly for TurnMode.OpenRelay, and as the
            // automatic fallback if TurnMode.Cloudflare's fetch failed.
            servers.Add(new RTCIceServer { urls = new[] { turnUrl }, username = turnUsername, credential = turnCredential });
        }

        return new RTCConfiguration { iceServers = servers.ToArray() };
    }

    private RTCPeerConnection GetOrCreatePeer(string peerId, bool iAmInitiator, Action<string, string> sendSignal)
    {
        if (peerConnections.TryGetValue(peerId, out var existing)) return existing;

        Debug.Log($"[AetherCare] Creating RTCPeerConnection for '{peerId}' (Initiator: {iAmInitiator})");

        var config = BuildConfig();
        var pc = new RTCPeerConnection(ref config);
        peerConnections[peerId] = pc;

        pc.OnConnectionStateChange = state =>
        {
            Debug.Log($"[AetherCare] Peer connection state with '{peerId}': {state}");
            
            // If we are a Spoke, and our connection to the Hub drops, we should disconnect entirely
            if ((state == RTCPeerConnectionState.Closed || state == RTCPeerConnectionState.Disconnected || state == RTCPeerConnectionState.Failed) 
                && myRole == PeerRole.Spoke)
            {
                Debug.LogWarning("[AetherCare] Connection to Hub lost. Firing OnDisconnectedFromRoom.");
                OnDisconnectedFromRoom?.Invoke();
            }
        };

        if (localVideoTrack != null)
        {
            pc.AddTrack(localVideoTrack);
            Debug.Log($"[AetherCare] Added local video track to PC '{peerId}'");
        }
        if (localAudioTrack != null)
        {
            pc.AddTrack(localAudioTrack);
            Debug.Log($"[AetherCare] Added local audio track to PC '{peerId}'");
        }

        pc.OnIceCandidate = candidate =>
        {
            if (!string.IsNullOrEmpty(candidate.Candidate))
            {
                var payload = new IceCandidatePayload
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                };
                sendSignal(peerId, "{\"kind\":\"ice\",\"candidate\":" + JsonUtility.ToJson(payload) + "}");
            }
        };

        pc.OnTrack = evt =>
        {
            Debug.Log($"[AetherCare] 🎥 OnTrack event fired from '{peerId}': Kind={evt.Track?.Kind}");
            if (evt.Track is VideoStreamTrack videoTrack)
            {
                remoteVideoTracks[peerId] = videoTrack; // Keep track reference alive so GC doesn't drop callbacks
                videoTrack.Enabled = true;
                videoTrack.OnVideoReceived += tex =>
                {
                    if (tex != null)
                    {
                        Debug.Log($"[AetherCare] 📺 Video frame texture received from '{peerId}': {tex.width}x{tex.height}");
                        if (remoteDisplayMaterial != null) remoteDisplayMaterial.mainTexture = tex;
                        if (remoteCameraRawImage != null) remoteCameraRawImage.texture = tex;
                        OnRemoteTextureReceived?.Invoke(tex);
                    }
                };
            }
            if (evt.Track is AudioStreamTrack audioTrack)
            {
                Debug.Log($"[AetherCare] 🔊 Audio track received from '{peerId}'!");
                // Each peer needs its OWN child GameObject for its AudioSource.
                // Adding multiple AudioSources to the same GameObject causes the
                // AudioCustomFilter (OnAudioFilterRead) conflict Unity warns about —
                // a filter component can only be owned by one AudioSource at a time.
                if (!remoteAudioSources.TryGetValue(peerId, out var audioSource) || audioSource == null)
                {
                    var child = new GameObject($"RemoteAudio_{peerId}");
                    child.transform.SetParent(transform, false);
                    audioSource = child.AddComponent<AudioSource>();
                    remoteAudioSources[peerId] = audioSource;
                }
                audioSource.SetTrack(audioTrack);
                audioSource.loop = true;
                audioSource.spatialBlend = 0f; // 2D audio — no positional attenuation
                audioSource.volume = remoteAudioVolume;
                audioSource.Play();
            }
        };

        pc.OnDataChannel = channel =>
        {
            dataChannels[peerId] = channel;
            channel.OnMessage = bytes => HandleDataChannelMessage(peerId, bytes);
        };

        if (iAmInitiator)
        {
            var dc = pc.CreateDataChannel("chat");
            dataChannels[peerId] = dc;
            dc.OnMessage = bytes => HandleDataChannelMessage(peerId, bytes);

            StartCoroutine(CreateAndSendOffer(pc, peerId, sendSignal));
        }

        return pc;
    }

    private IEnumerator CreateAndSendOffer(RTCPeerConnection pc, string peerId, Action<string, string> sendSignal)
    {
        Debug.Log($"[AetherCare] Creating SDP Offer for '{peerId}'...");
        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError($"[AetherCare] CreateOffer error for '{peerId}': {offerOp.Error.errorType}");
            yield break;
        }
        var desc = offerOp.Desc;
        var setLocalOp = pc.SetLocalDescription(ref desc);
        yield return setLocalOp;
        if (setLocalOp.IsError)
        {
            Debug.LogError($"[AetherCare] SetLocalDescription error for '{peerId}': {setLocalOp.Error.errorType}");
            yield break;
        }
        var payload = new SDPPayload { sdp = desc.sdp, type = "offer" };
        sendSignal(peerId, "{\"kind\":\"offer\",\"sdp\":" + EscapeJsonString(desc.sdp) + "}");
        Debug.Log($"[AetherCare] Sent SDP Offer to '{peerId}'");
    }

    private IEnumerator HandleIncomingSignal(string fromPeerId, string kind, string sdp, IceCandidatePayload ice, Action<string, string> sendSignal)
    {
        bool iAmInitiator = string.CompareOrdinal(myPeerId, fromPeerId) < 0;
        var pc = GetOrCreatePeer(fromPeerId, false, sendSignal);

        if (kind == "offer")
        {
            Debug.Log($"[AetherCare] Handling SDP Offer from '{fromPeerId}'...");
            var desc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
            var setRemoteOp = pc.SetRemoteDescription(ref desc);
            yield return setRemoteOp;
            if (setRemoteOp.IsError)
            {
                Debug.LogError($"[AetherCare] SetRemoteDescription error (Offer) from '{fromPeerId}': {setRemoteOp.Error.errorType}");
                yield break;
            }

            var answerOp = pc.CreateAnswer();
            yield return answerOp;
            if (answerOp.IsError)
            {
                Debug.LogError($"[AetherCare] CreateAnswer error for '{fromPeerId}': {answerOp.Error.errorType}");
                yield break;
            }
            var answerDesc = answerOp.Desc;
            var setLocalOp = pc.SetLocalDescription(ref answerDesc);
            yield return setLocalOp;

            sendSignal(fromPeerId, "{\"kind\":\"answer\",\"sdp\":" + EscapeJsonString(answerDesc.sdp) + "}");
            Debug.Log($"[AetherCare] Sent SDP Answer to '{fromPeerId}'");
        }
        else if (kind == "answer")
        {
            Debug.Log($"[AetherCare] Handling SDP Answer from '{fromPeerId}'...");
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var setRemoteOp = pc.SetRemoteDescription(ref desc);
            yield return setRemoteOp;
            if (setRemoteOp.IsError)
            {
                Debug.LogError($"[AetherCare] SetRemoteDescription error (Answer) from '{fromPeerId}': {setRemoteOp.Error.errorType}");
            }
            else
            {
                Debug.Log($"[AetherCare] SetRemoteDescription (Answer) succeeded for '{fromPeerId}'!");
            }
        }
        else if (kind == "ice" && ice != null)
        {
            var init = new RTCIceCandidateInit { candidate = ice.candidate, sdpMid = ice.sdpMid, sdpMLineIndex = ice.sdpMLineIndex };
            pc.AddIceCandidate(new RTCIceCandidate(init));
        }
    }

    private void ReconcileRoster(List<string> roster, Dictionary<string, string> roles, Action<string, string> sendSignal)
    {
        var current = new HashSet<string>(roster);
        foreach (var peerId in roster)
        {
            if (!ShouldConnectTo(peerId, roles)) continue;
            if (!knownPeers.Contains(peerId))
            {
                knownPeers.Add(peerId);
                bool iAmInitiator = string.CompareOrdinal(myPeerId, peerId) < 0;
                GetOrCreatePeer(peerId, iAmInitiator, sendSignal);
            }
        }
        foreach (var peerId in new List<string>(knownPeers))
        {
            if (!current.Contains(peerId) || !ShouldConnectTo(peerId, roles))
            {
                knownPeers.Remove(peerId);
                if (peerConnections.TryGetValue(peerId, out var pc)) { pc.Close(); peerConnections.Remove(peerId); }
                dataChannels.Remove(peerId);
            }
        }
    }
    #endregion // Peer Connection Management

    #region Messaging & Data Channel
    public void SendEventToAll(string eventName, string payloadJson = "{}")
    {
        string payload = "{\"kind\":\"event\",\"event\":\"" + eventName + "\",\"payload\":" + payloadJson + "}";
        foreach (var dc in dataChannels.Values)
        {
            if (dc.ReadyState == RTCDataChannelState.Open) dc.Send(payload);
        }
    }

    public void SendEvent(string toPeerId, string eventName, string payloadJson = "{}")
    {
        string payload = "{\"kind\":\"event\",\"event\":\"" + eventName + "\",\"payload\":" + payloadJson + "}";
        if (toPeerId != null)
        {
            if (dataChannels.TryGetValue(toPeerId, out var dc) && dc.ReadyState == RTCDataChannelState.Open)
                dc.Send(payload);
        }
        else
        {
            foreach (var dc in dataChannels.Values)
                if (dc.ReadyState == RTCDataChannelState.Open) dc.Send(payload);
        }
    }

    public void SendChatToAll(string text)
    {
        // Must wrap in JSON to match app.js expectation: msg.text
        // Note: We escape quotes and backslashes in the text for valid JSON.
        string safeText = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string payload = "{\"kind\":\"chat\",\"text\":\"" + safeText + "\"}";
        foreach (var dc in dataChannels.Values)
        {
            if (dc.ReadyState == RTCDataChannelState.Open) dc.Send(payload);
        }
    }

    private void HandleDataChannelMessage(string fromPeerId, byte[] bytes)
    {
        string json = Encoding.UTF8.GetString(bytes);
        string kind = ExtractStringField(json, "kind");

        if (kind == "event")
        {
            string eventName = ExtractStringField(json, "event");
            string payload = ExtractRawField(json, "payload") ?? "{}";
            Debug.Log($"[AetherCare] Event '{eventName}' from '{fromPeerId}': {payload}");

            // Handle built-in mute broadcast: if the sender says they are muted, silence their AudioSource.
            if (eventName == "mute")
            {
                string senderId = ExtractStringField(json, "peerId");
                string mutedStr = ExtractStringField(json, "muted");
                bool muted = mutedStr == "true";
                if (!string.IsNullOrEmpty(senderId) && remoteAudioSources.TryGetValue(senderId, out var src) && src != null)
                {
                    src.volume = muted ? 0f : remoteAudioVolume;
                    Debug.Log($"[AetherCare] Peer '{senderId}' muted={muted} — adjusted AudioSource volume.");
                }
            }

            OnEventReceived?.Invoke(fromPeerId, eventName, payload);
        }
        else if (kind == "chat")
        {
            string chatText = ExtractStringField(json, "text") ?? "";
            Debug.Log($"[AetherCare] Chat from {fromPeerId}: {chatText}");
            OnChatReceived?.Invoke(fromPeerId, chatText);
        }
        else
        {
            // Web app app.js sends chat just as {"text":"...","ts":...} without a "kind"
            string chatText = ExtractStringField(json, "text");
            if (!string.IsNullOrEmpty(chatText))
            {
                Debug.Log($"[AetherCare] Chat from {fromPeerId}: {chatText}");
                OnChatReceived?.Invoke(fromPeerId, chatText);
            }
            else
            {
                // Fallback for raw data
                Debug.Log($"[AetherCare] Raw data channel message from {fromPeerId}: {json}");
                OnChatReceived?.Invoke(fromPeerId, json);
            }
        }
    }
    #endregion // Messaging & Data Channel

    #region Signaling — Firebase
    // Backend A: Firebase REST polling (matches web app's cloud schema)
    private IEnumerator FirebaseSignalingLoop()
    {
        string room = SanitizeRoom(roomKey);

        // Announce presence (role included so hub-spoke topology works over Firebase too).
        yield return PutFirebase($"rooms/{room}/peers/{myPeerId}.json",
            "{\"joinedAt\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"role\":\"" + RoleString() + "\"}");

        Action<string, string> sendSignal = (toPeerId, jsonData) =>
        {
            StartCoroutine(PostFirebase($"rooms/{room}/mailbox/{toPeerId}.json",
                "{\"from\":\"" + myPeerId + "\",\"data\":" + jsonData + "}"));
        };

        while (running)
        {
            // Poll roster.
            yield return GetFirebase($"rooms/{room}/peers.json", json =>
            {
                var peerObjs = ExtractKeyedObjects(json); // id -> raw peer object (contains "role")
                var ids = new List<string>(peerObjs.Keys);
                ids.Remove(myPeerId);
                var roles = new Dictionary<string, string>();
                foreach (var id in ids)
                {
                    string role = ExtractStringField(peerObjs[id], "role");
                    roles[id] = string.IsNullOrEmpty(role) ? "peer" : role;
                }
                ReconcileRoster(ids, roles, sendSignal);
            });

            // Poll my mailbox.
            yield return GetFirebase($"rooms/{room}/mailbox/{myPeerId}.json", json =>
            {
                foreach (var kv in ExtractKeyedObjects(json))
                {
                    string pushKey = kv.Key;
                    string obj = kv.Value;
                    string from = ExtractStringField(obj, "from");
                    string dataObj = ExtractRawField(obj, "data");
                    if (from != null && dataObj != null)
                    {
                        string kind = ExtractStringField(dataObj, "kind");
                        string sdp = ExtractStringField(dataObj, "sdp");
                        IceCandidatePayload ice = null;
                        if (kind == "ice")
                        {
                            string iceObj = ExtractRawField(dataObj, "candidate");
                            if (iceObj != null) ice = JsonUtility.FromJson<IceCandidatePayload>(iceObj);
                        }
                        StartCoroutine(HandleIncomingSignal(from, kind, sdp, ice, sendSignal));
                    }
                    StartCoroutine(DeleteFirebase($"rooms/{room}/mailbox/{myPeerId}/{pushKey}.json"));
                }
            });

            yield return new WaitForSeconds(1.0f);
        }
    }
    #endregion // Signaling — Firebase

    private IEnumerator GetFirebase(string path, Action<string> onSuccess)
    {
        using (var www = UnityWebRequest.Get($"{firebaseDatabaseUrl}/{path}"))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.Success && www.downloadHandler.text != "null")
                onSuccess(www.downloadHandler.text);
        }
    }

    private IEnumerator PutFirebase(string path, string json)
    {
        using (var www = new UnityWebRequest($"{firebaseDatabaseUrl}/{path}", "PUT"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();
        }
    }

    private IEnumerator PostFirebase(string path, string json)
    {
        using (var www = new UnityWebRequest($"{firebaseDatabaseUrl}/{path}", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();
        }
    }

    private IEnumerator DeleteFirebase(string path)
    {
        using (var www = UnityWebRequest.Delete($"{firebaseDatabaseUrl}/{path}"))
        {
            yield return www.SendWebRequest();
        }
    }

    #region Signaling — Local WebSocket Server
    // Backend B: Local WebSocket signaling server (LAN, zero internet)
    private async Task LocalServerSignalingLoop()
    {
        string room = SanitizeRoom(roomKey);
        ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(signalingServerUrl), CancellationToken.None);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AetherCare] Could not reach local signaling server at {signalingServerUrl}: {e.Message}");
            return;
        }

        Action<string, string> sendSignal = (toPeerId, jsonData) =>
        {
            _ = SendWs("{\"type\":\"signal\",\"to\":\"" + toPeerId + "\",\"data\":" + jsonData + "}");
        };

        await SendWs("{\"type\":\"join\",\"room\":\"" + room + "\",\"peerId\":\"" + myPeerId + "\",\"meta\":{\"role\":\"" + RoleString() + "\"}}");

        var buffer = new byte[1 << 16];
        while (running && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            string type = ExtractStringField(json, "type");
            Debug.Log($"[AetherCare] WS message received: type='{type}' raw='{(json.Length > 200 ? json.Substring(0, 200) + "..." : json)}'");

            if (type == "roster")
            {
                var ids = ExtractStringArrayField(json, "peers");
                string rolesRaw = ExtractRawField(json, "roles");
                var roles = rolesRaw != null ? ExtractStringMap(rolesRaw) : new Dictionary<string, string>();
                Debug.Log($"[AetherCare] Roster received: [{string.Join(", ", ids)}] (myPeerId={myPeerId})");
                ReconcileRoster(ids, roles, sendSignal);
            }
            else if (type == "signal")
            {
                string from = ExtractStringField(json, "from");
                string dataObj = ExtractRawField(json, "data");
                if (from != null && dataObj != null)
                {
                    string kind = ExtractStringField(dataObj, "kind");
                    string sdp = ExtractStringField(dataObj, "sdp");
                    IceCandidatePayload ice = null;
                    if (kind == "ice")
                    {
                        string iceObj = ExtractRawField(dataObj, "candidate");
                        if (iceObj != null) ice = JsonUtility.FromJson<IceCandidatePayload>(iceObj);
                    }
                    StartCoroutine(HandleIncomingSignal(from, kind, sdp, ice, sendSignal));
                }
            }
            else if (type == "ping")
            {
                await SendWs("{\"type\":\"pong\"}");
            }
        }
    }

    private async Task SendWs(string json)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
    #endregion // Signaling — Local WebSocket Server

    #region Signaling — LanTcp (Direct TCP Socket)
    // Backend C: Direct TCP socket (LAN-only, no intermediate server)
    private void LanTcpSignalingLoop()
    {
        var tcp = gameObject.GetComponent<LanTcpSignaling>();
        if (tcp == null) tcp = gameObject.AddComponent<LanTcpSignaling>();

        Action<string, string> sendSignal = (toPeerId, jsonData) =>
        {
            tcp.SendSignalingMessage(jsonData);
        };

        tcp.OnConnected += () =>
        {
            Debug.Log($"[AetherCare] LanTcp connected. I am initiator: {tcp.IAmInitiator}");
            // Use a hardcoded peerId for the other side since LanTcp is strictly 1-to-1
            string peerId = "lan-peer";

            // Mark as known
            if (!knownPeers.Contains(peerId)) knownPeers.Add(peerId);

            GetOrCreatePeer(peerId, tcp.IAmInitiator, sendSignal);
        };

        tcp.OnMessageReceived += (json) =>
        {
            string kind = ExtractStringField(json, "kind");
            string sdp = ExtractStringField(json, "sdp");
            IceCandidatePayload ice = null;
            if (kind == "ice")
            {
                string iceObj = ExtractRawField(json, "candidate");
                if (iceObj != null) ice = JsonUtility.FromJson<IceCandidatePayload>(iceObj);
            }
            StartCoroutine(HandleIncomingSignal("lan-peer", kind, sdp, ice, sendSignal));
        };

        tcp.OnDisconnected += () =>
        {
            Debug.LogWarning("[AetherCare] LanTcp connection lost.");
            if (peerConnections.TryGetValue("lan-peer", out var pc)) { pc.Close(); peerConnections.Remove("lan-peer"); }
            dataChannels.Remove("lan-peer");
            knownPeers.Remove("lan-peer");
        };

        if (isLanHost)
            tcp.StartAsHost(lanHostPort);
        else
            tcp.ConnectAsClient(lanHostIp, lanHostPort);
    }
    #endregion // Signaling — LanTcp

    #region JSON Parsing Helpers
    // Minimal hand-rolled JSON helpers (avoids pulling in a JSON library;
    // Firebase's REST shape and our own signaling messages are simple
    // enough that brace/quote walking is reliable and dependency-free).

    private static string SanitizeRoom(string room) => room.Replace(" ", "").ToUpperInvariant();

    /// <summary>Minimal JSON string escaper for embedding raw SDP text (which contains \r\n and
    /// occasionally quotes) inside our hand-built JSON payloads.</summary>
    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static List<string> ExtractTopLevelKeys(string json)
    {
        var keys = new List<string>();
        int depth = 0; bool inString = false; bool expectKey = false;
        var sb = new StringBuilder();
        for (int i = 1; i < json.Length - 1; i++) // skip outer braces
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\'))
            {
                inString = !inString;
                if (inString && depth == 0) { sb.Clear(); expectKey = true; continue; }
                if (!inString && expectKey) { keys.Add(sb.ToString()); expectKey = false; continue; }
            }
            if (inString && expectKey) { sb.Append(c); continue; }
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
        }
        return keys;
    }

    private static Dictionary<string, string> ExtractKeyedObjects(string json)
    {
        // Firebase shape: { "-pushKey1": {...}, "-pushKey2": {...} }
        var result = new Dictionary<string, string>();
        var keys = ExtractTopLevelKeys(json);
        foreach (var key in keys)
        {
            int keyIdx = json.IndexOf("\"" + key + "\"");
            int braceStart = json.IndexOf('{', keyIdx);
            if (braceStart < 0) continue;
            int depth = 0; int i = braceStart;
            for (; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) break; }
            }
            result[key] = json.Substring(braceStart, i - braceStart + 1);
        }
        return result;
    }

    private static string ExtractStringField(string json, string field)
    {
        // Find the field key
        string keyMarker = "\"" + field + "\"";
        int idx = json.IndexOf(keyMarker);
        if (idx < 0) return null;

        // Find the colon and the opening quote of the value
        int colonIdx = json.IndexOf(':', idx + keyMarker.Length);
        if (colonIdx < 0) return null;

        int quoteIdx = json.IndexOf('"', colonIdx + 1);
        if (quoteIdx < 0) return null;

        int start = quoteIdx + 1;
        var sb = new StringBuilder();
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                char next = json[i + 1];
                if (next == 'n') sb.Append('\n');
                else if (next == 'r') sb.Append('\r');
                else if (next == 't') sb.Append('\t');
                else sb.Append(next);
                i++;
                continue;
            }
            if (json[i] == '"') break;
            sb.Append(json[i]);
        }
        return sb.ToString();
    }

    private static string ExtractRawField(string json, string field)
    {
        string marker = "\"" + field + "\":";
        int idx = json.IndexOf(marker);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        if (start >= json.Length) return null;
        if (json[start] == '{')
        {
            int depth = 0, i = start;
            for (; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) break; }
            }
            return json.Substring(start, i - start + 1);
        }
        else if (json[start] == '"')
        {
            int end = start + 1;
            while (end < json.Length && !(json[end] == '"' && json[end - 1] != '\\')) end++;
            return json.Substring(start, end - start + 1);
        }
        return null;
    }

    /// <summary>Extracts each raw object substring from a JSON array field, e.g.
    /// "iceServers":[{...},{...}] -> ["{...}", "{...}"]. Used to walk the
    /// Cloudflare Worker's iceServers response without a full JSON library.</summary>
    private static List<string> ExtractObjectArray(string json, string field)
    {
        var list = new List<string>();
        string marker = "\"" + field + "\":[";
        int idx = json.IndexOf(marker);
        if (idx < 0) return list;
        int i = idx + marker.Length;
        while (i < json.Length)
        {
            while (i < json.Length && (json[i] == ',' || char.IsWhiteSpace(json[i]))) i++;
            if (i >= json.Length || json[i] == ']') break;
            if (json[i] != '{') break;
            int depth = 0, start = i;
            for (; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { i++; break; } }
            }
            list.Add(json.Substring(start, i - start));
        }
        return list;
    }

    /// <summary>Parses a flat JSON object of string:string pairs, e.g. the
    /// local server's "roles":{"abc123":"hub","def456":"spoke"} field
    /// (pass the raw {...} substring, e.g. from ExtractRawField). Deliberately
    /// NOT built on ExtractTopLevelKeys — that helper assumes object values
    /// (nested braces raise depth), so it misreads plain string values here
    /// as extra "keys". This scans key/value string pairs directly instead.</summary>
    private static Dictionary<string, string> ExtractStringMap(string json)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(json)) return result;
        int i = 0;
        while (i < json.Length)
        {
            int keyStart = json.IndexOf('"', i);
            if (keyStart < 0) break;
            int keyEnd = FindStringEnd(json, keyStart + 1);
            if (keyEnd < 0) break;
            string key = json.Substring(keyStart + 1, keyEnd - keyStart - 1);

            int colon = json.IndexOf(':', keyEnd + 1);
            if (colon < 0) break;

            int valStart = json.IndexOf('"', colon + 1);
            if (valStart < 0) break;
            int valEnd = FindStringEnd(json, valStart + 1);
            if (valEnd < 0) break;
            string val = json.Substring(valStart + 1, valEnd - valStart - 1);

            result[key] = val;
            i = valEnd + 1;
        }
        return result;
    }

    private static int FindStringEnd(string json, int start)
    {
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '\\') { i++; continue; }
            if (json[i] == '"') return i;
        }
        return -1;
    }

    private static List<string> ExtractStringArrayField(string json, string field)
    {
        var list = new List<string>();
        string marker = "\"" + field + "\":[";
        int idx = json.IndexOf(marker);
        if (idx < 0) return list;
        int start = idx + marker.Length;
        int end = json.IndexOf(']', start);
        if (end < 0) return list;
        string inner = json.Substring(start, end - start);
        foreach (var part in inner.Split(','))
        {
            var trimmed = part.Trim().Trim('"');
            if (!string.IsNullOrEmpty(trimmed)) list.Add(trimmed);
        }
        return list;
    }
    #endregion // JSON Parsing Helpers

    #region Lifecycle (Start is above; Leave / Destroy here)

    /// <summary>
    /// Gracefully disconnects from the room without destroying the GameObject,
    /// so you can call ConnectNow() again afterwards.
    /// Also called automatically on application quit and GameObject destroy.
    /// </summary>
    public void LeaveRoom()
    {
        Debug.Log($"[AetherCare] Leaving room {roomKey}...");
        running = false;
        StopAllCoroutines(); // stops connection loops

        // We must restart WebRTC.Update() immediately so local video PIP doesn't freeze in the UI
        StartCoroutine(WebRTC.Update());

        foreach (var pc in peerConnections.Values)
        {
            try { pc.Close(); } catch (Exception) { }
        }
        peerConnections.Clear();
        dataChannels.Clear();
        knownPeers.Clear();

        foreach (var track in remoteVideoTracks.Values) track?.Dispose();
        remoteVideoTracks.Clear();

        foreach (var src in remoteAudioSources.Values)
        {
            if (src != null) Destroy(src.gameObject);
        }
        remoteAudioSources.Clear();

        try { ws?.Abort(); } catch (Exception) { }
        ws = null;
    }

    // Called by Unity when the application is about to quit.
    // Fires BEFORE OnDestroy in a real build, giving us a chance to close connections.
    // NOTE: In the Unity Editor, Stop-Play cycles do NOT reliably fire OnApplicationQuit
    // before OnDestroy, so we reset _cleanedUp in Start() to handle re-play correctly.
    void OnApplicationQuit()
    {
        Cleanup();
        Debug.Log("[AetherCare] Application quitting — room left cleanly.");
    }

    // Called by Unity when this GameObject is destroyed (scene unload, Destroy() call, etc.).
    // Cleanup() is idempotent — safe to call even if OnApplicationQuit already ran.
    void OnDestroy()
    {
        Cleanup();
    }

    /// <summary>
    /// Single source of truth for all teardown logic.
    /// Idempotent: safe to call multiple times (guarded by _cleanedUp flag).
    /// </summary>
    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        running = false;
        StopAllCoroutines(); // stop Firebase polling loop, stats loop, etc.

        // Close all peer connections before disposing the tracks they reference.
        foreach (var pc in peerConnections.Values)
        {
            try { pc.Close(); } catch (Exception) { }
        }
        peerConnections.Clear();
        dataChannels.Clear();
        knownPeers.Clear();

        // Dispose remote video tracks.
        foreach (var track in remoteVideoTracks.Values) track?.Dispose();
        remoteVideoTracks.Clear();

        // Destroy per-peer child GameObjects that hold AudioSources.
        foreach (var src in remoteAudioSources.Values)
        {
            if (src != null) Destroy(src.gameObject);
        }
        remoteAudioSources.Clear();

        // Dispose the local capture tracks.
        localVideoTrack?.Dispose();
        localAudioTrack?.Dispose();

        // Close the WebSocket connection.
        try { ws?.Abort(); } catch (Exception) { }
        ws = null;

        // Cleanup LanTcp if used
        var tcp = gameObject.GetComponent<LanTcpSignaling>();
        if (tcp != null) Destroy(tcp);

        // Unity WebRTC 3.x: WebRTC.Dispose() was removed —
        // lifecycle is managed automatically by the package.
    }
    #endregion // Lifecycle
}