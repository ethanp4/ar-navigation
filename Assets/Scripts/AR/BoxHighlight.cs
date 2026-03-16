using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// BoxHighlight.cs
///
/// Renders the visual highlight on a detected pick-target box:
///   • An animated outline quad that pulses to draw attention
///   • A world-space label showing SKU, description, quantity, and bin
///   • A subtle filled tint so the box face is clearly bounded
///
/// This component lives on the boxHighlightPrefab instantiated by AnchorManager.
///
/// Prefab setup (create this in the Unity Editor):
///   BoxHighlight (GameObject)
///   ├── OutlineQuad       — MeshRenderer using Highlight_Outline material
///   ├── FillQuad          — MeshRenderer using Highlight_Fill material
///   └── LabelCanvas       — World Space Canvas
///       └── LabelPanel    — Background Image
///           ├── SkuText        — TextMeshProUGUI
///           ├── DescText       — TextMeshProUGUI
///           └── BinText        — TextMeshProUGUI
///
/// Materials (create in Unity):
///   Highlight_Outline  — Unlit/Transparent, ZWrite Off, source sprite = 1px border texture
///   Highlight_Fill     — Unlit/Transparent, ZWrite Off, solid color with low alpha
///
/// The Shader Graph / URP equivalent uses a simple Unlit graph with
/// _Color and _Alpha properties so we can tint from script.
/// </summary>
public class BoxHighlight : MonoBehaviour
{
    // ── Inspector refs ────────────────────────────────────────────────────────
    [Header("Visual Elements")]
    public MeshRenderer outlineQuad;
    public MeshRenderer fillQuad;

    [Header("Label")]
    public Canvas       labelCanvas;
    public TextMeshProUGUI skuText;
    public TextMeshProUGUI descText;
    public TextMeshProUGUI binText;

    [Header("Highlight Colours")]
    [Tooltip("Outline and label colour for a target-match (worker needs this item).")]
    public Color targetColor    = new Color(0.10f, 0.90f, 0.40f, 1.00f); // bright green
    [Tooltip("Fill alpha — keep low so the box contents are still visible.")]
    [Range(0f, 0.4f)]
    public float fillAlpha      = 0.12f;

    [Header("Animation")]
    [Tooltip("Outline pulses between 1.0 and this minimum opacity.")]
    [Range(0.2f, 1.0f)]
    public float pulseMinAlpha  = 0.45f;
    public float pulseSpeed     = 2.2f;

    [Tooltip("Label floats this many metres above the highlight centre.")]
    public float labelOffsetY   = 0.18f;

    // ── Private state ─────────────────────────────────────────────────────────
    private MaterialPropertyBlock _outlineMpb;
    private MaterialPropertyBlock _fillMpb;
    private float  _currentSize;
    private float  _targetSize;
    private Camera _arCamera;

    // Cached shader property IDs — faster than string look-ups every frame.
    private static readonly int PropColor = Shader.PropertyToID("_Color");

