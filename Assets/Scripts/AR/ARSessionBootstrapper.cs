using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// ARSessionBootstrapper.cs
///
/// Wires up the AR session at startup:
///   - Loads the demo pick list into PickListManager
///   - Provides a simple UI for the worker to type a barcode/SKU to search for
///   - Bridges the UI search input → PickListManager's target code filter
/// </summary>
public class ARSessionBootstrapper : MonoBehaviour
{
    [Header("Feature Scripts")]
    public PickListManager pickListManager;
    public BarcodeDetector barcodeDetector;

    [Header("Search UI")]
    [Tooltip("InputField where the worker types the SKU or barcode they want to find.")]
    public TMP_InputField searchInputField;
    public Button searchButton;
    public TMP_Text statusText;

    // The single barcode ID the worker is currently hunting for.
    // AnchorManager already reads from PickListManager.IsTargetCode(),
    // so we just need to load a single-item list here.
    private string _activeTarget = "";

    void Start()
    {
        if (pickListManager == null)
        {
            Debug.LogError("[ARSessionBootstrapper] PickListManager not assigned!");
            return;
        }

        // Load demo data so the app works without a real WMS endpoint.
        pickListManager.LoadDemoPickList();
        Debug.Log("[ARSessionBootstrapper] Demo pick list loaded.");

        // Wire up the search button.
        if (searchButton != null)
            searchButton.onClick.AddListener(OnSearchPressed);

        // Also search on pressing Enter in the input field.
        if (searchInputField != null)
            searchInputField.onSubmit.AddListener(_ => OnSearchPressed());

        UpdateStatusText("Enter a SKU or scan a barcode to begin.");
    }

    private void OnSearchPressed()
    {
        if (searchInputField == null) return;

        string query = searchInputField.text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            UpdateStatusText("Please enter a SKU or barcode ID.");
            return;
        }

        // Check if the queried item is actually on the demo pick list.
        if (!pickListManager.IsTargetCode(query))
        {
            UpdateStatusText($"'{query}' not found on pick list.\nTry: SKU-4821, SKU-0042, QR-9901");
            return;
        }

        // Narrow the pick list to just this one item so AnchorManager
        // only highlights the box the worker is looking for.
        var item = pickListManager.GetPickItem(query);
        pickListManager.SetSingleTarget(query);
        pickListManager.CurrentPickList.Add(item);
        // Re-expose the method we need — see note below.

        _activeTarget = query;
        UpdateStatusText($"Searching for: {item.description}\nSKU: {item.barcodeId}  |  BIN: {item.bin}");

        Debug.Log($"[ARSessionBootstrapper] Target set to: {query}");

        // Hide the search UI so it doesn't cover the camera view.
        if (searchInputField != null) searchInputField.gameObject.SetActive(false);
        if (searchButton != null) searchButton.gameObject.SetActive(false);
    }

    private void UpdateStatusText(string msg)
    {
        if (statusText != null) statusText.text = msg;
        Debug.Log($"[ARSessionBootstrapper] {msg}");
    }
}