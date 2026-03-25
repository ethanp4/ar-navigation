using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace ARDepthRefinement
{
    public class DepthRefinementController : MonoBehaviour
    {
        [Header("AR Foundation")]
        public AROcclusionManager occlusionManager;

        [Header("Pipeline (ScriptableObject processors)")]
        public List<DepthProcessorBase> processors = new List<DepthProcessorBase>();

        [Header("Output")]
        [Tooltip("Downscale factor for processing (1 = full, 2 = half, 4 = quarter).")]
        [Range(1, 4)]
        public int downscale = 1;

        [Tooltip("Global shader name to expose the refined depth texture.")]
        public string refinedDepthGlobalName = "_RefinedDepthTex";

        public RenderTexture RefinedDepth => _rtB;

        private RenderTexture _rtA;
        private RenderTexture _rtB;

        private int _w, _h;

        void Reset()
        {
            occlusionManager = GetComponent<AROcclusionManager>();
        }

        void OnEnable()
        {
            if (occlusionManager == null)
                occlusionManager = GetComponent<AROcclusionManager>();
        }

        void OnDisable()
        {
            DisposeRTs();
            foreach (var p in processors)
                if (p != null) p.Dispose();
        }

        void Update()
        {
            if (occlusionManager == null) return;

            Texture rawDepth;
            if (!occlusionManager.TryGetEnvironmentDepthTexture(out rawDepth) || rawDepth == null)
            {
                // Depth not available this frame
                return;
            }

            int targetW = Mathf.Max(1, rawDepth.width / downscale);
            int targetH = Mathf.Max(1, rawDepth.height / downscale);

            EnsureRTs(targetW, targetH);

            // Initialize processors on first run / size change
            foreach (var p in processors)
                if (p != null) p.Initialize(_w, _h);

            // Start with raw depth as input
            Texture currentInput = rawDepth;

            bool hasProcessedAnything = false;

            // Run pipeline
            for (int i = 0; i < processors.Count; i++)
            {
                var p = processors[i];
                if (p == null || !p.enabledInPipeline)
                    continue;

                RenderTexture currentOutput = hasProcessedAnything && currentInput == _rtA ? _rtB : _rtA;

                p.Process(currentInput, currentOutput);

                Debug.Log($"Ran processor: {p.name}");

                currentInput = currentOutput;
                hasProcessedAnything = true;
            }

            if (!hasProcessedAnything)
            {
                Graphics.Blit(rawDepth, _rtB);
            }
            else if (currentInput != _rtB)
            {
                Graphics.Blit(currentInput, _rtB);
            }

            Shader.SetGlobalTexture(refinedDepthGlobalName, _rtB);
        }

        private void EnsureRTs(int width, int height)
        {
            if (_rtA != null && _rtB != null && _w == width && _h == height)
                return;

            DisposeRTs();

            _w = width;
            _h = height;

            // Mobile-friendly single channel half precision RT
            // If your Unity version/device complains, change format to RenderTextureFormat.RFloat
            var desc = new RenderTextureDescriptor(_w, _h, RenderTextureFormat.ARGB32, 0);
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;

            _rtA = new RenderTexture(desc) { name = "DepthRT_A" };
            _rtB = new RenderTexture(desc) { name = "DepthRT_B" };
            _rtA.Create();
            _rtB.Create();
        }

        private void DisposeRTs()
        {
            if (_rtA != null) { _rtA.Release(); Destroy(_rtA); _rtA = null; }
            if (_rtB != null) { _rtB.Release(); Destroy(_rtB); _rtB = null; }
        }
    }
}