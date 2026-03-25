using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;

/// <summary>
/// AnchorManager.cs
///
/// Receives detected barcode data (ID + pixel corners) from BarcodeDetector,
/// checks the pick list, then converts the 2-D pixel bounding box into a
/// 3-D world-space pose using ARCore's hit-testing and the camera's
/// intrinsic projection.
///
/// For each matched code it spawns (or repositions) a BoxHighlight prefab
/// anchored to the estimated box face plane.
///
/// Pipeline per detection event:
///   pixel corners → NDC → ray cast → ARPlane hit / depth estimate
///   → world-space quad → ARAnchor → BoxHighlight prefab
///
/// Setup:
///   1. Add this component to AR Session Origin.
///   2. Assign arCamera, raycastManager, anchorManager, barcodeDetector,
///      pickListManager, and boxHighlightPrefab in the Inspector.
/// </summary>
[RequireComponent(typeof(ARRaycastManager))]
public class AnchorManager : MonoBehaviour
{
    [Header("AR Components")]
    public Camera          arCamera;
    public ARRaycastManager   raycastManager;
    public ARAnchorManager    anchorManager;

    [Header("Feature Scripts")]
    public BarcodeDetector  barcodeDetector;
    public PickListManager  pickListManager;

    [Header("Highlight Prefab")]
    [Tooltip("Prefab with a BoxHighlight component. Spawned once per unique barcode ID.")]
    public GameObject boxHighlightPrefab;

    [Header("Anchor Settings")]
    [Tooltip("Seconds before a highlight disappears if the code leaves the camera view.")]
    public float highlightTimeoutSeconds = 3f;

    [Tooltip("Fall-back world-space distance from camera when no plane is hit.")]
    public float defaultDepthMeters = 1.0f;

    // ── Private state ─────────────────────────────────────────────────────────
    // Maps barcodeId → active highlight instance, so we reuse rather than respawn.
    private readonly Dictionary<string, ActiveHighlight> _activeHighlights = new();
    private readonly List<ARRaycastHit> _hitResults = new();

    // ─────────────────────────────────────────────────────────────────────────
    void OnEnable()  => barcodeDetector.OnCodeDetected += HandleCodeDetected;
    void OnDisable() => barcodeDetector.OnCodeDetected -= HandleCodeDetected;

    void Update() => ExpireStaleHighlights();

