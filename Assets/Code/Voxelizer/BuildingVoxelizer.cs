using CesiumForUnity;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using static UnityEngine.Rendering.STP;

public class Voxelizer
{
    FluidSimConfig cf;
    RuntimeConfig rcf;

    int[] flags; // Initialized to zero by default.

    private float3 physBoundBoxSize;
    private float3 physModelPos;
    private float3 physDomainSize;
    private int3 gridRes;

    float dx;

    private Stopwatch stopwatch = new();

    int modelBottomCell = int.MaxValue;

    private HashSet<string> insideBuildingsIdSet = new HashSet<string>();
    private HashSet<string> edgeBuildingsIdSet = new HashSet<string>();

    public float[] facePosXArray;
    public float[] facePosYArray;
    public float[] facePosZArray;

    public Voxelizer(FluidSimConfig cfIn, RuntimeConfig rcfIn, GameObject model)
    {
        LoadConfig(cfIn, rcfIn);

        flags = new int[gridRes.x * gridRes.y * gridRes.z];

        // Init building mesh bounding box.
        if (model == null)
        {
            UnityEngine.Debug.LogError("The model GO needs to determined.");
            return;
        }

        MeshCollider meshCollider = model.GetComponentInChildren<MeshCollider>();
        if (meshCollider == null)
        {
            UnityEngine.Debug.LogError("The model GO needs a MeshCollider to be voxelized.");
            return;
        }

        Renderer renderer = model.GetComponentInChildren<Renderer>();
        if (renderer == null)
        {
            UnityEngine.Debug.LogError("The building GO needs a Renderer component.");
            return;
        }

        cf.physModelPos = model.transform.position;
        physBoundBoxSize = renderer.bounds.size;

        //float3 physFieldPos = config.physModelPos + config.physFieldOffset;
        if (cf.attachFieldToModelBottom)
        {
            float3 physFieldPos = cf.physFieldPos;
            physFieldPos.y += rcf.physDomainSize.y / 2.0f - physBoundBoxSize.y / 2.0f;
            cf.physFieldPos = physFieldPos;
        }

        UnityEngine.Debug.Log("Model pos: " + cf.physModelPos);
        UnityEngine.Debug.Log("Model size: " + physBoundBoxSize);
    }

    void LoadConfig(FluidSimConfig config, RuntimeConfig rcfIn)
    {
        cf = config;
        rcf = rcfIn;

        physDomainSize = rcf.physDomainSize;
        dx = rcf.dxUnif;
        gridRes = rcf.numCells;
        physModelPos = cf.physModelPos;
    }

    // ----- RayCasting in Unity: 1. Edit -> Project Settings -> Physics Settings -> Game Object -> Queries Hit Backfaces: On.
    // Otherwise the ray cannot hit from inside the mesh. 2. Func "RaycastAll" can only hit one collider once. So we need
    // to loop to continue casting rays. -----
    public (int[], float) VoxelizeMesh()
    {
        Array.Clear(flags, 0, flags.Length);

        if (cf.voxelizerType == VoxelizerType.YDirFromTop)
        {
            return (VoxelizeMeshScanLineYDirFromTop(), 0);

        }
        else if (cf.voxelizerType == VoxelizerType.YDirFromTopOneColli)
        {
            return VoxelizeMeshOneColliCullBoundaryBuildings();
            //return VoxelizeMeshScanLineYDirFromTopOneColli();
        }
        else
        {
            return (VoxelizeMeshScanLine(), 0);
        }
    }
    
    private int[] VoxelizeMeshLegacy()
    {
        UnityEngine.Debug.Log("Res: " + gridRes.x + " " + gridRes.y + " " + gridRes.z);

        int numCellsInside = 0;
        int numCellsOutside = 0;

        GameObject parentObj;
        if (cf.showVoxelization)
        {
            parentObj = new GameObject("ModelVoxels");
        }

        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        for (int z = 0; z < gridRes.z; z += 1)
            for (int y = 0; y < gridRes.y; y += 1)
                for (int x = 1; x < gridRes.x; x += 1)
                {
                    int intersectCount = 0;

                    float3 offset = new float3(x + 0.5f, y + 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                    float3 physPos = cf.physFieldPos + offset;
                    float3 direct = math.normalize(physModelPos - physPos); // Let the ray point to the model center.
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
                        flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 0;
                    }
                    else
                    {
                        numCellsInside++;
                        flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 1;

                        if (cf.showVoxelization)
                        {
                            GameObject voxelInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            voxelInstance.transform.position = physPos;
                            voxelInstance.transform.localScale = new Vector3(1, 1, 1);
                            voxelInstance.GetComponent<BoxCollider>().enabled = false;
                            voxelInstance.GetComponent<Renderer>().material.color = new Color(0.0f, 0.0f, 1.0f);
                            voxelInstance.transform.SetParent(GameObject.Find("ModelVoxels").transform);
                        }
                    }
                }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Point in cell voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        UnityEngine.Debug.Log("Number of cells inside: " + numCellsInside);
        UnityEngine.Debug.Log("Number of cells outside: " + numCellsOutside);

        return flags;
    }

