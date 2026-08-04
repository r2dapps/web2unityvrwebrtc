using UnityEngine;

/// <summary>
/// Example module for a Drone or Unmanned Ground Vehicle (UGV).
/// This configures the WebRTC engine to transmit video ONLY (no audio),
/// acting as a Hub broadcaster for remote viewers to watch the feed.
/// </summary>
public class DroneCommsModule : MonoBehaviour
{
    [Header("WebRTC Engine Reference")]
    public UnityVRWebRTC webrtcEngine;
    
    [Header("Drone Hardware")]
    public Camera droneNoseCamera;
    public string secureChannelKey = "DRONE-ALPHA-99";

    private bool _isStealthMode = false;

    private void Start()
    {
        if (webrtcEngine == null)
        {
            Debug.LogError("[DroneComms] WebRTC Engine not assigned!");
            return;
        }

        // 1. Configure for "Drone Video Only" mode
        webrtcEngine.sendCamera = true;
        webrtcEngine.sendMicrophone = false; // No wind/motor noise
        webrtcEngine.receiveVideo = false;   // Drone doesn't need to watch remote video

        // 2. Assign the specific camera we want to broadcast
        if (droneNoseCamera != null)
        {
            webrtcEngine.vrStreamCamera = droneNoseCamera;
        }
        
        // 3. Set the room topology (Drone acts as the Hub broadcasting to viewers)
        webrtcEngine.topology = UnityVRWebRTC.Topology.HubSpoke;
        webrtcEngine.myRole = UnityVRWebRTC.PeerRole.Hub;

        // 4. Automatically boot up and connect on start
        webrtcEngine.roomKey = secureChannelKey;
        webrtcEngine.ConnectNow();
        
        Debug.Log($"[DroneComms] Drone online and broadcasting on channel: {secureChannelKey}");
    }

    /// <summary>
    /// Instantly kills the video feed without dropping the WebRTC connection.
    /// Can be wired to a UI button or input system.
    /// </summary>
    public void ToggleStealthMode()
    {
        if (webrtcEngine == null) return;

        _isStealthMode = !_isStealthMode;
        webrtcEngine.sendCamera = !_isStealthMode;
        
        Debug.Log($"[DroneComms] Stealth Mode: {_isStealthMode}. Camera broadcasting: {webrtcEngine.sendCamera}");
    }

    /// <summary>
    /// Completely severs the connection and shuts down the drone's transmission.
    /// </summary>
    public void SelfDestructFeed()
    {
        if (webrtcEngine != null)
        {
            webrtcEngine.LeaveRoom();
            Debug.Log("[DroneComms] Feed severed permanently.");
        }
    }
}
