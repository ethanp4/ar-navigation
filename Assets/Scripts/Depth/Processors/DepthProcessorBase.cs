using UnityEngine;

namespace ARDepthRefinement
{
    public abstract class DepthProcessorBase : ScriptableObject
    {
        /*
         * This class is an interface for all controllers
         */

        [Tooltip("This processor can be turned on and off")]
        public bool enabledInPipeline = true;

        public virtual void Initialize(int width, int height) { }

        public abstract void Process(Texture inputDepth, RenderTexture outputDepth);

        public virtual void Dispose() { }
    }


}