    // ─────────────────────────────────────────────────────────────────────────
    // Called on the main thread by BarcodeDetector when a code is decoded.
    // corners[] are in full-resolution camera pixel space.
    // ─────────────────────────────────────────────────────────────────────────
    private async void HandleCodeDetected(string codeId, ResultPoint[] corners)
    {
        // ── Pick list gate ────────────────────────────────────────────────────
        if (!pickListManager.IsTargetCode(codeId))
        {
            // Not on this worker's list — silently ignore.
            return;
        }

        PickItem item = pickListManager.GetPickItem(codeId);

        // ── Compute pixel centroid of the 4 code corners ──────────────────────
        // ZXing gives us the corners of the barcode symbol itself.
        // We use the centroid as the ray origin into the scene.
        Vector2 pixelCentroid = ComputeCentroid(corners);

        // ── Pixel → NDC → World ray ───────────────────────────────────────────
        // Convert pixel coords (origin top-left) to Unity viewport coords
        // (origin bottom-left, range 0..1).
        float vpX = pixelCentroid.x / Screen.width;
        float vpY = 1f - (pixelCentroid.y / Screen.height); // flip Y

        Ray ray = arCamera.ViewportPointToRay(new Vector3(vpX, vpY, 0f));

        // ── Hit test: try ARCore plane hit first ──────────────────────────────
        // ARCore's plane detection gives us the most accurate surface normal,
        // which is what we need to orient the highlight quad correctly.
        Pose hitPose;
        bool hitFound = raycastManager.Raycast(
            new Vector2(vpX * Screen.width, vpY * Screen.height),
            _hitResults,
            TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds
        );

        if (hitFound && _hitResults.Count > 0)
        {
            hitPose = _hitResults[0].pose;
        }
        else
        {
            // Fallback: project the ray to a fixed depth in front of the camera.
            // This works well when the box face hasn't been plane-detected yet
            // (e.g. on first approach to a new shelf).
            Vector3 worldPos = ray.GetPoint(defaultDepthMeters);

            // Orient the quad to face the camera — reasonable for a box face.
            Quaternion facingCamera = Quaternion.LookRotation(
                arCamera.transform.position - worldPos,
                Vector3.up
            );
            hitPose = new Pose(worldPos, facingCamera);
        }

        // ── Estimate highlight size from barcode corner spread ─────────────────
        // The pixel spread of the code corners gives us a rough scale.
        // We convert pixel width → angular size → metric size at the hit depth.
        float pixelSpread   = ComputePixelSpread(corners);
        float depthToTarget = Vector3.Distance(arCamera.transform.position, hitPose.position);
        float metricSize    = PixelSpreadToMetricSize(pixelSpread, depthToTarget);

        // Clamp to sane warehouse box sizes (10 cm – 120 cm).
        metricSize = Mathf.Clamp(metricSize, 0.10f, 1.20f);

        // ── Spawn or update the highlight ─────────────────────────────────────
        await SpawnOrUpdateHighlightAsync(codeId, item, hitPose, metricSize);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Creates a new highlight or repositions the existing one for this codeId.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task SpawnOrUpdateHighlightAsync(string codeId, PickItem item,
                                         Pose pose, float size)
    {
        if (!_activeHighlights.TryGetValue(codeId, out var active))
        {
            // First detection of this code — attach a real ARCore anchor so
            // the highlight stays glued to the physical surface as the device moves.
            // First detection of this code — attach a real ARCore anchor
            ARAnchor anchor = null;
            var anchorResult = await anchorManager.TryAddAnchorAsync(pose);
            if (anchorResult.status.IsSuccess())
                anchor = anchorResult.value;

            GameObject go = Instantiate(
                boxHighlightPrefab,
                pose.position,
                pose.rotation,
                anchor != null ? anchor.transform : null
            );

            var highlight = go.GetComponent<BoxHighlight>();
            highlight.Initialise(item, size);

            active = new ActiveHighlight
            {
                go          = go,
                highlight   = highlight,
                anchor      = anchor,
                lastSeenTime = Time.time
            };
            _activeHighlights[codeId] = active;
        }
        else
        {
            // Already exists — smoothly lerp to the newly measured pose.
            // This handles jitter from frame-to-frame decode variance.
            active.go.transform.position = Vector3.Lerp(
                active.go.transform.position, pose.position, Time.deltaTime * 10f);
            active.go.transform.rotation = Quaternion.Slerp(
                active.go.transform.rotation, pose.rotation, Time.deltaTime * 10f);

            active.highlight.UpdateSize(size);
            active.lastSeenTime = Time.time;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Remove highlights whose barcode has not been seen for N seconds.
    // Called every Update().
    // ─────────────────────────────────────────────────────────────────────────
    private void ExpireStaleHighlights()
    {
        var toRemove = new List<string>();

        foreach (var kv in _activeHighlights)
        {
            if (Time.time - kv.Value.lastSeenTime > highlightTimeoutSeconds)
                toRemove.Add(kv.Key);
        }

        foreach (var id in toRemove)
        {
            var active = _activeHighlights[id];
            if (active.anchor != null)
                Destroy(active.anchor.gameObject);
            Destroy(active.go);
            _activeHighlights.Remove(id);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Geometry helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// Returns the average pixel position of the 4 code corners.
    private static Vector2 ComputeCentroid(ResultPoint[] corners)
    {
        float x = 0, y = 0;
        foreach (var c in corners) { x += c.X; y += c.Y; }
        return new Vector2(x / corners.Length, y / corners.Length);
    }

    /// Returns the diagonal pixel distance across the bounding box.
    private static float ComputePixelSpread(ResultPoint[] corners)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            if (c.X < minX) minX = c.X;
            if (c.X > maxX) maxX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.Y > maxY) maxY = c.Y;
        }
        return Mathf.Max(maxX - minX, maxY - minY);
    }

    /// Converts a pixel measurement to a real-world metric size using the
    /// camera's vertical field-of-view and the known distance to the target.
    private float PixelSpreadToMetricSize(float pixelSpread, float depth)
    {
        // Angular size of one pixel (radians)
        float fovRad         = arCamera.fieldOfView * Mathf.Deg2Rad;
        float pixelsPerRad   = Screen.height / fovRad;
        float angularSizeRad = pixelSpread / pixelsPerRad;

        // metric size = 2 * depth * tan(angle/2)
        return 2f * depth * Mathf.Tan(angularSizeRad * 0.5f);
    }

    // ── Internal record ───────────────────────────────────────────────────────
    private class ActiveHighlight
    {
        public GameObject  go;
        public BoxHighlight highlight;
        public ARAnchor    anchor;
        public float       lastSeenTime;
    }
}
