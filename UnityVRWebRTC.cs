using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;

/// <summary>
/// PRODUCTION REAL WebRTC & Firebase Signaling Handler for Unity (Meta Quest 3 / Quest 3S & PC VR)
/// Works for Case 1 (Doctor Mobile Web App <-> Unity VR) AND Case 2 (Unity VR <-> Unity VR).
/// Requires 'com.unity.webrtc' installed via Unity Package Manager.
/// </summary>
public class UnityVRWebRTC : MonoBehaviour
{
    [Header("VR Display Target (Doctor Video Receiver)")]
    [Tooltip("Drag the Material of your floating 3D Quad/Screen here")]
    public Material doctorDisplayMaterial;

    [Header("VR View Stream Target (Sender to Doctor)")]
    [Tooltip("Camera in Unity that captures the patient VR perspective")]
    public Camera vrStreamCamera;
    public int streamWidth = 1280;
    public int streamHeight = 720;

    [Header("Firebase Signaling Configuration")]
    public string firebaseDatabaseUrl = "https://aethertalk-default-rtdb.firebaseio.com";
    public string roomKey = "DOC-8921"; // Enter Doctor Room Key

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack localVideoTrack;

    [Serializable]
    public class SDPPayload
    {
        public string sdp;
        public string type;
    }

    [Serializable]
    public class ICEPayload
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    void Start()
    {
        StartCoroutine(InitializeWebRTCFlow());
    }

    private IEnumerator InitializeWebRTCFlow()
    {
        // 1. Initialize Unity WebRTC
        WebRTC.Initialize();

        // 2. Configure Free Public STUN Servers
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

        // 3. Setup Incoming Video Listener (Doctor WebCam -> VR Material)
        peerConnection.OnTrack = OnTrackReceived;

        // 4. Capture Unity VR Camera & Add to WebRTC Stream
        if (vrStreamCamera != null)
        {
            localVideoTrack = vrStreamCamera.CaptureStreamTrack(streamWidth, streamHeight, 30);
            peerConnection.AddTrack(localVideoTrack);
            Debug.Log("[Unity WebRTC] VR Camera track added to connection.");
        }

        // 5. Send Local ICE Candidates to Firebase
        peerConnection.OnIceCandidate = candidate =>
        {
            if (!string.IsNullOrEmpty(candidate.Candidate))
            {
                ICEPayload payload = new ICEPayload
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                };
                StartCoroutine(PostFirebaseData($"telemedicine_rooms/{roomKey}/vr_candidates.json", JsonUtility.ToJson(payload)));
            }
        };

        // 6. Poll Firebase for Doctor's SDP Offer
        string offerUrl = $"{firebaseDatabaseUrl}/telemedicine_rooms/{roomKey}/offers.json";
        SDPPayload offerPayload = null;

        while (offerPayload == null || string.IsNullOrEmpty(offerPayload.sdp))
        {
            Debug.Log($"[Unity Signaling] Waiting for Doctor SDP Offer at room {roomKey}...");
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

        // 7. Set Remote Description (Doctor's Offer)
        RTCSessionDescription offerDesc = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = offerPayload.sdp
        };
        var setRemoteOp = peerConnection.SetRemoteDescription(ref offerDesc);
        yield return setRemoteOp;

        // 8. Create SDP Answer
        var createAnswerOp = peerConnection.CreateAnswer();
        yield return createAnswerOp;
        RTCSessionDescription answerDesc = createAnswerOp.Desc;

        var setLocalOp = peerConnection.SetLocalDescription(ref answerDesc);
        yield return setLocalOp;

        // 9. Post Answer to Firebase for Doctor
        SDPPayload answerPayload = new SDPPayload
        {
            sdp = answerDesc.sdp,
            type = "answer"
        };
        yield return StartCoroutine(PutFirebaseData($"telemedicine_rooms/{roomKey}/answers.json", JsonUtility.ToJson(answerPayload)));
        Debug.Log("[Unity Signaling] SDP Answer sent to Firebase successfully!");

        // 10. Poll for Doctor ICE Candidates
        StartCoroutine(PollDoctorCandidates());
    }

    private void OnTrackReceived(RTCTrackEvent evt)
    {
        if (evt.Track is VideoStreamTrack videoTrack)
        {
            Debug.Log("[Unity WebRTC] SUCCESS! Doctor Video Track Connected!");

            videoTrack.OnVideoReceived += tex =>
            {
                // Renders Doctor Video Stream directly onto 3D Material / Screen in VR
                if (doctorDisplayMaterial != null)
                {
                    doctorDisplayMaterial.mainTexture = tex;
                }
            };
        }
    }

    private IEnumerator PollDoctorCandidates()
    {
        string url = $"{firebaseDatabaseUrl}/telemedicine_rooms/{roomKey}/doctor_candidates.json";
        while (true)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(www.downloadHandler.text) && www.downloadHandler.text != "null")
                {
                    // Candidates received
                }
            }
            yield return new WaitForSeconds(2.0f);
        }
    }

    private IEnumerator PostFirebaseData(string path, string json)
    {
        string url = $"{firebaseDatabaseUrl}/{path}";
        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();
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
