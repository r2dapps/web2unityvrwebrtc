/*
 * LanTcpSignaling.cs
 * ---------------------------------------------------------------------
 * A minimal, fully-offline signaling transport for exactly TWO Unity
 * instances on the same LAN (or a direct ethernet cable between them —
 * no router even needed, just static/link-local IPs on both ends).
 *
 * This replaces signaling_server.py / Firebase for the Unity-to-Unity
 * case ONLY. It does not (and structurally cannot) replace them for
 * Unity-to-Web, since a browser page has no way to open a raw TCP socket
 * — that's exactly why the WebSocket/Firebase paths exist for the web
 * side. Use this specifically for "two Unity instances, no internet,
 * no server, no Python" — e.g. two Quest headsets on the same WiFi with
 * no router internet uplink at all, or a wired point-to-point test rig.
 *
 * DESIGN: one side calls StartAsHost(port) and listens; the other calls
 * ConnectAsClient(hostIp, port) and connects to it directly. Whoever
 * connects is deterministically the WebRTC offer-initiator; whoever
 * listens is the answerer — no ID comparison needed, since there are
 * only ever two parties.
 *
 * FRAMING: newline-delimited JSON. Our signaling messages are always a
 * single-line JSON object with no embedded newlines, so this is the
 * simplest correct framing for a TCP byte stream (TCP has no built-in
 * message boundaries — you must define your own, and "up to the next
 * \n" is the simplest one that works here).
 *
 * WIRE FORMAT: identical "kind" envelope already used elsewhere in this
 * project — {"kind":"offer","sdp":"..."}, {"kind":"answer","sdp":"..."},
 * {"kind":"ice","candidate":{...}} — so this drops in next to the
 * existing GetOrCreatePeer/OnIceCandidate/HandleRemoteSignal code with
 * no changes to how those messages are built or parsed, only to how
 * they're transported.
 *
 * LIMITS — read before assuming this scales:
 *   - Exactly 2 parties. There is no room/roster concept here at all;
 *     extending this to 3+ peers would mean either (a) one side becomes
 *     a relay hub for the others — which is just reinventing a
 *     signaling server, the thing this was meant to avoid — or (b) a
 *     full mesh of direct TCP connections, one pair per two peers,
 *     which gets unwieldy fast. For 3+ peers, use signaling_server.py
 *     or Firebase instead; this class is specifically for the simple
 *     2-party offline case.
 *   - Both devices must already know how to reach each other — a
 *     hostname/IP + port, typed in or hardcoded. There is no discovery
 *     here (see UDP broadcast discovery in the wider project's
 *     server.py if you want that layered on top later).
 *   - Once the WebRTC PeerConnection itself is established, THIS TCP
 *     link is no longer needed for media — audio/video/data all flow
 *     through WebRTC's own connection from that point on. You can
 *     safely close this TCP link after signaling completes, or leave
 *     it open cheaply as a simple "are we still on the same network"
 *     heartbeat if useful.
 * ---------------------------------------------------------------------
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class LanTcpSignaling : MonoBehaviour
{
    /// <summary>True once a TCP connection to the other Unity instance exists (either as host or client).</summary>
    public bool IsConnected { get; private set; }

    /// <summary>True if this instance is the one that called ConnectAsClient — by convention, the offer-initiator.</summary>
    public bool IAmInitiator { get; private set; }

    /// <summary>Fired once the TCP link is up, before any signaling messages have necessarily been exchanged.</summary>
    public event Action OnConnected;

    /// <summary>Fired when the TCP link drops for any reason.</summary>
    public event Action OnDisconnected;

    /// <summary>Fired for every complete line (one JSON signaling message) received.</summary>
    public event Action<string> OnMessageReceived;

    private TcpListener _listener;
    private TcpClient _tcpClient;
    private NetworkStream _stream;
    private CancellationTokenSource _cts;
    private readonly object _sendLock = new object();

    // ------------------------------------------------------------------
    // Host side — call this on whichever instance should listen and wait
    // ------------------------------------------------------------------
    public void StartAsHost(int port)
    {
        IAmInitiator = false;
        _cts = new CancellationTokenSource();
        _ = HostLoop(port, _cts.Token);
    }

    private async Task HostLoop(int port, CancellationToken token)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Debug.Log($"[LanTcpSignaling] Listening on port {port} — waiting for the other Unity instance to connect...");

            using (token.Register(() => { try { _listener.Stop(); } catch { } }))
            {
                _tcpClient = await _listener.AcceptTcpClientAsync();
            }

            Debug.Log($"[LanTcpSignaling] Incoming connection accepted from {_tcpClient.Client.RemoteEndPoint}.");
            await AfterConnected(token);
        }
        catch (ObjectDisposedException) { /* Stop() called during shutdown — expected, not an error */ }
        catch (Exception e)
        {
            Debug.LogError($"[LanTcpSignaling] Host listen failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Client side — call this on the instance that already knows the
    // host's IP address (typed in, or hardcoded for a fixed test rig)
    // ------------------------------------------------------------------
    public void ConnectAsClient(string hostIp, int port)
    {
        IAmInitiator = true;
        _cts = new CancellationTokenSource();
        _ = ClientLoop(hostIp, port, _cts.Token);
    }

    private async Task ClientLoop(string hostIp, int port, CancellationToken token)
    {
        try
        {
            _tcpClient = new TcpClient();
            Debug.Log($"[LanTcpSignaling] Connecting to {hostIp}:{port}...");
            await _tcpClient.ConnectAsync(hostIp, port);
            Debug.Log("[LanTcpSignaling] Connected.");
            await AfterConnected(token);
        }
        catch (Exception e)
        {
            Debug.LogError($"[LanTcpSignaling] Connect to {hostIp}:{port} failed: {e.Message}. " +
                            "Check both devices are on the same LAN/subnet, the port isn't blocked by " +
                            "a firewall, and the IP is actually current (it can change on WiFi reconnects).");
        }
    }

    // ------------------------------------------------------------------
    // Shared: once connected (either role), read newline-delimited
    // messages until disconnected or stopped.
    // ------------------------------------------------------------------
    private async Task AfterConnected(CancellationToken token)
    {
        _stream = _tcpClient.GetStream();
        IsConnected = true;
        OnConnected?.Invoke();

        var buffer = new byte[8192];
        var lineBuilder = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead == 0) break; // remote closed the connection

                string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                lineBuilder.Append(chunk);

                // Split on newline — there may be zero, one, or several
                // complete messages in this chunk, plus a partial one.
                int newlineIdx;
                while ((newlineIdx = lineBuilder.ToString().IndexOf('\n')) >= 0)
                {
                    string line = lineBuilder.ToString(0, newlineIdx).TrimEnd('\r');
                    lineBuilder.Remove(0, newlineIdx + 1);
                    if (!string.IsNullOrWhiteSpace(line))
                        OnMessageReceived?.Invoke(line);
                }
            }
        }
        catch (OperationCanceledException) { /* Stop() called — expected */ }
        catch (Exception e)
        {
            Debug.LogWarning($"[LanTcpSignaling] Connection lost: {e.Message}");
        }
        finally
        {
            IsConnected = false;
            OnDisconnected?.Invoke();
        }
    }

    /// <summary>
    /// Sends one signaling message (a single-line JSON string — do not
    /// pass anything containing a literal newline). Safe to call from
    /// any thread; writes are serialized with a lock since NetworkStream
    /// doesn't support concurrent writes.
    /// </summary>
    public void SendSignalingMessage(string json)
    {
        if (!IsConnected || _stream == null)
        {
            Debug.LogWarning("[LanTcpSignaling] SendSignalingMessage called with no active connection — dropped.");
            return;
        }
        if (json.Contains("\n"))
        {
            Debug.LogError("[LanTcpSignaling] Message contains a literal newline, which breaks the framing — refusing to send.");
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (_sendLock)
        {
            try
            {
                _stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LanTcpSignaling] SendSignalingMessage failed: {e.Message}");
            }
        }
    }

    public void StopAndClose()
    {
        _cts?.Cancel();
        try { _stream?.Close(); } catch { }
        try { _tcpClient?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        IsConnected = false;
    }

    void OnDestroy() => StopAndClose();
}

