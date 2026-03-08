using UnityEngine;
using UnityEngine.XR.ARFoundation;

public class DepthConfirmation : MonoBehaviour
{
    public AROcclusionManager occlusion;

    void Update()
    {
        if (!occlusion) return;
        if (occlusion.TryGetEnvironmentDepthTexture(out _))
            Debug.Log("Depth ACTIVE");
        else
            Debug.Log("Depth NOT available");
    }
}