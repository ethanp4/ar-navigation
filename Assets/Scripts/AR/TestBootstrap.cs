using UnityEngine;

public class TestBootstrap : MonoBehaviour
{
    public PickListManager pickListManager;

    void Awake()
    {
        Debug.Log("[TestBootstrap] Awake called!");
        pickListManager.LoadDemoPickList();
        Debug.Log("[TestBootstrap] Demo pick list loaded.");
    }
}
