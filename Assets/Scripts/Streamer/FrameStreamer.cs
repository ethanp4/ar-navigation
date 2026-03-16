using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using NativeWebSocket;   // install via: https://github.com/endel/NativeWebSocket

namespace ARDepthRefinement
{
    /// <summary>
    /// Captures the AR camera image every N frames, compresses to JPEG,
    /// and sends it to the depth server over WebSocket.
    /// The 4-byte header prefixed to each message is the send timestamp
    /// (float32 realtimeSinceStartup) so we can compute round-trip latency.
    /// </summary>
    public class FrameStreamer : MonoBehaviour
    {
        [Header("AR Camera")]
        [SerializeField] ARCameraManager cameraManager;

        [Header("Server")]
        [Tooltip("Set automatically by ServerDiscovery.")]
        [HideInInspector] public string serverUrl = "";

        [Header("Streaming Quality")]
        [Range(1, 6)]
        [Tooltip("Send every Nth frame. 3 = ~10fps at 30fps game, keeps bandwidth manageable.")]
        [SerializeField] int frameInterval = 3;
        [Range(10, 90)]
        [Tooltip("JPEG quality. 50-60 is the sweet spot for this use case.")]
        [SerializeField] int jpegQuality = 55;
        [SerializeField] int captureWidth = 320;
        [SerializeField] int captureHeight = 240;

        [Header("Reconnection")]
        [SerializeField] float reconnectDelaySec = 3f;

        // ── State ─────────────────────────────────────────────────────────────
        WebSocket _ws;
        Texture2D _captureTex;
        int _frameCount = 0;
        bool _connecting = false;

        /// <summary>
        /// Set to true by ServerDiscovery once the server IP is resolved.
        /// FrameStreamer will not attempt to connect until this is true.
        /// If you are not using ServerDiscovery (e.g. testing with a fixed IP),
        /// set this to true manually or add a fallback IP in the inspector.
        /// </summary>
        
        [HideInInspector] public bool discoveryReady = false;

        [HideInInspector]
        public bool IsConnected =>
            _ws != null && _ws.State == WebSocketState.Open;

        // ──────────────────────────────────────────────────────────────────────
        void Start()
        {
            _captureTex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
            StartCoroutine(ConnectLoop());
        }

        IEnumerator ConnectLoop()
        {
            while (true)
            {
                if (!IsConnected && !_connecting)
                    yield return StartCoroutine(Connect());
                yield return new WaitForSeconds(reconnectDelaySec);
            }
        }

        IEnumerator Connect()
        {
            _connecting = true;
            Debug.Log($"[FrameStreamer] Connecting to {serverUrl}...");

            _ws = new WebSocket(serverUrl);

            _ws.OnOpen += () => { Debug.Log("[FrameStreamer] Connected to depth server."); };
            _ws.OnClose += (code) => { Debug.LogWarning($"[FrameStreamer] Disconnected: {code}"); };
            _ws.OnError += (err) => { Debug.LogError($"[FrameStreamer] WS error: {err}"); };
            _ws.OnMessage += OnMessageReceived;

            // NativeWebSocket Connect() returns a Task — yield a frame then mark done
            var connectTask = _ws.Connect();
            yield return new WaitUntil(() => connectTask.IsCompleted);
            _connecting = false;
        }

        // ── Per-frame capture & send ──────────────────────────────────────────
        void Update()
        {
            if (_ws != null) _ws.DispatchMessageQueue();
            if (!IsConnected) return;

            if (++_frameCount % frameInterval != 0) return;

            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImg)) return;

            using (cpuImg)
            {
                var convParams = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, captureWidth, captureHeight),
                    outputDimensions = new Vector2Int(captureWidth, captureHeight),
                    outputFormat = TextureFormat.RGB24,
                    transformation = XRCpuImage.Transformation.MirrorY
                };

                // Synchronous — fast enough for 320×240
                cpuImg.Convert(convParams, _captureTex.GetRawTextureData<byte>());
                _captureTex.Apply();
            }

            byte[] jpg = _captureTex.EncodeToJPG(jpegQuality);

            // Prepend 4-byte float timestamp for RTT measurement
            float timestamp = Time.realtimeSinceStartup;
            byte[] tsBytes = BitConverter.GetBytes(timestamp);
            byte[] payload = new byte[4 + jpg.Length];
            Buffer.BlockCopy(tsBytes, 0, payload, 0, 4);
            Buffer.BlockCopy(jpg, 0, payload, 4, jpg.Length);

            _ = _ws.Send(payload);
        }

        // ── Receive depth map from server ─────────────────────────────────────
        void OnMessageReceived(byte[] data)
        {
            // Expected: 4 bytes timestamp echo + float32 depth map bytes
            if (data.Length < 4 + captureWidth * captureHeight * 4)
            {
                Debug.LogWarning("[FrameStreamer] Received undersized message, skipping.");
                return;
            }

            float sentTime = BitConverter.ToSingle(data, 0);

            byte[] depthBytes = new byte[data.Length - 4];
            Buffer.BlockCopy(data, 4, depthBytes, 0, depthBytes.Length);

            if (DepthInjector.Instance != null)
                DepthInjector.Instance.EnqueueServerDepth(depthBytes, sentTime);
        }

        // ──────────────────────────────────────────────────────────────────────
        async void OnApplicationQuit()
        {
            if (_ws != null) await _ws.Close();
        }

        void OnDestroy()
        {
            if (_captureTex != null) Destroy(_captureTex);
        }
    }
}