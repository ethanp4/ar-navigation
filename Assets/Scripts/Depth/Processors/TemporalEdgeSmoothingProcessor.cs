using UnityEngine;

namespace ARDepthRefinement
{
    [CreateAssetMenu(menuName = "AR Depth Refinement/Processors/Temporal Edge Smoothing Processor")]
    public class TemporalEdgeSmoothingProcessor : DepthProcessorBase
    {
        [Header("Shader / Material")]
        public Shader shader;

        [Header("Temporal Settings")]
        [Range(0.01f, 1.0f)]
        public float alpha = 0.25f;

        [Tooltip("If camera motion exceeds this position delta, reset smoothing.")]
        public float motionPositionThreshold = 0.03f;

        [Tooltip("If camera motion exceeds this rotation delta, reset smoothing.")]
        public float motionRotationThreshold = 3.0f;

        private Material _mat;
        private RenderTexture _history;
        private int _w, _h;
        private bool _hasHistory = false;

        private Vector3 _prevCamPos;
        private Quaternion _prevCamRot;
        private bool _hasPrevPose = false;

        public override void Initialize(int width, int height)
        {
            _w = width;
            _h = height;

            if (shader == null)
            {
                Debug.LogError("TemporalEMAProcessor: Missing shader reference.");
                return;
            }

            if (_mat == null)
            {
                _mat = new Material(shader);
                _mat.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_history == null || _history.width != _w || _history.height != _h)
            {
                if (_history != null)
                {
                    _history.Release();
                    Object.Destroy(_history);
                }

                var desc = new RenderTextureDescriptor(_w, _h, RenderTextureFormat.RHalf, 0);
                desc.msaaSamples = 1;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;

                _history = new RenderTexture(desc);
                _history.name = "TemporalHistoryRT";
                _history.Create();
                _hasHistory = false;
            }
        }

        public override void Process(Texture inputDepth, RenderTexture outputDepth)
        {
            if (!enabledInPipeline) return;
            if (_mat == null || _history == null) return;

            Camera cam = Camera.main;
            float effectiveAlpha = alpha;

            if (cam != null)
            {
                if (_hasPrevPose)
                {
                    float posDelta = Vector3.Distance(cam.transform.position, _prevCamPos);
                    float rotDelta = Quaternion.Angle(cam.transform.rotation, _prevCamRot);

                    if (posDelta > motionPositionThreshold || rotDelta > motionRotationThreshold)
                        effectiveAlpha = 1.0f;
                }

                _prevCamPos = cam.transform.position;
                _prevCamRot = cam.transform.rotation;
                _hasPrevPose = true;
            }

            if (!_hasHistory)
            {
                Graphics.Blit(inputDepth, outputDepth);
                Graphics.Blit(outputDepth, _history);
                _hasHistory = true;
                return;
            }

            _mat.SetTexture("_CurrentDepth", inputDepth);
            _mat.SetTexture("_HistoryDepth", _history);
            _mat.SetFloat("_Alpha", effectiveAlpha);

            Graphics.Blit(inputDepth, outputDepth, _mat, 0);
            Graphics.Blit(outputDepth, _history);
        }

        public override void Dispose()
        {
            if (_mat != null)
            {
                Object.Destroy(_mat);
                _mat = null;
            }

            if (_history != null)
            {
                _history.Release();
                Object.Destroy(_history);
                _history = null;
            }

            _hasHistory = false;
            _hasPrevPose = false;
        }
    }
}