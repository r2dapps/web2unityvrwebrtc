using UnityEngine;

/// <summary>
/// Example module for a Tactical Radio or Walkie-Talkie.
/// This configures the WebRTC engine to transmit Audio ONLY (no video),
/// acting as a Spoke that connects to a central command Hub.
/// </summary>
public class TacticalRadioModule : MonoBehaviour
{
    [Header("WebRTC Engine Reference")]
    public UnityVRWebRTC webrtcEngine;
    
    [Header("Radio Settings")]
    public string currentFrequency = "SQUAD-BRAVO";
    
    private bool _isTransmitting = false;

    private void Start()
    {
        if (webrtcEngine == null)
        {
            Debug.LogError("[TacticalRadio] WebRTC Engine not assigned!");
            return;
        }

        // 1. Configure for "Walkie-Talkie Audio Only" mode
        webrtcEngine.sendCamera = false;     // No video needed
        webrtcEngine.receiveVideo = false;   // No video needed
        webrtcEngine.sendMicrophone = true;  // Audio is active

        // 2. Set the topology (Squad members are Spokes connecting to Command)
        webrtcEngine.topology = UnityVRWebRTC.Topology.HubSpoke;
        webrtcEngine.myRole = UnityVRWebRTC.PeerRole.Spoke;

        // 3. Start muted by default (Wait for Push-To-Talk)
        webrtcEngine.isMicrophoneMuted = true;

        // 4. Connect to frequency
        ConnectToFrequency(currentFrequency);
    }

    /// <summary>
    /// Change the radio frequency dynamically and reconnect to a new WebRTC room.
    /// </summary>
    public void ConnectToFrequency(string newFrequency)
    {
        if (webrtcEngine == null) return;

        // Disconnect from old frequency if already connected
        webrtcEngine.LeaveRoom();

        currentFrequency = newFrequency;
        webrtcEngine.roomKey = currentFrequency;
        webrtcEngine.ConnectNow();
        
        Debug.Log($"[TacticalRadio] Tuning radio to frequency: {currentFrequency}");
    }

    /// <summary>
    /// Push-to-Talk (PTT) implementation. 
    /// Pass true when button is held down, false when released.
    /// </summary>
    public void PushToTalk(bool isPressed)
    {
        if (webrtcEngine == null) return;

        _isTransmitting = isPressed;
        
        // We set muted = !isPressed (if pressed, we are NOT muted)
        webrtcEngine.SetMicrophoneMuted(!_isTransmitting);
        
        if (_isTransmitting)
        {
            Debug.Log("[TacticalRadio] 🔊 Transmitting...");
        }
        else
        {
            Debug.Log("[TacticalRadio] 🔇 Radio silent.");
        }
    }
}
