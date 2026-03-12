using UnityEngine;
using UnityEngine.UI;

namespace ARDepthRefinement
{
    public class DepthPreviewUI : MonoBehaviour
    {
        public DepthRefinementController controller;
        public RawImage previewImage;

        void Update()
        {
            if (controller != null && previewImage != null)
            {
                previewImage.texture = controller.RefinedDepth;
            }
        }
    }
}