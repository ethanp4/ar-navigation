using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// PickListManager.cs
///
/// Maintains the worker's active pick order in memory and exposes
/// a fast lookup: IsTargetCode(string id) → bool.
///
/// Pick orders are fetched from your WMS/ERP REST endpoint and cached
/// locally. A lightweight polling loop keeps the list fresh without
/// hammering the server.
///
/// Setup:
///   1. Add this component to a persistent GameObject (e.g. AR Session Origin).
///   2. Set wmsBaseUrl and workerId in the Inspector (or inject at runtime).
///   3. Call IsTargetCode() from AnchorManager whenever a code is detected.
/// </summary>
public class PickListManager : MonoBehaviour
{
    [Header("WMS Connection")]
    [Tooltip("Base URL of your WMS REST API, no trailing slash.")]
    public string wmsBaseUrl = "https://your-wms-api.example.com";

    [Tooltip("Worker ID — set at login time via SetWorkerId().")]
    public string workerId = "worker-001";

    [Header("Refresh Settings")]
    [Tooltip("How often (seconds) to re-fetch the pick list from WMS.")]
    public float refreshIntervalSeconds = 15f;

    // ── Events ────────────────────────────────────────────────────────────────
    // Fires whenever the pick list changes (new order loaded, order completed).
    public event Action<List<PickItem>> OnPickListUpdated;

    // ── Public state ─────────────────────────────────────────────────────────
    public List<PickItem> CurrentPickList { get; private set; } = new();

    // Fast O(1) lookup set — rebuilt whenever CurrentPickList changes.
    private readonly HashSet<string> _targetCodes = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        // Don't poll WMS until a real URL is set
        if (wmsBaseUrl.Contains("your-wms-api"))
        {
            Debug.Log("[PickListManager] Demo mode — skipping WMS fetch.");
            return;
        }
        StartCoroutine(RefreshLoop());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Primary query: called by AnchorManager on every detected code.
    // Returns true if the scanned code is on this worker's current pick list.
    // ─────────────────────────────────────────────────────────────────────────
    public bool IsTargetCode(string scannedId) => _targetCodes.Contains(scannedId);

    // Returns the full PickItem for richer overlay label data (SKU, qty, etc.)
    public PickItem GetPickItem(string scannedId)
    {
        return CurrentPickList.Find(p =>
            string.Equals(p.barcodeId, scannedId, StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Call this when the worker logs in or is reassigned.
    // ─────────────────────────────────────────────────────────────────────────
    public void SetWorkerId(string id)
    {
        workerId = id;
        StopAllCoroutines();
        StartCoroutine(RefreshLoop());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Call this when the worker confirms they've picked an item.
    // Removes it from the local list immediately (WMS sync happens async).
    // ─────────────────────────────────────────────────────────────────────────
    public void MarkPicked(string barcodeId)
    {
        CurrentPickList.RemoveAll(p =>
            string.Equals(p.barcodeId, barcodeId, StringComparison.OrdinalIgnoreCase));

        RebuildLookup();
        OnPickListUpdated?.Invoke(CurrentPickList);

        // Notify WMS asynchronously — fire-and-forget.
        StartCoroutine(PostPickConfirmation(barcodeId));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Polling loop: fetches the pick list every refreshIntervalSeconds.
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator RefreshLoop()
    {
        while (true)
        {
            yield return FetchPickList();
            yield return new WaitForSeconds(refreshIntervalSeconds);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /pick-orders/{workerId}/active  →  PickOrderResponse JSON
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator FetchPickList()
    {
        string url = $"{wmsBaseUrl}/pick-orders/{workerId}/active";

        using var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("Accept", "application/json");
        // Add your auth header here, e.g. Bearer token from a SessionManager.
        // req.SetRequestHeader("Authorization", $"Bearer {SessionManager.Token}");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[PickListManager] WMS fetch failed: {req.error}");
            yield break;
        }

        // Deserialize — Unity's JsonUtility works fine for flat structures.
        // Swap in Newtonsoft.Json if your payload uses polymorphism or nullables.
        var response = JsonUtility.FromJson<PickOrderResponse>(req.downloadHandler.text);

        if (response?.items == null) yield break;

        CurrentPickList = response.items;
        RebuildLookup();
        OnPickListUpdated?.Invoke(CurrentPickList);

        Debug.Log($"[PickListManager] Loaded {CurrentPickList.Count} pick items for {workerId}.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /pick-orders/{workerId}/confirm  — tells WMS an item was picked.
    // ─────────────────────────────────────────────────────────────────────────
    private IEnumerator PostPickConfirmation(string barcodeId)
    {
        string url  = $"{wmsBaseUrl}/pick-orders/{workerId}/confirm";
        string body = JsonUtility.ToJson(new PickConfirmPayload { barcodeId = barcodeId });

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogWarning($"[PickListManager] Confirm POST failed: {req.error}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rebuilds the O(1) hash set from the current list.
    // ─────────────────────────────────────────────────────────────────────────
    private void RebuildLookup()
    {
        _targetCodes.Clear();
        foreach (var item in CurrentPickList)
            _targetCodes.Add(item.barcodeId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OFFLINE / DEMO MODE
    // Call this to inject a hardcoded list without a WMS connection.
    // ─────────────────────────────────────────────────────────────────────────
    public void LoadDemoPickList()
    {
        CurrentPickList = new List<PickItem>
        {
            new() { barcodeId = "SKU-4821", description = "Widget A",  quantity = 3, bin = "A-12-3" },
            new() { barcodeId = "SKU-0042", description = "Bracket B", quantity = 1, bin = "C-07-1" },
            new() { barcodeId = "QR-9901",  description = "Motor C",   quantity = 2, bin = "B-03-5" },
        };
        RebuildLookup();
        OnPickListUpdated?.Invoke(CurrentPickList);
    }
}

// ── Data models ───────────────────────────────────────────────────────────────

[Serializable]
public class PickItem
{
    public string barcodeId;    // Matches what ZXing decodes off the physical label
    public string description;  // Human-readable name shown in the AR label
    public int    quantity;     // Units to pick
    public string bin;          // Shelf / bin location code
}

[Serializable]
public class PickOrderResponse
{
    public string         orderId;
    public List<PickItem> items;
}

[Serializable]
public class PickConfirmPayload
{
    public string barcodeId;
}