    private int[] VoxelizeMeshScanLine()
    {
        UnityEngine.Debug.Log("Res: " + gridRes.x + " " + gridRes.y + " " + gridRes.z);

        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        for (int z = 0; z < gridRes.z; z += 1)
            for (int y = 0; y < gridRes.y; y += 1)
            {
                float3 offset = new float3(0.5f, y + 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                float3 physPos = rcf.physFieldPos + offset;
                float3 direct = new(1.0f, 0.0f, 0.0f);

                Ray ray = new(physPos, direct);
                RaycastHit[] hits = Physics.RaycastAll(ray);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                bool inside = false;
                int lastHitPointX = 0;
                int hitPointX = 0;

                while (hits.Length > 0)
                {
                    hitPointX = Mathf.RoundToInt((hits[0].point.x - rcf.physFieldPos.x + physDomainSize.x / 2f) / dx);
                    if (hitPointX > gridRes.x)
                    {
                        break;
                    }
                    for (int x = lastHitPointX; x < hitPointX; x += 1)
                    {
                        flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = inside ? 1 : 0;
                    }

                    inside = !inside;
                    lastHitPointX = hitPointX;

                    // Use the original y and z to avoid precision issue.
                    float3 hitpoint = new float3(hits[0].point.x, physPos.y, physPos.z);
                    ray = new Ray(hitpoint + direct / 1000.0f, direct);

                    // Old implementation.
                    //ray = new Ray((float3)hits[0].point + direct / 100.0f, direct);

                    hits = Physics.RaycastAll(ray);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                    modelBottomCell = math.min(modelBottomCell, y);
                }

                for (int x = hitPointX; x < gridRes.x; x += 1)
                {
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 0;
                }
            }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        if (cf.showVoxelization)
        {
            GameObject parentObj = new GameObject("ModelVoxels");

            for (int z = 0; z < gridRes.z; z += 1)
                for (int y = 0; y < gridRes.y; y += 1)
                    for (int x = 0; x < gridRes.x; x += 1)
                    {
                        if (flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] == 1)
                        {
                            float3 offset = new float3(x + 0.5f, y + 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                            float3 physPos = rcf.physFieldPos + offset;

                            GameObject voxelInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            voxelInstance.transform.position = physPos;
                            voxelInstance.transform.localScale = new Vector3(1, 1, 1);
                            voxelInstance.GetComponent<BoxCollider>().enabled = false;
                            voxelInstance.GetComponent<Renderer>().material.color = new Color(0.0f, 0.0f, 1.0f);
                            voxelInstance.transform.SetParent(parentObj.transform);
                        }
                    }
        }

        return flags;
    }

    private int[] VoxelizeMeshScanLineYDirFromTopOneCollisionNoRotation()
    {
        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        int terrainMask = LayerMask.GetMask("cesiumTerrain");

        for (int z = 0; z < gridRes.z; z += 1)
            for (int x = 0; x < gridRes.x; x += 1)
            {
                float3 offset = new float3(x + 0.5f, gridRes.y - 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                float3 physPos = cf.physFieldPos + offset;
                float3 direct = new(0.0f, -1.0f, 0.0f);

                Ray ray = new(physPos, direct);
                RaycastHit[] hits;
                if (x > 10 && x < rcf.numCells.x - 10)
                {
                    hits = Physics.RaycastAll(ray);
                }
                else
                {
                    hits = Physics.RaycastAll(ray, float.PositiveInfinity, terrainMask);
                }

                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                int hitPointY = 0;

                if (hits.Length > 0)
                {
                    hitPointY = Mathf.RoundToInt((hits[0].point.y - cf.physFieldPos.y + physDomainSize.y / 2f) / dx);
                }

                for (int y = hitPointY; y >= 0; y -= 1)
                {
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 1;
                }
            }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        if (cf.showVoxelization)
        {
            GameObject parentObj = new GameObject("ModelVoxels");

            for (int z = 0; z < gridRes.z; z += 1)
                for (int y = 0; y < gridRes.y; y += 1)
                    for (int x = 0; x < gridRes.x; x += 1)
                    {
                        if (flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] == 1)
                        {
                            float3 offset = new float3(x + 0.5f, y + 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                            float3 physPos = cf.physFieldPos + offset;

                            GameObject voxelInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            voxelInstance.transform.position = physPos;
                            voxelInstance.transform.localScale = new Vector3(1, 1, 1);
                            voxelInstance.GetComponent<BoxCollider>().enabled = false;
                            voxelInstance.GetComponent<Renderer>().material.color = new Color(0.0f, 0.0f, 1.0f);
                            voxelInstance.transform.SetParent(parentObj.transform);
                        }
                    }
        }

        return flags;
    }

    /// <summary>
    /// Cast rays from top (y = Ny - 1), only the first collision per ray is used.
    /// </summary>
    /// <returns>An int[] array containing the cell flags.</returns>
    private (int[], float) VoxelizeMeshScanLineYDirFromTopOneColli()
    {
        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        int terrainMask = LayerMask.GetMask("cesiumTerrain");

        Quaternion q = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);

        float maxH = 0;

        for (int z = 0; z < gridRes.z; z += 1)
            for (int x = 0; x < gridRes.x; x += 1)
            {
                Vector3 offsetVec = q * (new Vector3(x + 0.5f, gridRes.y - 0.5f, z + 0.5f) * dx - (Vector3)physDomainSize / 2f);
                float3 offset = offsetVec;

                float3 physPos = cf.physFieldPos + offset;
                float3 direct = new Vector3(0.0f, -1.0f, 0.0f);

                Ray ray = new(physPos, direct);
                RaycastHit[] hits;

                // ----- Skip building intersection for solver stability near the x boundaries -----
                if (x > 10 && x < rcf.numCells.x - 10 && z > 10 && z < rcf.numCells.z - 10)
                    hits = Physics.RaycastAll(ray);
                else
                    hits = Physics.RaycastAll(ray, float.PositiveInfinity, terrainMask);

                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                int hitPointY = 0;

                if (hits.Length > 0)
                {
                    hitPointY = Mathf.RoundToInt((hits[0].point.y - cf.physFieldPos.y + physDomainSize.y / 2f) / dx);
                    maxH = math.max(maxH, hits[0].point.y);
                }

                for (int y = hitPointY; y >= 0; y -= 1)
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 1;
            }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        return (flags, maxH);
    }

    private (int[], float) VoxelizeMeshOneColliCullBoundaryBuildings()
    {
        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        int terrainMask = LayerMask.GetMask("CesiumTerrain");

        Quaternion q = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);
        Quaternion invQ = Quaternion.Inverse(q);

        float maxH = 0;

        // ----- The bounds for culling boundary buildings -----
        Bounds queryBounds = new Bounds();
        queryBounds.center = rcf.physFieldPos + new float3(0, rcf.unifRegionPhysDomainSize.y / 2f, 0);
        
        // ----- Shrink the bounds a little to avoid building touches the fluid field boundary. 
        // The boundary needs to be shrinked more if unif grid is used, since there is no extra
        // buffer region. -----
        if (cf.grid is NonUniformGridAuto)
        {
            queryBounds.size = new Vector3(
                rcf.unifRegionPhysDomainSize.x - 2,
                rcf.unifRegionPhysDomainSize.y,
                rcf.unifRegionPhysDomainSize.z - 2);
        }
        else
        {
            queryBounds.size = new Vector3(
                rcf.unifRegionPhysDomainSize.x - 20,
                rcf.unifRegionPhysDomainSize.y,
                rcf.unifRegionPhysDomainSize.z - 20);
        }

        for (int z = 0; z < rcf.unifRegionNumCells.z; z += 1)
            for (int x = 0; x < rcf.unifRegionNumCells.x; x += 1)
            {
                Vector3 offsetVec = q * (new Vector3(x + 0.5f, gridRes.y - 0.5f, z + 0.5f) * dx - new Vector3(physDomainSize.x / 2f, 0, physDomainSize.z / 2f));
                float3 offset = offsetVec;

                float3 physPos = rcf.physFieldPos + offset;
                float3 direct = new Vector3(0.0f, -1.0f, 0.0f);

                Ray ray = new(physPos, direct);
                bool hasHit = Physics.Raycast(ray, out RaycastHit hit);

                int hitPointY = 0;

                if (hasHit)
                {
                    hitPointY = Mathf.RoundToInt((hit.point.y - rcf.physFieldPos.y) / dx);
                    maxH = math.max(maxH, hit.point.y);

                    CesiumPrimitiveFeatures primitiveFeatures = hit.transform.GetComponent<CesiumPrimitiveFeatures>();
                    CesiumModelMetadata metadata = hit.collider.GetComponentInParent<CesiumModelMetadata>();

                    // ----- Handle buildings -----
                    if (primitiveFeatures != null && metadata != null)
                    {
                        // ----- Get elementId -----
                        long featureId = primitiveFeatures.GetFeatureIdFromRaycastHit(hit, 0);
                        if (featureId < 0)
                        {
                            UnityEngine.Debug.Log("Cannot access featureId");
                        }

                        CesiumFeatureIdSet featureIdSet = primitiveFeatures.featureIdSets[0];
                        long tableIndex = featureIdSet.propertyTableIndex;

                        if (tableIndex < 0 || tableIndex >= metadata.propertyTables.Length)
                        {
                            UnityEngine.Debug.Log("propertyTableIndex is invalid");
                        }

                        CesiumPropertyTable propertyTable = metadata.propertyTables[tableIndex];

                        var values = propertyTable.GetMetadataValuesForFeature(featureId);

                        if (values.TryGetValue("elementId", out CesiumMetadataValue idValue))
                        {
                            string buildingId = idValue.GetString("");

                            if (insideBuildingsIdSet.Contains(buildingId))
                            {
                            }
                            else if (edgeBuildingsIdSet.Contains(buildingId))
                            {
                                hitPointY = 0;
                                hasHit = Physics.Raycast(ray, out hit, float.PositiveInfinity, terrainMask);
                                if (hasHit)
                                    hitPointY = Mathf.RoundToInt((hit.point.y - rcf.physFieldPos.y) / dx);
                            }
                            else
                            {
                                MeshFilter meshFilter = hit.transform.GetComponent<MeshFilter>();

                                Vector3[] vertices = meshFilter.mesh.vertices;
                                bool hasVertexOutside = false;

                                for (int i = 0; i < vertices.Length; i++)
                                {
                                    if (featureIdSet.GetFeatureIdForVertex(i) != featureId)
                                        continue;

                                    Vector3 worldPos = hit.transform.TransformPoint(vertices[i]);
                                    Vector3 localPos = invQ * (worldPos - queryBounds.center);
                                    Vector3 unRotPos = localPos + queryBounds.center;

                                    if (!queryBounds.Contains(unRotPos))
                                    {
                                        hasVertexOutside = true;
                                        break;
                                    }
                                }

                                if (hasVertexOutside)
                                {
                                    edgeBuildingsIdSet.Add(buildingId);
                                    hitPointY = 0;
                                    hasHit = Physics.Raycast(ray, out hit, float.PositiveInfinity, terrainMask);
                                    if (hasHit)
                                        hitPointY = Mathf.RoundToInt((hit.point.y - rcf.physFieldPos.y) / dx);
                                }
                                else
                                {
                                    insideBuildingsIdSet.Add(buildingId);
                                }
                            }
                        }
                    }
                }

                for (int y = hitPointY; y >= 0; y -= 1)
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 1;
            }

        UnityEngine.Debug.Log("Number of unique buildings: " + insideBuildingsIdSet.Count);
        UnityEngine.Debug.Log("Number of unique edge buildings: " + edgeBuildingsIdSet.Count);

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        return (flags, maxH);
    }

