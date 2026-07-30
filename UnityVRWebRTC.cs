using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;

/// <summary>
/// Unity C# WebRTC Peer Presence Handler (Meta Quest 3 / Quest 3S & PC VR)
/// Adapted from WalkieTalkie peerManager.ts architecture.
/// </summary>
public class UnityVRWebRTC : MonoBehaviour
{
    [Header("VR Display Target (Doctor Video Receiver)")]
    public Material doctorDisplayMaterial;

    [Header("VR Camera Stream (Sender to Doctor)")]
    public Camera vrStreamCamera;
    public int streamWidth = 1280;
    public int streamHeight = 720;

    [Header("Clinic Session Configuration")]
    public string firebaseDatabaseUrl = "https://web2rvrwebrtc-default-rtdb.firebaseio.com";
    public string roomKey = "DOC-8921"; // Doctor Room Key

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack localVideoTrack;
    private string myPeerId;

    [Serializable]
    public class PeerPresence
    {
        public string peerId;
        public string role;
        public long timestamp;
    }

    [Serializable]
    public class SDPPayload
    {
        public string sdp;
        public string type;
    }

    void Start()
    {
        // 1. Generate Unique VR Peer ID (e.g. w2r-DOC8921-vr-9a2f)
        string sRoom = roomKey.Replace("-", "").ToUpper();
        string rand = UnityEngine.Random.Range(1000, 9999).ToString();
        myPeerId = $"w2r-{sRoom}-vr-{rand}";

        StartCoroutine(InitializeWebRTCFlow(sRoom));
    }

    private IEnumerator InitializeWebRTCFlow(string sRoom)
    {
        // 2. Initialize WebRTC Subsystem
        WebRTC.Initialize();

        RTCConfiguration config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
                new RTCIceServer { urls = new[] { "stun:stun1.l.google.com:19302" } },
                new RTCIceServer { urls = new[] { "stun:stun.cloudflare.com:3478" } }
            }
        };

        peerConnection = new RTCPeerConnection(ref config);
        peerConnection.OnTrack = OnTrackReceived;

        // 3. Add VR Camera Stream
        if (vrStreamCamera != null)
        {
            localVideoTrack = vrStreamCamera.CaptureStreamTrack(streamWidth, streamHeight, 30);
            peerConnection.AddTrack(localVideoTrack);
            Debug.Log("[Unity VR] VR Camera track added.");
        }

        // 4. Register VR Presence in Firebase Room Directory
        PeerPresence presence = new PeerPresence
        {
            peerId = myPeerId,
            role = "vr",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        string presenceJson = JsonUtility.ToJson(presence);
        yield return StartCoroutine(PutFirebaseData($"telemedicine_rooms/{sRoom}/peers/{myPeerId}.json", presenceJson));

        Debug.Log($"[Unity VR] Registered presence in room {sRoom} with ID: {myPeerId}");

        // 5. Poll for Doctor Offer in Room
        string offerUrl = $"{firebaseDatabaseUrl}/telemedicine_rooms/{sRoom}/offers.json";
        SDPPayload offerPayload = null;

        while (offerPayload == null || string.IsNullOrEmpty(offerPayload.sdp))
        {
            Debug.Log($"[Unity VR] Waiting for Doctor Room Offer at {sRoom}...");
            using (UnityWebRequest www = UnityWebRequest.Get(offerUrl))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(www.downloadHandler.text) && www.downloadHandler.text != "null")
                {
                    offerPayload = JsonUtility.FromJson<SDPPayload>(www.downloadHandler.text);
                }
            }
            yield return new WaitForSeconds(1.5f);
        }

        // 6. Set Remote Offer & Create Answer
        RTCSessionDescription offerDesc = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = offerPayload.sdp
        };
        var setRemoteOp = peerConnection.SetRemoteDescription(ref offerDesc);
        yield return setRemoteOp;

        var createAnswerOp = peerConnection.CreateAnswer();
        yield return createAnswerOp;
        RTCSessionDescription answerDesc = createAnswerOp.Desc;

        var setLocalOp = peerConnection.SetLocalDescription(ref answerDesc);
        yield return setLocalOp;

        // 7. Send Answer to Doctor
        SDPPayload answerPayload = new SDPPayload
        {
            sdp = answerDesc.sdp,
            type = "answer"
        };
        yield return StartCoroutine(PutFirebaseData($"telemedicine_rooms/{sRoom}/answers.json", JsonUtility.ToJson(answerPayload)));
        Debug.Log("[Unity VR] WebRTC SDP Answer sent successfully!");
    }

    private void OnTrackReceived(RTCTrackEvent evt)
    {
        if (evt.Track is VideoStreamTrack videoTrack)
        {
            Debug.Log("[Unity VR] SUCCESS! Doctor Video Track Connected!");

            videoTrack.OnVideoReceived += tex =>
            {
                if (doctorDisplayMaterial != null)
                {
                    doctorDisplayMaterial.mainTexture = tex;
                }
            };
        }
    }

    private IEnumerator PutFirebaseData(string path, string json)
    {
        string url = $"{firebaseDatabaseUrl}/{path}";
        using (UnityWebRequest www = new UnityWebRequest(url, "PUT"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();
        }
    }

    void OnDestroy()
    {
        localVideoTrack?.Dispose();
        peerConnection?.Close();
        WebRTC.Dispose();
    }
}
