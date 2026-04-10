using System;
using Unity.Mathematics;
using UnityEngine;

// This class holds configuration settings that are adjustable at runtime, and discarded when the
// application stops.
public class RuntimeConfig
{
    public float3 physFieldPos = new(0.0f, 0.0f, 0.0f);
    public float flowFieldOrientation = 0.0f;
    public float windSpeed = 10.0f;
    public double3 llhCoord = new(0.0, 0.0, 0.0);

    // ----- Grid parameters -----
    public float dxUnif;
    public float dsUnif;
    public float dvUnif;

    public float3 physDomainSize;
    public float3 unifRegionPhysStartOffset;
    public float3 unifRegionPhysDomainSize;

    public int3 numCells;
    public int3 unifRegionNumCells;
    public int3 nonUnifRegion0NumCells;
    public int3 nonUnifRegionNNumCells;

    public float3 boxStartPhysOffset;
    public float3 boxEndPhysOffset;

    public float3 stretchFactor;

    public float maxH = 0;

    public RuntimeConfig(FluidSimConfig cf, double3 llhCoordIn)
    {
        physFieldPos = cf.physFieldPos;
        windSpeed = cf.velX0.x;
        llhCoord = llhCoordIn;

        Grid grid = cf.grid;
        switch (grid)
        {
            case UniformGrid uniformGrid:
                InitUnifGrid(uniformGrid);
                break;

            case NonUniformGridAuto nonUniformGrid:
                InitTempUnifGrid(nonUniformGrid);
                break;

            case NonUniformGridDefault nonUniformGridDefault:
                InitNonUnifGridDefault(nonUniformGridDefault);
                break;

            default:
                Debug.LogError("RuntimeConfig: FluidSimConfig has no grid.");
                break;
        }
    }

    // ----- l: physical size of a stretched grid block, r: stretch factor, dx: init cell size at
    // solid boundary. -----
    private int _CalcNonUnifGridCellNum(float l, float r, float dx)
    {
        if (Mathf.Abs(r - 1) < 1e-5)
            return Mathf.RoundToInt(l / dx);
        else
            return Mathf.CeilToInt(Mathf.Log(1 - l * (1 - r) / dx) / Mathf.Log(r));
    }

    // ----- l: specified physical size of a stretched grid block, r: stretch factor, dx: init
    // cell size at solid boundary, n: number of cells. -----
    private float _CalNonUnifGridRegionSize(float l, float r, float dx, float n)
    {
        if (Mathf.Abs(r - 1) < 1e-5)
            return l;
        else
            return dx * (1 - Mathf.Pow(r, n)) / (1 - r);
    }


    private void InitUnifGrid(UniformGrid grid)
    {
        dxUnif = grid.physDomainSize.x / grid.numCellsX;
        dsUnif = dxUnif * dxUnif;
        dvUnif = dxUnif * dxUnif * dxUnif;

        int numCellsY = Mathf.RoundToInt(grid.physDomainSize.y / dxUnif);
        int numCellsZ = Mathf.RoundToInt(grid.physDomainSize.z / dxUnif);

        physDomainSize = grid.physDomainSize;
        unifRegionPhysDomainSize = grid.physDomainSize;

        numCells = new int3(grid.numCellsX, numCellsY, numCellsZ);
        unifRegionNumCells = numCells;
    }

    private void InitTempUnifGrid(NonUniformGridAuto grid)
    {
        unifRegionPhysDomainSize = grid.unifRegionPhysDomainSize;
        physDomainSize = grid.unifRegionPhysDomainSize;

        dxUnif = grid.unifRegionPhysDomainSize.x / grid.unifRegionNumCellsX;

        int unifRegionNumCellsY = Mathf.RoundToInt(grid.unifRegionPhysDomainSize.y / dxUnif);
        int unifRegionNumCellsZ = Mathf.RoundToInt(grid.unifRegionPhysDomainSize.z / dxUnif);

        unifRegionNumCells = new int3(grid.unifRegionNumCellsX, unifRegionNumCellsY, unifRegionNumCellsZ);
        numCells = unifRegionNumCells;
    }

