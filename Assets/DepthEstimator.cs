using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class DepthEstimator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private AROcclusionManager arOcclusionManager;
    
    [Header("Model")]
    [SerializeField] private Unity.InferenceEngine.ModelAsset modelAsset;
    
    [Header("Visualization")]
    [SerializeField] private RawImage depthPreview;
    
    [Header("Settings")]
    [SerializeField] private int inferenceWidth = 256;
    [SerializeField] private int inferenceHeight = 256;
    [SerializeField] private int skipFrames = 2;
    
    private Unity.InferenceEngine.Model runtimeModel;
    private Unity.InferenceEngine.Worker worker;
    private int frameCount = 0;
    private Texture2D depthTexture;
    
    void Start()
    {
        runtimeModel = Unity.InferenceEngine.ModelLoader.Load(modelAsset);
        worker = new Unity.InferenceEngine.Worker(runtimeModel, Unity.InferenceEngine.BackendType.GPUCompute);
        depthTexture = new Texture2D(inferenceWidth, inferenceHeight, TextureFormat.RFloat, false);
        
        // Assign texture to UI
        if (depthPreview != null)
        {
            depthPreview.texture = depthTexture;
        }
        
        Debug.Log("Depth Estimator initialized");
    }
    
    void Update()
    {
        frameCount++;
        
        if (frameCount % (skipFrames + 1) != 0) return;
        
        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            return;
        }
        
        ProcessDepth(image);
        image.Dispose();
    }
    
    void ProcessDepth(XRCpuImage cpuImage)
    {
        var conversionParams = new XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
            outputDimensions = new Vector2Int(inferenceWidth, inferenceHeight),
            outputFormat = TextureFormat.RGB24,
            transformation = XRCpuImage.Transformation.MirrorY
        };
        
        Texture2D inputTexture = new Texture2D(inferenceWidth, inferenceHeight, TextureFormat.RGB24, false);
        var rawData = inputTexture.GetRawTextureData<byte>();
        cpuImage.Convert(conversionParams, rawData);
        inputTexture.Apply();
        
        using (Unity.InferenceEngine.Tensor<float> inputTensor = Unity.InferenceEngine.TextureConverter.ToTensor(inputTexture, inferenceWidth, inferenceHeight, 3))
        {
            worker.Schedule(inputTensor);
            Unity.InferenceEngine.Tensor<float> outputTensor = worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>;
            outputTensor.ReadbackAndClone();
            UpdateDepthTexture(outputTensor);
        }
        
        Destroy(inputTexture);
    }
    
void UpdateDepthTexture(Unity.InferenceEngine.Tensor<float> depthOutput)
{
    var depthData = depthOutput.DownloadToArray();
    
    float minDepth = float.MaxValue;
    float maxDepth = float.MinValue;
    
    for (int i = 0; i < depthData.Length; i++)
    {
        float depth = depthData[i];
        if (depth < minDepth) minDepth = depth;
        if (depth > maxDepth) maxDepth = depth;
    }
    
    Color[] pixels = new Color[inferenceWidth * inferenceHeight];
    
    // Rotate 90 degrees counter-clockwise AND flip vertically
    for (int y = 0; y < inferenceHeight; y++)
    {
        for (int x = 0; x < inferenceWidth; x++)
        {
            int srcIndex = y * inferenceWidth + x;
            float normalizedDepth = (depthData[srcIndex] - minDepth) / (maxDepth - minDepth);
            
            // Rotate 90 degrees counter-clockwise
            int rotatedX = y;
            int rotatedY = inferenceWidth - 1 - x;
            
            // Then flip vertically
            int finalX = rotatedX;
            int finalY = inferenceHeight - 1 - rotatedY;
            
            int dstIndex = finalY * inferenceHeight + finalX;
            
            pixels[dstIndex] = new Color(normalizedDepth, normalizedDepth, normalizedDepth, 1f);
        }
    }
    
    depthTexture.SetPixels(pixels);
    depthTexture.Apply();
    
    Debug.Log($"Depth range: {minDepth:F2} to {maxDepth:F2}");
}
    
    void OnDestroy()
    {
        worker?.Dispose();
        if (depthTexture != null) Destroy(depthTexture);
    }
}