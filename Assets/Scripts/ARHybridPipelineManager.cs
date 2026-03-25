using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ARDepthRefinement
{
    /// <summary>
    /// Central pipeline manager.
    /// Decides whether to use server-supplied dense depth or ARCore local depth,
    /// then runs Bilateral → Confidence → Temporal in order.
    /// Drop this onto your AR Session Origin or a dedicated manager GameObject.
    /// </summary>
    public class ARHybridPipelineManager : MonoBehaviour
    {
        // ── Inspector references ───────────────────────────────────────────────
        [Header("AR Components")]
        [SerializeField] ARCameraManager arCameraManager;
        [SerializeField] AROcclusionManager arOcclusionManager;

        [Header("Processors (assign ScriptableObjects)")]
        [SerializeField] BilateralDepthProcessor bilateralProcessor;
        [SerializeField] ConfidenceFilterProcessor confidenceProcessor;
        [SerializeField] TemporalEdgeSmoothingProcessor temporalProcessor;

        [Header("Output")]
        [Tooltip("Final refined depth is written here. Assign to your occlusion shader.")]
        [SerializeField] RenderTexture finalDepthRT;

        [Header("Pipeline Settings")]
        [SerializeField] int depthWidth = 320;
        [SerializeField] int depthHeight = 240;
        [Tooltip("When server depth is older than this (seconds), fall back to ARCore depth.")]
        [SerializeField] float serverDepthStaleSec = 0.15f;

        // ── Singleton ─────────────────────────────────────────────────────────
        public static ARHybridPipelineManager Instance { get; private set; }

        // ── Internal state ─────────────────────────────────────────────────────
        RenderTexture _rtA;   // bilateral output
        RenderTexture _rtB;   // confidence output
        // temporal writes directly to finalDepthRT

        Texture2D _arcoreDepthTex;  // local ARCore depth staging
        bool _initialized = false;

        // Source selection
        public enum DepthSource { ARCore, Server, Blended }
        [HideInInspector] public DepthSource activeSource = DepthSource.ARCore;

        float _lastServerDepthTime = -999f;

        // ── Debug / status (read by UI) ────────────────────────────────────────
        [HideInInspector] public string statusMessage = "Initializing...";
        [HideInInspector] public float serverLatencyMs = 0f;

        // ──────────────────────────────────────────────────────────────────────
        void Start()
        {
            InitRenderTextures();
            InitProcessors();
            _initialized = true;
            statusMessage = "Pipeline ready – waiting for depth";
        }

        void InitRenderTextures()
        {
            _rtA = CreateRT(depthWidth, depthHeight, "BilateralOut");
            _rtB = CreateRT(depthWidth, depthHeight, "ConfidenceOut");

            if (finalDepthRT == null)
            {
                finalDepthRT = CreateRT(depthWidth, depthHeight, "FinalDepth");
                Debug.LogWarning("ARHybridPipelineManager: finalDepthRT was null – created one at runtime. " +
                                 "Assign it in the inspector and link it to your occlusion material.");
            }
        }

        RenderTexture CreateRT(int w, int h, string name)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.RHalf, 0)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            var rt = new RenderTexture(desc) { name = name };
            rt.Create();
            return rt;
        }

        void InitProcessors()
        {
            if (bilateralProcessor != null)
                bilateralProcessor.Initialize(depthWidth, depthHeight);

            if (confidenceProcessor != null)
                confidenceProcessor.Initialize(depthWidth, depthHeight);

            if (temporalProcessor != null)
                temporalProcessor.Initialize(depthWidth, depthHeight);
        }

        // ──────────────────────────────────────────────────────────────────────
        void Update()
        {
            if (!_initialized) return;

            Texture sourceDepth = PickSourceDepth();
            if (sourceDepth == null) return;

            RunPipeline(sourceDepth);
        }

        // ── Source selection ───────────────────────────────────────────────────
        Texture PickSourceDepth()
        {
            bool serverFresh = (Time.time - _lastServerDepthTime) < serverDepthStaleSec;
            bool hasServer = DepthInjector.Instance != null &&
                               DepthInjector.Instance.ServerDepthRT != null &&
                               serverFresh;

            if (hasServer)
            {
                activeSource = DepthSource.Server;
                statusMessage = $"Server depth ACTIVE  latency {serverLatencyMs:F0} ms";
                return DepthInjector.Instance.ServerDepthRT;
            }

            // Fall back to ARCore
            if (arOcclusionManager != null &&
                arOcclusionManager.TryGetEnvironmentDepthTexture(out Texture arcoreTex) &&
                arcoreTex != null)
            {
                activeSource = DepthSource.ARCore;
                statusMessage = "LOCAL ARCore depth (server offline/stale)";
                return arcoreTex;
            }

            statusMessage = "No depth source available";
            return null;
        }

        // ── Pipeline execution ─────────────────────────────────────────────────
        void RunPipeline(Texture input)
        {
            // Step 1
            if (bilateralProcessor != null)
                bilateralProcessor.Process(input, _rtA);

            // Step 2
            if (confidenceProcessor != null)
                confidenceProcessor.Process(_rtA, _rtB);

            // Step 3
            if (temporalProcessor != null)
                temporalProcessor.Process(_rtB, finalDepthRT);
        }

        // ── Called by DepthInjector when a new server frame arrives ────────────
        public void NotifyServerDepthUpdated(float roundTripMs)
        {
            _lastServerDepthTime = Time.time;
            serverLatencyMs = roundTripMs;
        }

        // ──────────────────────────────────────────────────────────────────────
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            if (bilateralProcessor != null)
                bilateralProcessor.Dispose();

            if (confidenceProcessor != null)
                confidenceProcessor.Dispose();

            if (temporalProcessor != null)
                temporalProcessor.Dispose();

            if (_rtA != null)
            {
                _rtA.Release();
                _rtA = null;
            }

            if (_rtB != null)
            {
                _rtB.Release();
                _rtB = null;
            }
        }
    }
}