    public int[] VoxelizeMeshOneColliFillNonUnifRegions()
    {
        // ----- Fill in the uniform region -----
        int[] flagsNonUnif = new int[rcf.numCells.x * rcf.numCells.y * rcf.numCells.z];

        for (int z = 0; z < rcf.unifRegionNumCells.z; z++)
            for (int y = 0; y < rcf.unifRegionNumCells.y; y++)
            {
                int idx1Start = (rcf.unifRegionNumCells.x * rcf.unifRegionNumCells.y) * z + rcf.unifRegionNumCells.x * y;

                int idx2Start = (rcf.numCells.x * rcf.numCells.y) * (rcf.nonUnifRegion0NumCells.z + z) + rcf.numCells.x * (rcf.nonUnifRegion0NumCells.y + y) + rcf.nonUnifRegion0NumCells.x;

                Array.Copy(flags, idx1Start, flagsNonUnif, idx2Start, rcf.unifRegionNumCells.x);
            }

        // ----- Fill in the non-uniform region -----
        int terrainMask = LayerMask.GetMask("CesiumTerrain");

        Quaternion q = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);

        float maxH = 0;

        for (int z = 0; z < rcf.numCells.z; z += 1)
            for (int x = 0; x < rcf.numCells.x; x += 1)
            {
                if (z < rcf.nonUnifRegion0NumCells.z || z >= rcf.nonUnifRegion0NumCells.z + rcf.unifRegionNumCells.z ||
                    x < rcf.nonUnifRegion0NumCells.x || x >= rcf.nonUnifRegion0NumCells.x + rcf.unifRegionNumCells.x)
                {
                    float posXOffset = 0.5f * (facePosXArray[x] + facePosXArray[x + 1]);
                    float posYOffset = 0.5f * (facePosYArray[^1] + facePosYArray[^2]);
                    float posZOffset = 0.5f * (facePosZArray[z] + facePosZArray[z + 1]);

                    float posX = rcf.physFieldPos.x + posXOffset;
                    float posY = rcf.physFieldPos.y + posYOffset;
                    float posZ = rcf.physFieldPos.z + posZOffset;

                    float3 physPos = new float3(posX, posY, posZ);
                    float3 direct = new Vector3(0.0f, -1.0f, 0.0f);

                    Ray ray = new(physPos, direct);
                    RaycastHit[] hits;

                    hits = Physics.RaycastAll(ray, float.PositiveInfinity, terrainMask);
                    //hits = Physics.RaycastAll(ray);

                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                    int hitPointY = 0;

                    if (hits.Length > 0)
                    {
                        hitPointY = Mathf.RoundToInt((hits[0].point.y - rcf.physFieldPos.y) / dx);
                        maxH = math.max(maxH, hits[0].point.y);
                    }

                    for (int y = hitPointY; y >= 0; y -= 1)
                        flagsNonUnif[z * (rcf.numCells.x * rcf.numCells.y) + y * rcf.numCells.x + x] = 1;
                }
            }

