using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;

/// <summary>
/// Native Unity C# WebRTC Gateway for Meta Quest 3 & PC VR
/// Streams live VR camera feed to Doctor Mobile Web and receives Doctor HD Video.
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
    public string firebaseDatabaseUrl = "https://walkietalkie-c0f03-default-rtdb.asia-southeast1.firebasedatabase.app";
    public string roomKey = "DOC-8921"; // Doctor Room Key

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack localVideoTrack;

    [Serializable]
    public class SDPPayload
    {
        public string sdp;
        public string type;
    }

    [Serializable]
    public class IceCandidatePayload
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    void Start()
    {
        string sRoom = roomKey.Replace("-", "").ToUpper();
        StartCoroutine(InitializeWebRTCFlow(sRoom));
    }

    private IEnumerator InitializeWebRTCFlow(string sRoom)
    {
        // 1. Initialize Unity WebRTC
        WebRTC.Initialize();

        RTCConfiguration config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
                new RTCIceServer { urls = new[] { "stun:stun1.l.google.com:19302" } },
                new RTCIceServer { urls = new[] { "stun:stun.cloudflare.com:3478" } },
                new RTCIceServer 
                { 
                    urls = new[] { "turn:openrelay.metered.ca:80", "turn:openrelay.metered.ca:443", "turn:openrelay.metered.ca:443?transport=tcp" },
                    username = "openrelay",
                    credential = "openrelay"
                }
            }
        };

        peerConnection = new RTCPeerConnection(ref config);
        peerConnection.OnTrack = OnTrackReceived;

        // 2. Add Local VR Camera Video Track
        if (vrStreamCamera != null)
        {
            localVideoTrack = vrStreamCamera.CaptureStreamTrack(streamWidth, streamHeight, 30);
            peerConnection.AddTrack(localVideoTrack);
            Debug.Log("[Unity VR] Local VR Camera Track added.");
        }

        // 3. ICE Candidate Callback to Firebase
        peerConnection.OnIceCandidate = candidate =>
        {
            if (!string.IsNullOrEmpty(candidate.Candidate))
            {
                IceCandidatePayload payload = new IceCandidatePayload
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                };
                StartCoroutine(PostFirebaseData($"web2rvr_rooms/{sRoom}/patient_candidates.json", JsonUtility.ToJson(payload)));
            }
        };

        // 4. Poll for Doctor's SDP Offer
        string offerUrl = $"{firebaseDatabaseUrl}/web2rvr_rooms/{sRoom}/offer.json";
        SDPPayload offerPayload = null;

        while (offerPayload == null || string.IsNullOrEmpty(offerPayload.sdp))
        {
            Debug.Log($"[Unity VR] Waiting for Doctor Room Offer at key: {sRoom}...");
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

        // 5. Set Remote Description (Doctor Offer)
        RTCSessionDescription offerDesc = new RTCSessionDescription
        {
            type = RTCSdpType.Offer,
            sdp = offerPayload.sdp
        };
        var setRemoteOp = peerConnection.SetRemoteDescription(ref offerDesc);
        yield return setRemoteOp;

        // 6. Create Answer
        var createAnswerOp = peerConnection.CreateAnswer();
        yield return createAnswerOp;
        RTCSessionDescription answerDesc = createAnswerOp.Desc;

        var setLocalOp = peerConnection.SetLocalDescription(ref answerDesc);
        yield return setLocalOp;

        // 7. Publish SDP Answer to Firebase
        SDPPayload answerPayload = new SDPPayload
        {
            sdp = answerDesc.sdp,
            type = "answer"
        };
        yield return StartCoroutine(PutFirebaseData($"web2rvr_rooms/{sRoom}/answer.json", JsonUtility.ToJson(answerPayload)));
        Debug.Log("[Unity VR] WebRTC SDP Answer published to Firebase successfully!");

        // 8. Poll Doctor's ICE Candidates and feed to PeerConnection
        StartCoroutine(PollDoctorCandidates(sRoom));
    }

    private IEnumerator PollDoctorCandidates(string sRoom)
    {
        string candidatesUrl = $"{firebaseDatabaseUrl}/web2rvr_rooms/{sRoom}/doctor_candidates.json";
        System.Collections.Generic.HashSet<string> processedCandidates = new System.Collections.Generic.HashSet<string>();

        while (peerConnection != null)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(candidatesUrl))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(www.downloadHandler.text) && www.downloadHandler.text != "null")
                {
                    string jsonText = www.downloadHandler.text;
                    int index = 0;
                    while ((index = jsonText.IndexOf("\"candidate\":", index)) != -1)
                    {
                        int end = jsonText.IndexOf("}", index);
                        if (end != -1)
                        {
                            string snippet = jsonText.Substring(index - 1, end - index + 2);
                            try
                            {
                                IceCandidatePayload candObj = JsonUtility.FromJson<IceCandidatePayload>(snippet);
                                if (candObj != null && !string.IsNullOrEmpty(candObj.candidate) && !processedCandidates.Contains(candObj.candidate))
                                {
                                    processedCandidates.Add(candObj.candidate);
                                    RTCIceCandidateInit init = new RTCIceCandidateInit
                                    {
                                        candidate = candObj.candidate,
                                        sdpMid = candObj.sdpMid,
                                        sdpMLineIndex = candObj.sdpMLineIndex
                                    };
                                    peerConnection.AddIceCandidate(new RTCIceCandidate(init));
                                    Debug.Log("[Unity VR] SUCCESS! Doctor ICE Candidate added to PeerConnection!");
                                }
                            }
                            catch (Exception) {}
                        }
                        index += 12;
                    }
                }
            }
            yield return new WaitForSeconds(1.0f);
        }
    }

    private void OnTrackReceived(RTCTrackEvent evt)
    {
        if (evt.Track is VideoStreamTrack videoTrack)
        {
            Debug.Log("[Unity VR] SUCCESS! Connected to Doctor Video Track!");
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

    void OnDestroy()
    {
        localVideoTrack?.Dispose();
        peerConnection?.Close();
        WebRTC.Dispose();
    }
}
