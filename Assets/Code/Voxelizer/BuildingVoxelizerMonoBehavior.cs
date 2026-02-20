using Unity.Burst.CompilerServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

public class BuildingVoxelizerMonoBehavior : MonoBehaviour
{
    public GameObject model;

    private float3 physBoundBoxCenter;
    private float3 physBoundBoxSize;

    private int3 gridSize;

    void Start()
    {
        if (model == null)
        {
            Debug.LogError("The building GO is not determined.");
        }
        else
        {
            Renderer renderer = model.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                Debug.LogError("The building GO does not have a Renderer component.");
                return;
            }

            physBoundBoxCenter = renderer.bounds.center;
            physBoundBoxSize = renderer.bounds.size;

            Debug.Log("Center: " + physBoundBoxCenter);
            Debug.Log("Size: " + physBoundBoxSize);

            gridSize = (int3)physBoundBoxSize;

            VoxelInsideMeshDetect();
        }
    }

    // RayCasting in Unity: 1. Edit -> Project Settings -> Physics Settings -> Game Object -> Queries Hit Backfaces: On.
    // Otherwise the ray cannot hit from inside the mesh. 2. Func "RaycastAll" can only hit one collider once. So we need
    // to loop to continue casting rays.
    void VoxelInsideMeshDetect()
    {
        int numCellsInside = 0;
        int numCellsOutside = 0;

        float dx = 0.2f;

        gridSize = new int3(200, 100, 200);

        for (int z = 0; z < gridSize.z; z += 1)
            for (int y = 0; y < gridSize.y; y += 1)
                for (int x = 1; x < gridSize.x; x += 1)
                {
                    int intersectCount = 0;

                    float3 offset = new float3(x + 0.1f, y + 0.1f, z + 0.1f);
                    float3 physPos = physBoundBoxCenter - physBoundBoxSize / 2f + offset * dx;
                    float3 direct = math.normalize(physBoundBoxCenter - physPos);
                    if (math.length(direct) < 0.01f)
                        direct += new float3(1.0f, 1.0f, 1.0f);

                    Ray ray = new Ray(physPos, direct);
                    RaycastHit[] hits = Physics.RaycastAll(ray);

                    while (hits.Length > 0)
                    {
                        intersectCount++;
                        ray = new Ray((float3)hits[0].point + direct / 10.0f, direct);
                        hits = Physics.RaycastAll(ray);
                    }

                    if (intersectCount % 2 == 0)
                    {
                        numCellsOutside++;
                    }
                    else
                    {
                        numCellsInside++;

                        GameObject voxelInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        voxelInstance.transform.position = physPos;
                        //voxelInstance.transform.localScale = new Vector3(1, 1, 1);
                        voxelInstance.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                        voxelInstance.GetComponent<BoxCollider>().enabled = false;
                        voxelInstance.GetComponent<Renderer>().material.color = new Color(0.0f, 0.0f, 1.0f);
                    }
                }
        Debug.Log("Number of cells inside the mesh: " + numCellsInside);
        Debug.Log("Number of cells outside the mesh: " + numCellsOutside);
    }
}