    public void InitNonUnifGridAuto(NonUniformGridAuto grid, float maxH)
    {
        // ----- Cal non-uniform layers width -----
        unifRegionPhysStartOffset.x = 5 * maxH;
        unifRegionPhysStartOffset.y = 0;
        unifRegionPhysStartOffset.z = 5 * maxH;

        stretchFactor = grid.stretchFactor;

        float3 unifRegionPhysEndOffset = unifRegionPhysStartOffset + unifRegionPhysDomainSize;

        dxUnif = unifRegionPhysDomainSize.x / grid.unifRegionNumCellsX;
        float dyUnif = dxUnif, dzUnif = dxUnif;

        unifRegionNumCells = new(
            grid.unifRegionNumCellsX,
            Mathf.RoundToInt(unifRegionPhysDomainSize.y / dyUnif),
            Mathf.RoundToInt(unifRegionPhysDomainSize.z / dzUnif)
            );

        // ----- X0 -----
        int nx0 = _CalcNonUnifGridCellNum(l: 5 * maxH, r: stretchFactor.x, dx: dxUnif);
        float nonUnifRegionPhysSizeX0 = _CalNonUnifGridRegionSize(l: 5 * maxH, r: stretchFactor.x, dx: dxUnif, n: nx0);
        // ----- Xn -----
        int nxn = _CalcNonUnifGridCellNum(l: 15 * maxH, r: stretchFactor.x, dx: dxUnif);
        float nonUnifRegionPhysSizeXn = _CalNonUnifGridRegionSize(l: 15 * maxH, r: stretchFactor.x, dx: dxUnif, n: nxn);
        // ----- Y0 -----
        int ny0 = _CalcNonUnifGridCellNum(l: 0, r: stretchFactor.y, dx: dyUnif);
        float nonUnifRegionPhysSizeY0 = _CalNonUnifGridRegionSize(l: 0, r: stretchFactor.y, dx: dyUnif, n: ny0);
        // ----- Yn -----
        int nyn = _CalcNonUnifGridCellNum(l: 5 * maxH, r: stretchFactor.y, dx: dyUnif);
        float nonUnifRegionPhysSizeYn = _CalNonUnifGridRegionSize(l: 5 * maxH, r: stretchFactor.y, dx: dyUnif, n: nyn);
        // ----- Z0 -----
        int nz0 = _CalcNonUnifGridCellNum(l: 5 * maxH, r: stretchFactor.z, dx: dzUnif);
        float nonUnifRegionPhysSizeZ0 = _CalNonUnifGridRegionSize(l: 5 * maxH, r: stretchFactor.z, dx: dzUnif, n: nz0);
        // ----- Zn -----
        int nzn = _CalcNonUnifGridCellNum(l: 5 * maxH, r: stretchFactor.z, dx: dzUnif);
        float nonUnifRegionPhysSizeZn = _CalNonUnifGridRegionSize(l: 5 * maxH, r: stretchFactor.z, dx: dzUnif, n: nzn);

        nonUnifRegion0NumCells = new int3(nx0, ny0, nz0);
        nonUnifRegionNNumCells = new int3(nxn, nyn, nzn);

        numCells = new int3(
            nx0 + unifRegionNumCells.x + nxn,
            ny0 + unifRegionNumCells.y + nyn,
            nz0 + unifRegionNumCells.z + nzn
            );

        unifRegionPhysStartOffset = new float3(
            nonUnifRegionPhysSizeX0, 
            nonUnifRegionPhysSizeY0, 
            nonUnifRegionPhysSizeZ0
            );

        physDomainSize = new float3(
            nonUnifRegionPhysSizeX0 + unifRegionPhysDomainSize.x + nonUnifRegionPhysSizeXn,
            nonUnifRegionPhysSizeY0 + unifRegionPhysDomainSize.y + nonUnifRegionPhysSizeYn,
            nonUnifRegionPhysSizeZ0 + unifRegionPhysDomainSize.z + nonUnifRegionPhysSizeZn
            );

        Debug.Log($"Num of cells: {numCells}");
        Debug.Log($"Real phys domain size: {physDomainSize}");
    }