        return flagsNonUnif;
    }

    /// <summary>
    /// Cast rays from top (y = Ny - 1) and perform intersections with all the meshes in the 
    /// simulation domain. If part of the mesh is outside the domain, that mesh will not be 
    /// voxelized.
    /// </summary>
    /// <returns>An int[] array containing the cell flags.</returns>
    private int[] VoxelizeMeshScanLineYDirFromTop()
    {
        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        Quaternion q = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);

        for (int z = 0; z < gridRes.z; z += 1)
            for (int x = 0; x < gridRes.x; x += 1)
            {
                // ----- Obtain current cell's physical position -----
                Vector3 offsetVec = q * (new Vector3(x + 0.5f, gridRes.y - 0.5f, z + 0.5f) * dx - (Vector3)physDomainSize / 2f);
                float3 offset = offsetVec;

                float3 physPos = rcf.physFieldPos + offset;
                float3 direct = new(0.0f, -1.0f, 0.0f);

                Ray ray = new(physPos, direct);
                RaycastHit[] hits = Physics.RaycastAll(ray);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                bool inside = false;
                int lastHitPointY = gridRes.y - 1;
                int hitPointY = gridRes.y - 1;

                while (hits.Length > 0)
                {
                    hitPointY = Mathf.RoundToInt((hits[0].point.y - rcf.physFieldPos.y + physDomainSize.y / 2f) / dx);
                    if (hitPointY < 0)
                        break;

                    for (int y = lastHitPointY; y > hitPointY; y -= 1)
                        flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = inside ? 1 : 0;

                    inside = !inside;
                    lastHitPointY = hitPointY;

                    // Use the original x and z to avoid value drifting.
                    float3 hitpoint = new float3(physPos.x, hits[0].point.y, physPos.z);
                    ray = new Ray(hitpoint + direct / 100.0f, direct);

                    hits = Physics.RaycastAll(ray);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                }

                // ----- Update the bottom cell of the model -----
                if (hitPointY >= 0 && hitPointY < gridRes.y)
                    modelBottomCell = math.min(modelBottomCell, hitPointY + 1);

                if (hitPointY < 0)
                    hitPointY = 0;

                // ----- Fill the remaining cells below the last hit point as outside -----
                for (int y = hitPointY; y >= 0; y -= 1)
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 0;
            }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        return flags;
    }

    private int[] VoxelizeMeshScanLineYDirection()
    {
        UnityEngine.Debug.Log("Res: " + gridRes.x + " " + gridRes.y + " " + gridRes.z);
        flags = new int[gridRes.x * gridRes.y * gridRes.z]; // Initialized to zero by default.

        if (cf.showSimulationTime)
        {
            stopwatch.Start();
        }

        for (int z = 0; z < gridRes.z; z += 1)
            for (int x = 0; x < gridRes.x; x += 1)
            {
                float3 offset = new float3(x + 0.5f, 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                float3 physPos = cf.physFieldPos + offset;
                float3 direct = new(0.0f, 1.0f, 0.0f);

                Ray ray = new(physPos, direct);
                RaycastHit[] hits = Physics.RaycastAll(ray);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                //bool inside = false;
                bool inside = true;
                int lastHitPointY = 0;
                int hitPointY = 0;

                while (hits.Length > 0)
                {
                    hitPointY = Mathf.RoundToInt((hits[0].point.y - cf.physFieldPos.y + physDomainSize.y / 2f) / dx);
                    if (hitPointY >= gridRes.y)
                    {
                        break;
                    }
                    for (int y = lastHitPointY; y < hitPointY; y += 1)
                    {
                        flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = inside ? 1 : 0;
                    }

                    inside = !inside;
                    lastHitPointY = hitPointY;

                    // Use the original y and z to avoid precision issue.
                    float3 hitpoint = new float3(physPos.x, hits[0].point.y, physPos.z);
                    ray = new Ray(hitpoint + direct / 1000.0f, direct);

                    // Old implementation.
                    //ray = new Ray((float3)hits[0].point + direct / 100.0f, direct);

                    hits = Physics.RaycastAll(ray);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                    //modelBottomCell = math.min(modelBottomCell, y);
                }

                for (int y = hitPointY; y < gridRes.y; y += 1)
                {
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 0;
                }
            }

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            UnityEngine.Debug.Log($"Scanline voxelization consumed time: {ts.Seconds} s {ts.Milliseconds} ms");
        }

        if (cf.simulateGround)
        {
            SimulateGround();
        }

        if (cf.showVoxelization)
        {
            GameObject parentObj = new GameObject("ModelVoxels");

            for (int z = 0; z < gridRes.z; z += 1)
                for (int y = 0; y < gridRes.y; y += 1)
                    for (int x = 0; x < gridRes.x; x += 1)
                    {
                        if (flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] == 1)
                        {
                            float3 offset = new float3(x + 0.5f, y + 0.5f, z + 0.5f) * dx - physDomainSize / 2f;
                            float3 physPos = cf.physFieldPos + offset;

                            GameObject voxelInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            voxelInstance.transform.position = physPos;
                            voxelInstance.transform.localScale = new Vector3(1, 1, 1);
                            voxelInstance.GetComponent<BoxCollider>().enabled = false;
                            voxelInstance.GetComponent<Renderer>().material.color = new Color(0.0f, 0.0f, 1.0f);
                            voxelInstance.transform.SetParent(parentObj.transform);
                        }
                    }
        }

        return flags;
    }

    private void SimulateGround()
    {
        UnityEngine.Debug.Log("Simulating ground plane at cell: " + modelBottomCell);
        for (int y = 0; y < modelBottomCell; y += 1)
            for (int z = 0; z < gridRes.z; z += 1)
                for (int x = 0; x < gridRes.x; x += 1)
                {
                    flags[z * (gridRes.x * gridRes.y) + y * gridRes.x + x] = 1;
                }
    }

    public void SetFacePosArrays(float[] facePosXArrayIn, float[] facePosYArrayIn, float[] facePosZArrayIn)
    {
        facePosXArray = facePosXArrayIn;
        facePosYArray = facePosYArrayIn;
        facePosZArray = facePosZArrayIn;
    }
}
