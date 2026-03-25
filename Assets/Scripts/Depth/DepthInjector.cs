using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARDepthRefinement
{
    /// <summary>
    /// Singleton that receives raw float32 depth bytes from the server
    /// (via FrameStreamer's WebSocket callback) and exposes them as a
    /// RenderTexture that the pipeline manager can read each frame.
    /// </summary>
    public class DepthInjector : MonoBehaviour
    {
        public static DepthInjector Instance { get; private set; }

        [Header("Depth RT (auto-created if null)")]
        public RenderTexture ServerDepthRT;

        [Header("Settings")]
        [SerializeField] int depthWidth = 320;
        [SerializeField] int depthHeight = 240;

        // Thread-safe queue: background WS thread → main thread
        readonly Queue<(byte[] data, int w, int h, float rttMs)> _pendingFrames = new();
        readonly object _lock = new();

        Texture2D _staging;   // CPU staging texture, reused every frame

        // ──────────────────────────────────────────────────────────────────────
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (ServerDepthRT == null)
                ServerDepthRT = CreateRT(depthWidth, depthHeight);

            _staging = new Texture2D(depthWidth, depthHeight, TextureFormat.RFloat, false);
        }

        RenderTexture CreateRT(int w, int h)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.RHalf, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            var rt = new RenderTexture(desc) { name = "ServerDepthRT" };
            rt.Create();
            return rt;
        }

        // ── Called from any thread (WebSocket receive thread) ─────────────────
        /// <param name="floatBytes">Raw IEEE-754 float32 bytes, row-major</param>
        /// <param name="w">Width of the depth map (must match server INFER_SIZE)</param>
        /// <param name="h">Height of the depth map (must match server INFER_SIZE)</param>
        /// <param name="rttMs">Round-trip time in milliseconds, already computed by caller</param>
        public void EnqueueServerDepth(byte[] floatBytes, int w, int h, float rttMs)
        {
            lock (_lock)
            {
                // Keep only the latest frame — drop older ones to avoid lag
                _pendingFrames.Clear();
                _pendingFrames.Enqueue((floatBytes, w, h, rttMs));
            }
        }

        // ── Upload on main thread ─────────────────────────────────────────────
        void Update()
        {
            (byte[] data, int w, int h, float rttMs) frame = default;
            bool hasFrame = false;

            lock (_lock)
            {
                if (_pendingFrames.Count > 0)
                {
                    frame = _pendingFrames.Dequeue();
                    hasFrame = true;
                }
            }

            if (!hasFrame) return;

            // Resize staging texture if server resolution differs from last frame
            if (_staging == null || _staging.width != frame.w || _staging.height != frame.h)
            {
                if (_staging != null)
                    Destroy(_staging);
                _staging = new Texture2D(frame.w, frame.h, TextureFormat.RFloat, false);
            }

            // Resize the ServerDepthRT if needed
            if (ServerDepthRT == null || ServerDepthRT.width != frame.w || ServerDepthRT.height != frame.h)
            {
                if (ServerDepthRT != null)
                    ServerDepthRT.Release();
                ServerDepthRT = CreateRT(frame.w, frame.h);
            }

            // Upload float bytes → GPU
            _staging.LoadRawTextureData(frame.data);
            _staging.Apply();
            Graphics.Blit(_staging, ServerDepthRT);

            // Notify pipeline manager with round-trip latency
            if (ARHybridPipelineManager.Instance != null)
                ARHybridPipelineManager.Instance.NotifyServerDepthUpdated(frame.rttMs);
        }

        void OnDestroy()
        {
            if (ServerDepthRT != null)
            {
                ServerDepthRT.Release();
                ServerDepthRT = null;
            }

            if (_staging != null)
            {
                Destroy(_staging);
                _staging = null;
            }
        }
    }
}