    private void InitNonUnifGridDefault(NonUniformGridDefault grid)
    {
        unifRegionPhysStartOffset = grid.unifRegionPhysStartOffset;
        unifRegionPhysDomainSize = grid.unifRegionPhysDomainSize;
        stretchFactor = grid.stretchFactor;

        float3 unifRegionPhysEndOffset = unifRegionPhysStartOffset + unifRegionPhysDomainSize;

        // ----- Calculate non-uniform grid parameters -----
        // l: physical size of a stretched grid block, r: stretch factor, n: number of cells in
        // that grid block, dx: init cell size at solid boundary.
        // 
        // Formula: Sum_(j from 0 to ni-1)(dxi * ri^j) = li,
        // which is a geometric sequence, apply the sum formula: 
        // dxi * (1 - ri^ni) / (1 - ri) = li,
        // thus: ri^ni = 1 - li * (1 - ri) / dxi, apply ln on both sides:
        // ni * ln(ri) = ln(1 - li * (1 - ri) / dxi), thus,
        // ni = ln(1 - li * (1 - ri) / dxi) / ln(ri).
        // 
        // Then, the actual physical domain size is calculated using ni (ceil). The result will be 
        // greater or equal to the user specified physical domain size.
        //
        dxUnif = unifRegionPhysDomainSize.x / grid.unifRegionNumCellsX;
        float dyUnif = dxUnif, dzUnif = dxUnif;

        unifRegionNumCells = new(
            grid.unifRegionNumCellsX,
            Mathf.RoundToInt(unifRegionPhysDomainSize.y / dyUnif),
            Mathf.RoundToInt(unifRegionPhysDomainSize.z / dzUnif)
            );

        //Debug.Log($"Bugfix: {unifRegionPhysStartOffset.x} {stretchFactor.x} {dxUnif}");

        // ----- X0 -----
        int nx0 = _CalcNonUnifGridCellNum(l: unifRegionPhysStartOffset.x, r: stretchFactor.x, dx: dxUnif);
        float nonUnifRegionPhysSizeX0 = _CalNonUnifGridRegionSize(l: unifRegionPhysStartOffset.x, r: stretchFactor.x, dx: dxUnif, n: nx0);
        // ----- Xn -----
        int nxn = _CalcNonUnifGridCellNum(l: grid.userDefinedPhysDomainSize.x - unifRegionPhysEndOffset.x, r: stretchFactor.x, dx: dxUnif);
        float nonUnifRegionPhysSizeXn = _CalNonUnifGridRegionSize(l: grid.userDefinedPhysDomainSize.x - unifRegionPhysEndOffset.x, r: stretchFactor.x, dx: dxUnif, n: nxn);
        // ----- Y0 -----
        int ny0 = _CalcNonUnifGridCellNum(l: unifRegionPhysStartOffset.y, r: stretchFactor.y, dx: dyUnif);
        float nonUnifRegionPhysSizeY0 = _CalNonUnifGridRegionSize(l: unifRegionPhysStartOffset.y, r: stretchFactor.y, dx: dyUnif, n: ny0);
        // ----- Yn -----
        int nyn = _CalcNonUnifGridCellNum(l: grid.userDefinedPhysDomainSize.y - unifRegionPhysEndOffset.y, r: stretchFactor.y, dx: dyUnif);
        float nonUnifRegionPhysSizeYn = _CalNonUnifGridRegionSize(l: grid.userDefinedPhysDomainSize.y - unifRegionPhysEndOffset.y, r: stretchFactor.y, dx: dyUnif, n: nyn);
        // ----- Z0 -----
        int nz0 = _CalcNonUnifGridCellNum(l: unifRegionPhysStartOffset.z, r: stretchFactor.z, dx: dzUnif);
        float nonUnifRegionPhysSizeZ0 = _CalNonUnifGridRegionSize(l: unifRegionPhysStartOffset.z, r: stretchFactor.z, dx: dzUnif, n: nz0);
        // ----- Zn -----
        int nzn = _CalcNonUnifGridCellNum(l: grid.userDefinedPhysDomainSize.z - unifRegionPhysEndOffset.z, r: stretchFactor.z, dx: dzUnif);
        float nonUnifRegionPhysSizeZn = _CalNonUnifGridRegionSize(l: grid.userDefinedPhysDomainSize.z - unifRegionPhysEndOffset.z, r: stretchFactor.z, dx: dzUnif, n: nzn);

        Debug.Log($"nx0, nxn: {nx0}, {nxn}");

        nonUnifRegion0NumCells = new int3(nx0, ny0, nz0);
        nonUnifRegionNNumCells = new int3(nxn, nyn, nzn);

        numCells = new int3(
            nx0 + unifRegionNumCells.x + nxn,
            ny0 + unifRegionNumCells.y + nyn,
            nz0 + unifRegionNumCells.z + nzn
            );

        physDomainSize = new float3(
            nonUnifRegionPhysSizeX0 + unifRegionPhysDomainSize.x + nonUnifRegionPhysSizeXn,
            nonUnifRegionPhysSizeY0 + unifRegionPhysDomainSize.y + nonUnifRegionPhysSizeYn,
            nonUnifRegionPhysSizeZ0 + unifRegionPhysDomainSize.z + nonUnifRegionPhysSizeZn
            );

        boxStartPhysOffset = grid.userBoxStartPhysOffset + new float3(
            nonUnifRegionPhysSizeX0 - grid.userBoxStartPhysOffset.x,
            nonUnifRegionPhysSizeY0 - grid.userBoxStartPhysOffset.y,
            nonUnifRegionPhysSizeZ0 - grid.userBoxStartPhysOffset.z);

        boxEndPhysOffset = grid.userBoxEndPhysOffset + new float3(
            nonUnifRegionPhysSizeX0 - grid.userBoxStartPhysOffset.x,
            nonUnifRegionPhysSizeY0 - grid.userBoxStartPhysOffset.y,
            nonUnifRegionPhysSizeZ0 - grid.userBoxStartPhysOffset.z);

        Debug.Log($"Num of cells: {numCells}");
        Debug.Log($"Real phys domain size: {physDomainSize}");
    }
}
