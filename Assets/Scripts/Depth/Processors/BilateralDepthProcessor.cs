using UnityEngine;

namespace ARDepthRefinement
{
    [CreateAssetMenu(menuName = "AR Depth Refinement/Processors/Bilateral Depth Processor")]
    public class BilateralDepthProcessor : DepthProcessorBase
    {
        [Header("Shader / Material")]
        public Shader shader;

        [Header("Filter Settings")]
        [Range(1, 3)]
        public int radius = 1;

        [Range(0.0001f, 0.1f)]
        public float depthSigma = 0.01f;

        [Range(0.1f, 10f)]
        public float spatialSigma = 2.0f;

        private Material _mat;
        private int _w, _h;

        public override void Initialize(int width, int height)
        {
            _w = width;
            _h = height;

            if (shader == null)
            {
                Debug.LogError("EdgeAwareSmoothingProcessor: Missing shader reference.");
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
            if (_mat == null) return;

            _mat.SetTexture("_InputDepth", inputDepth);
            _mat.SetVector("_TexelSize", new Vector4(1f / _w, 1f / _h, _w, _h));
            _mat.SetInt("_Radius", radius);
            _mat.SetFloat("_DepthSigma", depthSigma);
            _mat.SetFloat("_SpatialSigma", spatialSigma);

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