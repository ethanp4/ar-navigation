using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ARDepthRefinement
{
    /// <summary>
    /// Listens on UDP port 47777 for the server's broadcast beacon.
    /// When found, automatically sets FrameStreamer.serverUrl and triggers connection.
    ///
    /// Add this component to the same GameObject as FrameStreamer.
    /// FrameStreamer will NOT attempt to connect until this component
    /// has resolved the server IP.
    ///
    /// Status is exposed via the DiscoveryStatus enum so your UI can show
    /// "Searching for server..." before the connection is established.
    /// </summary>
    public class ServerDiscovery : MonoBehaviour
    {
        public enum DiscoveryStatus
        {
            Searching,
            Found,
            Failed
        }

        [Header("Discovery Settings")]
        [SerializeField] int discoveryPort = 47777;
        [SerializeField] float timeoutSeconds = 30f;
        [Tooltip("Magic prefix the server beacon must start with.")]
        [SerializeField] string beaconPrefix = "AR_DEPTH_SERVER";
        [SerializeField] int wsPort = 8765;

        [Header("References")]
        [SerializeField] FrameStreamer frameStreamer;

        // ── Public state ──────────────────────────────────────────────────────
        public DiscoveryStatus Status { get; private set; } = DiscoveryStatus.Searching;
        public string ResolvedIP { get; private set; } = string.Empty;

        // ── Internal ──────────────────────────────────────────────────────────
        UdpClient _udp;
        Thread _listenThread;
        bool _found = false;
        bool _running = false;

        // Thread-safe pending IP to hand off to main thread
        string _pendingIP = null;
        readonly object _ipLock = new object();

        float _searchTimer = 0f;

        // ──────────────────────────────────────────────────────────────────────
        void Start()
        {
            if (frameStreamer == null)
                frameStreamer = GetComponent<FrameStreamer>();

            if (frameStreamer == null)
            {
                Debug.LogError("[ServerDiscovery] No FrameStreamer found. Attach both to the same GameObject.");
                Status = DiscoveryStatus.Failed;
                return;
            }

            // Block FrameStreamer from connecting until we have an IP
            frameStreamer.discoveryReady = false;

            StartListening();
        }

        void StartListening()
        {
            _running = true;
            Status = DiscoveryStatus.Searching;

            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                _udp.EnableBroadcast = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ServerDiscovery] Could not bind UDP port {discoveryPort}: {e.Message}");
                Status = DiscoveryStatus.Failed;
                return;
            }

            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "ServerDiscovery" };
            _listenThread.Start();

            Debug.Log($"[ServerDiscovery] Listening for server beacon on UDP port {discoveryPort}...");
        }

        // ── Background thread ─────────────────────────────────────────────────
        void ListenLoop()
        {
            IPEndPoint remoteEP = new(IPAddress.Any, 0);

            while (_running && !_found)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remoteEP);
                    string msg = Encoding.UTF8.GetString(data);

                    if (!msg.StartsWith(beaconPrefix))
                        continue;

                    // Beacon format: "AR_DEPTH_SERVER:<ip>"
                    // The IP after the colon is the authoritative address the server knows about itself.
                    // Fall back to remoteEP.Address if the beacon has no colon.
                    string ip = remoteEP.Address.ToString();

                    int colonIdx = msg.IndexOf(':');
                    if (colonIdx >= 0 && colonIdx < msg.Length - 1)
                        ip = msg.Substring(colonIdx + 1).Trim();

                    lock (_ipLock)
                    {
                        _pendingIP = ip;
                    }

                    _found = true;
                }
                catch (SocketException)
                {
                    // Socket was closed from main thread — normal shutdown
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ServerDiscovery] UDP receive error: {e.Message}");
                }
            }
        }

        // ── Main thread polling ───────────────────────────────────────────────
        void Update()
        {
            if (Status == DiscoveryStatus.Found || Status == DiscoveryStatus.Failed)
                return;

            // Check for resolved IP from background thread
            string ip = null;
            lock (_ipLock)
            {
                if (_pendingIP != null)
                {
                    ip = _pendingIP;
                    _pendingIP = null;
                }
            }

            if (ip != null)
            {
                OnServerFound(ip);
                return;
            }

            // Timeout check
            _searchTimer += Time.deltaTime;
            if (_searchTimer >= timeoutSeconds)
            {
                Debug.LogWarning($"[ServerDiscovery] No server found after {timeoutSeconds}s. " +
                                 "Make sure server_discovery.py is running on the server.");
                Status = DiscoveryStatus.Failed;
                StopListening();
            }
        }

        void OnServerFound(string ip)
        {
            ResolvedIP = ip;
            Status = DiscoveryStatus.Found;

            string wsUrl = $"ws://{ip}:{wsPort}";
            frameStreamer.serverUrl = wsUrl;
            frameStreamer.discoveryReady = true;

            Debug.Log($"[ServerDiscovery] Server found at {ip} — connecting on {wsUrl}");
            StopListening();
        }

        // ──────────────────────────────────────────────────────────────────────
        void StopListening()
        {
            _running = false;
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null;
        }

        void OnDestroy()
        {
            StopListening();
            if (_listenThread != null && _listenThread.IsAlive)
                _listenThread.Join(200);
        }
    }
}