/*
 * ---------------------------------------------------------------------
 * INTEGRATION EXAMPLE — wiring this into the existing RTCPeerConnection
 * pattern from UnityVRWebRTC.cs. This is deliberately NOT merged into
 * that file directly (see GEMINI_HANDOFF.md) — wire it in as a third
 * signaling option alongside Firebase/LocalServer once you're ready,
 * reusing GetOrCreatePeer's existing sendSignal(peerId, json) shape:
 *
 *   var tcp = gameObject.AddComponent<LanTcpSignaling>();
 *   tcp.OnConnected += () => {
 *     var pc = GetOrCreatePeer("lan-peer", tcp.IAmInitiator, (_, json) => tcp.SendSignalingMessage(json));
 *     if (tcp.IAmInitiator) {
 *         // existing offer-creation coroutine, same as the WS/Firebase paths
 *     }
 *   };
 *   tcp.OnMessageReceived += (json) => {
 *     // existing HandleRemoteSignal("lan-peer", json)-equivalent parsing,
 *     // exactly like the "signal" message branch in LocalServerSignalingLoop
 *   };
 *
 *   // One device (whichever you designate — e.g. always the "left" headset):
 *   tcp.StartAsHost(9091);
 *   // The other device, once it knows the host's LAN IP:
 *   tcp.ConnectAsClient("192.168.1.50", 9091);
 * ---------------------------------------------------------------------
 */
