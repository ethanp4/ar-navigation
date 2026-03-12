using UnityEngine;

namespace ARDepthRefinement
{
    /*
     * This processor;
     * reads depth by computing a gradient g = [dx] + [dy] 
     * if the depth is == 0 it is invalid
     */

    [CreateAssetMenu(menuName = "AR Depth Refinement/Processors/Confidence Filter Processor")]
    public class ConfidenceFilterProcessor : DepthProcessorBase
    {
        [Header("Shader")]
        public Shader shader;

        [Header("Tuning Check")]
        public bool ifDiscardZeroDepth = true;

        [Tooltip("Gradient threshold. Lower = more removal of noisy edges.")]
        [Range(0.0001f, 0.1f)]
        public float gradientThreshold = 0.02f;

        [Tooltip("How strong the gradient is scaled before thresholding.")]
        [Range(0.1f, 10f)]
        public float gradientScale = 1.0f;

        private Material _mat;
        private int _w, _h;

        public override void Initialize(int width, int height)
        {
            _w = width;
            _h = height;

            if (shader == null)
            {
                Debug.LogError("ConfidenceFilterProcessor: Missing shader reference.");
                return;
            }

            if (_mat == null)
            {
                _mat = new Material(shader);
                _mat.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        public override void Process(Texture inputDepth, RenderTexture outputDepth)
        {
            if (!enabledInPipeline) return;

            if (_mat == null)
            {
                Debug.LogError("ConfidenceFilterProcessor: Material not initialized.");
                return;
            }

            _mat.SetTexture("_InputDepth", inputDepth);
            _mat.SetVector("_TexelSize", new Vector4(1f / _w, 1f / _h, _w, _h));
            _mat.SetFloat("_GradThreshold", gradientThreshold);
            _mat.SetFloat("_GradScale", gradientScale);
            _mat.SetFloat("_DiscardZero", ifDiscardZeroDepth ? 1f : 0f);

            Graphics.Blit(inputDepth, outputDepth, _mat, 0);
        }

        public override void Dispose()
        {
            if (_mat != null)
            {
                Object.Destroy(_mat);
                _mat = null;
            }
        }
    }
}