    // ─────────────────────────────────────────────────────────────────────────
    // Called by AnchorManager immediately after Instantiate.
    // ─────────────────────────────────────────────────────────────────────────
    public void Initialise(PickItem item, float sizeMeters)
    {
        _arCamera    = Camera.main;
        _outlineMpb  = new MaterialPropertyBlock();
        _fillMpb     = new MaterialPropertyBlock();
        _targetSize  = sizeMeters;
        _currentSize = sizeMeters * 0.01f; // start tiny → animate in

        // ── Populate label text ───────────────────────────────────────────────
        if (skuText  != null) skuText.text  = item.barcodeId;
        if (descText != null) descText.text  = $"{item.description}  ×{item.quantity}";
        if (binText  != null) binText.text   = $"BIN {item.bin}";

        // ── Apply material colours ────────────────────────────────────────────
        SetOutlineAlpha(1f);
        SetFillColor(targetColor, fillAlpha);

        // Label canvas always faces the camera; billboarding is handled in Update.
        labelCanvas.gameObject.SetActive(true);

        // Animate the highlight scaling in from a pinpoint.
        StartCoroutine(AnimateIn());

        // Start the outline pulse loop.
        StartCoroutine(PulseOutline());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Called by AnchorManager each frame the barcode is still visible.
    // Smoothly updates the highlight size as depth / angle changes.
    // ─────────────────────────────────────────────────────────────────────────
    public void UpdateSize(float newSizeMeters)
    {
        _targetSize = newSizeMeters;
    }

    // ─────────────────────────────────────────────────────────────────────────
    void Update()
    {
        // Smoothly track the target size (handles jitter without snapping).
        _currentSize = Mathf.Lerp(_currentSize, _targetSize, Time.deltaTime * 8f);
        transform.localScale = Vector3.one * _currentSize;

        // Billboard: rotate the label to always face the AR camera.
        if (_arCamera != null && labelCanvas != null)
        {
            Vector3 toCamera = _arCamera.transform.position - labelCanvas.transform.position;
            toCamera.y = 0f; // keep label upright — don't tilt with camera pitch
            if (toCamera.sqrMagnitude > 0.001f)
                labelCanvas.transform.rotation = Quaternion.LookRotation(-toCamera, Vector3.up);

            // Keep label floating above the highlight centre in world space.
            labelCanvas.transform.position = transform.position
                + Vector3.up * (labelOffsetY + _currentSize * 0.5f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scale-in animation: highlight expands from zero → target size.
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator AnimateIn()
    {
        float elapsed  = 0f;
        float duration = 0.25f;

        while (elapsed < duration)
        {
            elapsed      += Time.deltaTime;
            float t       = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            _currentSize  = Mathf.Lerp(0f, _targetSize, t);
            yield return null;
        }
        _currentSize = _targetSize;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Outline pulse: alpha oscillates between pulseMinAlpha and 1.0
    // to create a "breathing" attention effect.
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator PulseOutline()
    {
        while (true)
        {
            float alpha = Mathf.Lerp(pulseMinAlpha, 1f,
                            (Mathf.Sin(Time.time * pulseSpeed) + 1f) * 0.5f);
            SetOutlineAlpha(alpha);
            yield return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MaterialPropertyBlock helpers — avoid creating new material instances.
    // ─────────────────────────────────────────────────────────────────────────
    private void SetOutlineAlpha(float alpha)
    {
        if (outlineQuad == null) return;
        outlineQuad.GetPropertyBlock(_outlineMpb);
        _outlineMpb.SetColor(PropColor, new Color(
            targetColor.r, targetColor.g, targetColor.b, alpha));
        outlineQuad.SetPropertyBlock(_outlineMpb);
    }

    private void SetFillColor(Color color, float alpha)
    {
        if (fillQuad == null) return;
        fillQuad.GetPropertyBlock(_fillMpb);
        _fillMpb.SetColor(PropColor, new Color(color.r, color.g, color.b, alpha));
        fillQuad.SetPropertyBlock(_fillMpb);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Called by PickListManager (via AnchorManager) when the worker confirms
    // they have picked this item. Plays a brief flash then destroys itself.
    // ─────────────────────────────────────────────────────────────────────────
    public void PlayPickedAnimation(System.Action onComplete = null)
    {
        StopAllCoroutines();
        StartCoroutine(PickedFlash(onComplete));
    }

    private IEnumerator PickedFlash(System.Action onComplete)
    {
        // Three quick white flashes.
        Color flashColor = Color.white;
        for (int i = 0; i < 3; i++)
        {
            SetFillColor(flashColor, 0.6f);
            SetOutlineAlpha(1f);
            yield return new WaitForSeconds(0.08f);
            SetFillColor(targetColor, fillAlpha);
            SetOutlineAlpha(0.1f);
            yield return new WaitForSeconds(0.08f);
        }

        // Shrink to zero then notify caller to destroy this object.
        float elapsed = 0f, duration = 0.2f;
        float startSize = _currentSize;
        while (elapsed < duration)
        {
            elapsed     += Time.deltaTime;
            _currentSize = Mathf.Lerp(startSize, 0f, elapsed / duration);
            yield return null;
        }

        onComplete?.Invoke();
    }
}
