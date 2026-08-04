using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class AetherCareUIController : MonoBehaviour
{
    [Header("Dependencies")]
    public UnityVRWebRTC webrtcManager;
    [Tooltip("List of specific cameras to populate in the UI dropdown. Prevents grabbing UI cameras.")]
    public Camera[] availableCameras;

    [Header("Optional uGUI Fallbacks (Standard Unity UI)")]
    public UnityEngine.UI.Button uguiConnectBtn;
    public UnityEngine.UI.Button uguiLeaveBtn;
    public UnityEngine.UI.Toggle uguiMuteToggle;
    public UnityEngine.UI.Slider uguiVolumeSlider;
    public UnityEngine.UI.Dropdown uguiCameraDropdown;

    private UIDocument _uiDocument;
    
    // UI Toolkit Elements - Lobby
    private TextField _displayName;
    private TextField _roomKey;
    private Button _hostBtn;
    private Button _joinBtn;

    // UI Toolkit Elements - Call Screen
    private DropdownField _cameraDropdown;
    private Toggle _muteMicToggle;
    private Slider _remoteVolumeSlider;
    private Button _leaveBtn;
    private Button _toggleChatBtn;
    
    // UI Toolkit Elements - Chat
    private VisualElement _chatPanel;
    private ScrollView _chatScrollView;
    private TextField _chatInput;
    private Button _sendChatBtn;

    private RenderTexture _remoteRenderTexture;
    private Texture _latestRemoteTexture;
    private void OnEnable()
    {
        // 1. Wire up uGUI fallbacks
        if (uguiConnectBtn != null) uguiConnectBtn.onClick.AddListener(() => OnConnectClicked(false));
        if (uguiLeaveBtn != null) uguiLeaveBtn.onClick.AddListener(OnLeaveClicked);
        if (uguiMuteToggle != null && webrtcManager != null) uguiMuteToggle.onValueChanged.AddListener(v => webrtcManager.SetMicrophoneMuted(v));
        if (uguiVolumeSlider != null && webrtcManager != null) uguiVolumeSlider.onValueChanged.AddListener(v => webrtcManager.SetRemoteVolume(v));

        // 2. Wire up UI Toolkit
        _uiDocument = GetComponent<UIDocument>();
        if (_uiDocument != null && _uiDocument.rootVisualElement != null)
        {
            var root = _uiDocument.rootVisualElement;

            _displayName = root.Q<TextField>("display-name");
            _roomKey = root.Q<TextField>("room-key");
            
            _hostBtn = root.Q<Button>("host-btn");
            _joinBtn = root.Q<Button>("join-btn");
            
            _cameraDropdown = root.Q<DropdownField>("camera-dropdown");
            _muteMicToggle = root.Q<Toggle>("mute-mic");
            _remoteVolumeSlider = root.Q<Slider>("remote-volume");

            _leaveBtn = root.Q<Button>("leave-btn");
            _toggleChatBtn = root.Q<Button>("toggle-chat-btn");

            _chatPanel = root.Q<VisualElement>("chat-panel");
            _chatScrollView = root.Q<ScrollView>("chat-scrollview");
            _chatInput = root.Q<TextField>("chat-input");
            _sendChatBtn = root.Q<Button>("send-chat-btn");

            // Populate Camera Dropdown
            if (_cameraDropdown != null && availableCameras != null)
            {
                var camNames = new List<string>();
                foreach (var cam in availableCameras) {
                    if (cam != null) camNames.Add(cam.name);
                }
                _cameraDropdown.choices = camNames;
                if (camNames.Count > 0) _cameraDropdown.index = 0;
                
                _cameraDropdown.RegisterValueChangedCallback(evt => {
                    int idx = _cameraDropdown.choices.IndexOf(evt.newValue);
                    if (idx >= 0 && idx < availableCameras.Length && webrtcManager != null)
                    {
                        webrtcManager.SwitchCamera(availableCameras[idx]);
                    }
                });
            }

            SyncUIToManager();

            if (_hostBtn != null) _hostBtn.clicked += () => OnConnectClicked(true);
            if (_joinBtn != null) _joinBtn.clicked += () => OnConnectClicked(false);
            if (_leaveBtn != null) _leaveBtn.clicked += OnLeaveClicked;
            if (_sendChatBtn != null) _sendChatBtn.clicked += OnSendChatClicked;
            
            // Handle Enter key for chat
            if (_chatInput != null)
            {
                _chatInput.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        OnSendChatClicked();
                        evt.StopPropagation();
                    }
                });
            }
            
            if (_toggleChatBtn != null && _chatPanel != null)
            {
                _toggleChatBtn.clicked += () => 
                {
                    _chatPanel.ToggleInClassList("hidden");
                };
            }

            if (_roomKey != null) _roomKey.RegisterValueChangedCallback(evt => webrtcManager.roomKey = evt.newValue);
            if (_muteMicToggle != null) _muteMicToggle.RegisterValueChangedCallback(evt => webrtcManager.SetMicrophoneMuted(evt.newValue));
            if (_remoteVolumeSlider != null) _remoteVolumeSlider.RegisterValueChangedCallback(evt => webrtcManager.SetRemoteVolume(evt.newValue));
        }

        if (webrtcManager != null)
        {
            webrtcManager.OnRemoteTextureReceived += HandleRemoteTexture;
            webrtcManager.OnChatReceived += HandleIncomingChat;
            webrtcManager.OnDisconnectedFromRoom += HandleHostDisconnected;
        }
    }

    private void HandleHostDisconnected()
    {
        // Must marshal to main thread if not already, but WebRTC callbacks usually run on main thread.
        OnLeaveClicked();
    }

    private void OnDisable()
    {
        if (webrtcManager != null)
        {
            webrtcManager.OnRemoteTextureReceived -= HandleRemoteTexture;
            webrtcManager.OnChatReceived -= HandleIncomingChat;
            webrtcManager.OnDisconnectedFromRoom -= HandleHostDisconnected;
        }
    }

    private void HandleRemoteTexture(Texture tex)
    {
        _latestRemoteTexture = tex;
    }

    private void HandleIncomingChat(string fromPeerId, string text)
    {
        if (_chatScrollView != null)
        {
            AppendChatBubble(fromPeerId, text, isMe: false);
            // Optionally auto-open chat if a message is received
            if (_chatPanel != null && _chatPanel.ClassListContains("hidden"))
            {
                _chatPanel.RemoveFromClassList("hidden");
            }
        }
    }

    private void AppendChatBubble(string sender, string text, bool isMe)
    {
        if (_chatScrollView == null) return;

        var bubble = new VisualElement();
        bubble.AddToClassList("chat-bubble");
        if (isMe) bubble.AddToClassList("me");

        var senderLabel = new Label(isMe ? "You" : sender);
        senderLabel.AddToClassList("chat-bubble-sender");
        
        var textLabel = new Label(text);
        textLabel.AddToClassList("chat-bubble-text");

        bubble.Add(senderLabel);
        bubble.Add(textLabel);
        
        _chatScrollView.Add(bubble);
        
        // Scroll to bottom
        _chatScrollView.schedule.Execute(() => {
            _chatScrollView.ScrollTo(bubble);
        }).StartingIn(100);
    }

    private void Update()
    {
        if (_uiDocument == null) return;
        var root = _uiDocument.rootVisualElement;
        if (root == null) return;

        if (_latestRemoteTexture != null)
        {
            var videoContainer = root.Q<VisualElement>("video-container");
            var placeholder = root.Q<Label>("video-placeholder");

            if (videoContainer != null)
            {
                if (placeholder != null) placeholder.style.display = DisplayStyle.None;

                int w = _latestRemoteTexture.width > 0 ? _latestRemoteTexture.width : 1280;
                int h = _latestRemoteTexture.height > 0 ? _latestRemoteTexture.height : 720;

                if (_remoteRenderTexture == null || _remoteRenderTexture.width != w || _remoteRenderTexture.height != h)
                {
                    if (_remoteRenderTexture != null) _remoteRenderTexture.Release();
                    _remoteRenderTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                    _remoteRenderTexture.Create();
                }

                Graphics.Blit(_latestRemoteTexture, _remoteRenderTexture);
                
                videoContainer.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(_remoteRenderTexture));
                videoContainer.MarkDirtyRepaint();
            }
        }

        if (webrtcManager != null && webrtcManager.vrStreamCamera != null && webrtcManager.vrStreamCamera.targetTexture != null)
        {
            var localPip = root.Q<VisualElement>("local-video-pip");
            if (localPip != null)
            {
                localPip.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(webrtcManager.vrStreamCamera.targetTexture));
                localPip.MarkDirtyRepaint();
            }
        }
    }

    private void SyncUIToManager()
    {
        if (webrtcManager == null) return;
        if (_roomKey != null) _roomKey.value = webrtcManager.roomKey;
    }

    public void OnConnectClicked(bool isHost)
    {
        if (webrtcManager == null) return;

        // Apply UI input values to the manager
        if (_uiDocument != null)
        {
            if (_roomKey != null) webrtcManager.roomKey = _roomKey.value;
            webrtcManager.topology = UnityVRWebRTC.Topology.HubSpoke;
            webrtcManager.myRole = isHost ? UnityVRWebRTC.PeerRole.Hub : UnityVRWebRTC.PeerRole.Spoke;
        }

        if (uguiConnectBtn != null) uguiConnectBtn.interactable = false;

        // Hide the lobby overlay
        if (_uiDocument != null)
        {
            var lobbyOverlay = _uiDocument.rootVisualElement.Q<VisualElement>("lobby-overlay");
            if (lobbyOverlay != null) lobbyOverlay.style.display = DisplayStyle.None;
        }

        webrtcManager.ConnectNow();
    }

    public void OnLeaveClicked()
    {
        if (webrtcManager != null)
        {
            webrtcManager.LeaveRoom();
        }

        if (uguiConnectBtn != null) uguiConnectBtn.interactable = true;
        
        _latestRemoteTexture = null;
        
        // Reset UI state
        if (_uiDocument != null)
        {
            var root = _uiDocument.rootVisualElement;
            
            var placeholder = root.Q<Label>("video-placeholder");
            if (placeholder != null) placeholder.style.display = DisplayStyle.Flex;
            
            var videoContainer = root.Q<VisualElement>("video-container");
            if (videoContainer != null) videoContainer.style.backgroundImage = null;
            
            var localPip = root.Q<VisualElement>("local-video-pip");
            if (localPip != null) localPip.style.backgroundImage = null;
            
            var lobbyOverlay = root.Q<VisualElement>("lobby-overlay");
            if (lobbyOverlay != null) lobbyOverlay.style.display = DisplayStyle.Flex;
            
            if (_chatScrollView != null) _chatScrollView.Clear();
        }
    }

    public void OnSendChatClicked()
    {
        if (_chatInput != null && !string.IsNullOrEmpty(_chatInput.value))
        {
            string text = _chatInput.value;
            
            // If they entered a name, prefix the text with it so Web knows who sent it!
            string displayName = _displayName != null && !string.IsNullOrEmpty(_displayName.value) ? _displayName.value : "VR Peer";
            string msg = $"[{displayName}] {text}";
            
            webrtcManager.SendChatToAll(msg);
            AppendChatBubble("Me", text, isMe: true);
            _chatInput.value = ""; // Clear input after sending
        }
    }
}