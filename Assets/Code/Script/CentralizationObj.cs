using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR

public class CentralizationObj : MonoBehaviour
{
    [ContextMenu("Centralize the current object")]
    void MoveCenterToOrigin()
    {
        var renderers = GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0) return;

        //transform.Rotate(Vector3.right, -90f, Space.Self);

        Bounds bounds = renderers[0].bounds;
        foreach (var r in renderers)
        {
            bounds.Encapsulate(r.bounds);
        }

        Vector3 offset = bounds.center;

        foreach (Transform child in transform)
        {
            child.position -= offset;
            //child.position += Vector3.up * 50.0f;
        }

        Debug.Log("Object centralized.");
    }
}

#endif