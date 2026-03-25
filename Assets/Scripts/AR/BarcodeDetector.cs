using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;
using ZXing.Common;


/// <summary>
/// BarcodeDetector.cs
/// 
/// Runs on a background thread every N frames, sampling the ARCamera's
/// CPU image to detect barcodes and QR codes via ZXing.Net.
///
/// Outputs: decoded ID string + pixel-space bounding quad (4 corners)
/// to any listener subscribed to OnCodeDetected.
///
/// Setup:
///   1. Add this component to your AR Session Origin GameObject.
///   2. Assign the ARCameraManager in the Inspector.
///   3. Import ZXing.Net via NuGet or the Unity Package (com.zxing.net).
/// </summary>
public class BarcodeDetector : MonoBehaviour
{
    [Header("ARFoundation")]
    [Tooltip("Drag in the ARCameraManager component from your AR Camera.")]
    public ARCameraManager cameraManager;

    [Header("Detection Settings")]
    [Tooltip("Only run detection every N frames to save CPU budget.")]
    public int detectEveryNFrames = 3;

    [Tooltip("Scale down the image before decode (0.5 = half res). Faster but may miss small codes.")]
    [Range(0.25f, 1.0f)]
    public float decodeResolutionScale = 0.5f;

    // ── Public event ─────────────────────────────────────────────────────────
    // Fires on the main thread when a code is successfully decoded.
    // ResultPoint[] contains the 4 corner positions in pixel space.
    public event Action<string, ResultPoint[]> OnCodeDetected;

    // ── Private state ─────────────────────────────────────────────────────────
    private BarcodeReader<RGBLuminanceSource> _reader;
    private int _frameCounter;
    private bool _processingFrame;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        if (cameraManager == null)
            Debug.LogError("[BarcodeDetector] ARCameraManager is NOT assigned!");
        else
            Debug.Log("[BarcodeDetector] ARCameraManager assigned OK - ready to decode.");

        // Explicitly enable CPU image access — some ARFoundation setups have this disabled by default to save resources.
        if (cameraManager != null)
        {
            cameraManager.enabled = true;
            Debug.Log("[BarcodeDetector] ARCameraManager enabled and ready for CPU image access.");
        }
    }


    void Awake()
    {
        var options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = new[]
            {
            BarcodeFormat.QR_CODE,
            BarcodeFormat.CODE_128,
            BarcodeFormat.CODE_39,
            BarcodeFormat.EAN_13,
            BarcodeFormat.DATA_MATRIX
        }
        };

        _reader = new BarcodeReader<RGBLuminanceSource>(
            luminanceSource => luminanceSource
        );
        _reader.AutoRotate = false;
        options.TryInverted = true;
        _reader.Options = options;
    }

    void OnEnable()
    {
        cameraManager.frameReceived += OnCameraFrameReceived;
        cameraManager.requestedFacingDirection = CameraFacingDirection.World;
    }

    void OnDisable()
    {
        cameraManager.frameReceived -= OnCameraFrameReceived;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Called by ARFoundation every frame a new camera image is available.
    // ─────────────────────────────────────────────────────────────────────────
    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        Debug.Log("[BarcodeDetector] Frame received!");

        if (++_frameCounter % detectEveryNFrames != 0) return;
        if (_processingFrame) return;

        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            Debug.LogWarning("[BarcodeDetector] Could not acquire CPU image.");
            return;
        }

        _processingFrame = true;
        StartCoroutine(DecodeAsync(cpuImage));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Converts the raw YUV camera image to a grayscale byte array,
    // then runs the ZXing decode pass off the main thread.
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator DecodeAsync(XRCpuImage cpuImage)
    {
        // Scale dimensions for the decode resolution budget.
        int decodeW = Mathf.RoundToInt(cpuImage.width  * decodeResolutionScale);
        int decodeH = Mathf.RoundToInt(cpuImage.height * decodeResolutionScale);

        // Request async conversion to RGBA32 — ARFoundation handles YUV → RGB.
        var conversionParams = new XRCpuImage.ConversionParams
        {
            inputRect        = new RectInt(0, 0, cpuImage.width, cpuImage.height),
            outputDimensions = new Vector2Int(decodeW, decodeH),
            outputFormat     = TextureFormat.RGBA32,
            transformation   = XRCpuImage.Transformation.MirrorY  // Unity camera is flipped
        };

        var asyncConversion = cpuImage.ConvertAsync(conversionParams);
        cpuImage.Dispose(); // Release the raw image immediately

        // Wait until the conversion job finishes (usually 1-2 frames).
        yield return new WaitUntil(() =>
            asyncConversion.status == XRCpuImage.AsyncConversionStatus.Ready);

        if (asyncConversion.status != XRCpuImage.AsyncConversionStatus.Ready)
        {
            asyncConversion.Dispose();
            _processingFrame = false;
            yield break;
        }

        // Copy pixel data out before disposing the conversion result.
        var rawBytes = asyncConversion.GetData<byte>().ToArray();
        asyncConversion.Dispose();

        // Convert RGBA → grayscale luminance array for ZXing.
        // ZXing's RGBLuminanceSource does this internally but we do it
        // explicitly to avoid an extra allocation of a Color32 array.
        byte[] luminance = RGBAToLuminance(rawBytes, decodeW, decodeH);

        // Run ZXing decode on a background thread to keep the main thread free.
        string  decodedText   = null;
        ResultPoint[] corners = null;

        bool decodeAttempted = false;

        yield return new WaitForBackgroundThread(() =>
        {
            decodeAttempted = true;
            var result = _reader.Decode(luminance, decodeW, decodeH,
                                        RGBLuminanceSource.BitmapFormat.Gray8);
            if (result != null)
            {
                decodedText = result.Text;
                corners = result.ResultPoints;

                float scaleInv = 1f / decodeResolutionScale;
                for (int i = 0; i < corners.Length; i++)
                    corners[i] = new ResultPoint(corners[i].X * scaleInv,
                                                  corners[i].Y * scaleInv);
            }
        });

        // Back on main thread — now we can log
        Debug.Log($"[BarcodeDetector] Decode attempted: {decodeAttempted}, result: {decodedText ?? "null"}");

        // Back on the main thread — fire the event if something was found.
        if (decodedText != null && corners != null && corners.Length >= 2)
        {
            Debug.Log($"[BarcodeDetector] Firing OnCodeDetected for {decodedText}, corners: {corners.Length}");
            OnCodeDetected?.Invoke(decodedText, corners);
        }
        else
        {
            Debug.LogWarning($"[BarcodeDetector] Decode succeeded ({decodedText}) but corners invalid: {corners?.Length ?? 0} points.");
        }

        _processingFrame = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Converts a flat RGBA byte array to 8-bit grayscale luminance.
    // ─────────────────────────────────────────────────────────────────────────
    private static byte[] RGBAToLuminance(byte[] rgba, int width, int height)
    {
        byte[] lum = new byte[width * height];
        for (int i = 0; i < lum.Length; i++)
        {
            int b = i * 4;
            // Standard luma coefficients (BT.601)
            lum[i] = (byte)(0.299f * rgba[b] + 0.587f * rgba[b + 1] + 0.114f * rgba[b + 2]);
        }
        return lum;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Minimal helper: yield into a background thread, then resume on main thread.
// Unity doesn't have a built-in for this; this keeps the coroutine pattern clean.
// ─────────────────────────────────────────────────────────────────────────────
public class WaitForBackgroundThread : CustomYieldInstruction
{
    private bool _isDone;
    public override bool keepWaiting => !_isDone;

    public WaitForBackgroundThread(Action work)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try   { work(); }
            finally { _isDone = true; }
        });
    }
}
