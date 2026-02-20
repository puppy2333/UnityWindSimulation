using System;
using Unity.Mathematics;
using UnityEngine;
using System.Diagnostics;

public class FvmSolverCpu : IFvmSolver
{
    FluidSimConfig cf;

    // User-defined parameters for fluid simulation.
    float3 physicalDomainSize;
    int gridResX;
    float dt;
    float mu; // Dynamic viscosity
    float den;
    float Umax; // Maximum velocity for jet flow
    float3 externalForce; // External force applied to the fluid

    // Fluid simulation parameters
    int3 gridRes;
    float dx;
    float ds;
    float dv;
    float nu; // Kinematic viscosity
    float t = 0;

    // Fluid field, 3 fields for both velocity and pressure are the minimum for Jacobi iteration.
    float3[,,] velField;
    float3[,,] velFieldLastTime;
    float3[,,] velFieldLastIter;

    float[,,] presFieldLastTime;
    float[,,] presCorrectField;
    float[,,] presCorrectFieldLastIter;

    private Stopwatch stopwatch = new Stopwatch();

    public FvmSolverCpu(FluidSimConfig config)
    {
        // Load configuration file.
        if (config == null)
        {
            UnityEngine.Debug.LogError("FluidSimConfig is not set. Please assign a configuration file.");
            return;
        }
        cf = config;
        cf.Init();

        physicalDomainSize = config.physDomainSize;
        gridResX = config.gridResX;
        dt = config.dt;
        mu = config.mu;
        den = config.den;
        Umax = config.Umax;
        externalForce = config.externalForce;

        // Init fluid simulation parameters.
        dx = physicalDomainSize.x / gridResX;
        ds = dx * dx;
        dv = dx * dx * dx;

        int gridResY = Mathf.RoundToInt(physicalDomainSize.y / dx);
        int gridResZ = Mathf.RoundToInt(physicalDomainSize.z / dx);
        gridRes = new int3(gridResX, gridResY, gridResZ);

        nu = mu / den;

        // Print grid information.
        UnityEngine.Debug.Log($"Grid Resolution: {gridRes.x} x {gridRes.y} x {gridRes.z}");
        UnityEngine.Debug.Log($"CFL condition (should be less than 1): {Umax * dt / dx}");

        // Allocate velocity and pressure fields.
        InitVelField();

        // Copy velField to velFieldLastTime and velFieldLastIter to get ready for simulation.
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
                for (int x = 0; x < gridRes.x; x++)
                {
                    velFieldLastTime[x, y, z] = velField[x, y, z];
                    velFieldLastIter[x, y, z] = velField[x, y, z];
                }
    }

    public bool Step()
    {
        if (cf.showSimulationTime)
        {
            stopwatch.Restart();
        }

        ComputePredictedVel();
        SolvePresCorrection();
        ApplyVelCorrection();

        t += dt;

        // ----- Post processing at the end of the time step -----
        (velFieldLastTime, velField) = (velField, velFieldLastTime);

        // Initialize "velFieldLastIter" to "velFieldLastTime" for faster convergence.
        CopyVelField(velFieldLastIter, velFieldLastTime);

        // Initialize pressure corrected field to zero.
        Array.Clear(presCorrectField, 0, presCorrectField.Length);
        Array.Clear(presCorrectFieldLastIter, 0, presCorrectFieldLastIter.Length);

        if (cf.showSimulationTime)
        {
            stopwatch.Stop();
            long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            // Print the current simulation step.
            UnityEngine.Debug.Log($"Simulated physical time: {t:F2} seconds, consumed time: {elapsedMilliseconds / 1000f} s.");
            //Debug.Log($"Two vel values: {velField[50, 4, 10]}, {velField[50, 45, 10]}.");
        }

        return true;
    }

    void InitVelField()
    {
        velField = new float3[gridRes.x, gridRes.y, gridRes.z];
        velFieldLastTime = new float3[gridRes.x, gridRes.y, gridRes.z];
        velFieldLastIter = new float3[gridRes.x, gridRes.y, gridRes.z];

        presFieldLastTime = new float[gridRes.x, gridRes.y, gridRes.z];
        presCorrectField = new float[gridRes.x, gridRes.y, gridRes.z];
        presCorrectFieldLastIter = new float[gridRes.x, gridRes.y, gridRes.z];
    }

    void InitCavityFlowBndCond()
    {
        // Initialize velocity field for cavity flow condition.
        for (int z = 0; z < gridRes.z; z++)
            for (int x = 0; x < gridRes.x; x++)
            {
                velField[x, 0, z] = new float3(0f, 0f, 0f); // Bottom wall
                velField[x, gridRes.y - 1, z] = new float3(Umax, 0f, 0f); // Top wall
            }
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
            {
                velField[0, y, z] = new float3(0f, 0f, 0f); // Left wall
                velField[gridRes.x - 1, y, z] = new float3(0f, 0f, 0f); // Right wall
            }
        for (int y = 0; y < gridRes.y; y++)
            for (int x = 0; x < gridRes.x; x++)
            {
                velField[x, y, 0] = new float3(0f, 0f, 0f); // Back wall
                velField[x, y, gridRes.z - 1] = new float3(0f, 0f, 0f); // Front wall
            }
    }

    void InitJetFlowBndCond()
    {
        // Initialize velocity field with a jet flow condition.
        float y0 = gridRes.y / 2 - 0.5f;
        float z0 = gridRes.z / 2 - 0.5f;
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
            {
                float r = math.sqrt((y - y0) * (y - y0) + (z - z0) * (z - z0));
                if (r <= cf.R.x)
                {
                    float U = Umax * (1 - math.pow(r / cf.R.x, 2));
                    velField[0, y, z] = new float3(U, 0f, 0f);
                }
            }
    }

    void ComputePredictedVel()
    {
        // Solve the momentum equation to obtain predicted velocity u*.
        for (int iter = 0; iter < cf.velMaxNumIter; iter++)
        {
            for (int z = 1; z < gridRes.z - 1; z++)
                for (int y = 1; y < gridRes.y - 1; y++)
                    for (int x = 1; x < gridRes.x - 1; x++)
                    {
                        //Debug.Log($"Constructing A at ({x}, {y}, {z})");

                        velField[x, y, z].x = 1 / (dv / dt + 6 * nu * ds / dx) * ( 0
                            - (ds / 2 * velFieldLastTime[x + 1, y, z].x) * velFieldLastIter[x + 1, y, z].x
                            + (ds / 2 * velFieldLastTime[x - 1, y, z].x) * velFieldLastIter[x - 1, y, z].x
                            - (ds / 2 * velFieldLastTime[x, y + 1, z].x) * velFieldLastIter[x, y + 1, z].y
                            + (ds / 2 * velFieldLastTime[x, y - 1, z].x) * velFieldLastIter[x, y - 1, z].y
                            - (ds / 2 * velFieldLastTime[x, y, z + 1].x) * velFieldLastIter[x, y, z + 1].z
                            + (ds / 2 * velFieldLastTime[x, y, z - 1].x) * velFieldLastIter[x, y, z - 1].z
                            + (nu * ds / dx) * (
                            velFieldLastIter[x + 1, y, z].x + velFieldLastIter[x - 1, y, z].x +
                            velFieldLastIter[x, y + 1, z].x + velFieldLastIter[x, y - 1, z].x +
                            velFieldLastIter[x, y, z + 1].x + velFieldLastIter[x, y, z - 1].x)
                            + (dv / dt) * velFieldLastTime[x, y, z].x
                            - (ds / (2 * den)) * (presFieldLastTime[x + 1, y, z] - presFieldLastTime[x - 1, y, z])
                            + (externalForce.x * dv)
                            );

                        velField[x, y, z].y = 1 / (dv / dt + 6 * nu * ds / dx) * ( 0
                            - (ds / 2 * velFieldLastTime[x + 1, y, z].y) * velFieldLastIter[x + 1, y, z].x
                            + (ds / 2 * velFieldLastTime[x - 1, y, z].y) * velFieldLastIter[x - 1, y, z].x
                            - (ds / 2 * velFieldLastTime[x, y + 1, z].y) * velFieldLastIter[x, y + 1, z].y
                            + (ds / 2 * velFieldLastTime[x, y - 1, z].y) * velFieldLastIter[x, y - 1, z].y
                            - (ds / 2 * velFieldLastTime[x, y, z + 1].y) * velFieldLastIter[x, y, z + 1].z
                            + (ds / 2 * velFieldLastTime[x, y, z - 1].y) * velFieldLastIter[x, y, z - 1].z
                            + (nu * ds / dx) * (
                            velFieldLastIter[x + 1, y, z].y + velFieldLastIter[x - 1, y, z].y +
                            velFieldLastIter[x, y + 1, z].y + velFieldLastIter[x, y - 1, z].y +
                            velFieldLastIter[x, y, z + 1].y + velFieldLastIter[x, y, z - 1].y)
                            + (dv / dt) * velFieldLastTime[x, y, z].y
                            - (ds / (2 * den)) * (presFieldLastTime[x, y + 1, z] - presFieldLastTime[x, y - 1, z])
                            + (externalForce.y * dv)
                            );

                        velField[x, y, z].z = 1 / (dv / dt + 6 * nu * ds / dx) * ( 0
                            - (ds / 2 * velFieldLastTime[x + 1, y, z].z) * velFieldLastIter[x + 1, y, z].x
                            + (ds / 2 * velFieldLastTime[x - 1, y, z].z) * velFieldLastIter[x - 1, y, z].x
                            - (ds / 2 * velFieldLastTime[x, y + 1, z].z) * velFieldLastIter[x, y + 1, z].y
                            + (ds / 2 * velFieldLastTime[x, y - 1, z].z) * velFieldLastIter[x, y - 1, z].y
                            - (ds / 2 * velFieldLastTime[x, y, z + 1].z) * velFieldLastIter[x, y, z + 1].z
                            + (ds / 2 * velFieldLastTime[x, y, z - 1].z) * velFieldLastIter[x, y, z - 1].z
                            + (nu * ds / dx) * (
                            velFieldLastIter[x + 1, y, z].z + velFieldLastIter[x - 1, y, z].z +
                            velFieldLastIter[x, y + 1, z].z + velFieldLastIter[x, y - 1, z].z +
                            velFieldLastIter[x, y, z + 1].z + velFieldLastIter[x, y, z - 1].z)
                            + (dv / dt) * velFieldLastTime[x, y, z].z
                            - (ds / (2 * den)) * (presFieldLastTime[x, y, z + 1] - presFieldLastTime[x, y, z - 1])
                            + (externalForce.z * dv)
                            );
                    }

            //ZeroVelocityBoundary();

            // Swap the current and last iteration velocity fields.
            (velFieldLastIter, velField) = (velField, velFieldLastIter);
        }
        // Ensure up to date value is stored in "<quantity>Field", not "<quantity>FieldLastIter".
        (velFieldLastIter, velField) = (velField, velFieldLastIter);
    }

    void ZeroVelocityBoundary()
    {
        // Currently the whole field is set to zero, so nothing needs to be done here.
        return;
    }

    void SolvePresCorrection()
    {
        float DInv = 1 / (dv / dt + 6 * nu * ds / dx);
        float D = dv / dt + 6 * nu * ds / dx;

        // Solve the continuity equation to obtain pressure correction.
        for (int iter = 0; iter < cf.presMaxNumIter; iter++)
        {
            // Iterate over the grid to compute pressure correction p'.
            for (int z = 1; z < gridRes.z - 1; z++)
                for (int y = 1; y < gridRes.y - 1; y++)
                    for (int x = 1; x < gridRes.x - 1; x++)
                    {
                        presCorrectField[x, y, z] = (dx / 6) * (-D / 2 * (0
                            + velField[x + 1, y, z].x - velField[x - 1, y, z].x
                            + velField[x, y + 1, z].y - velField[x, y - 1, z].y
                            + velField[x, y, z + 1].z - velField[x, y, z - 1].z)
                            +  1 / dx * (0
                            + presCorrectFieldLastIter[x + 1, y, z] + presCorrectFieldLastIter[x - 1, y, z]
                            + presCorrectFieldLastIter[x, y + 1, z] + presCorrectFieldLastIter[x, y - 1, z]
                            + presCorrectFieldLastIter[x, y, z + 1] + presCorrectFieldLastIter[x, y, z - 1])
                            );
                    }

            NeumannPressureBoundary();

            // Swap the current and last iteration pressure correction fields.
            (presCorrectFieldLastIter, presCorrectField) = (presCorrectField, presCorrectFieldLastIter);
        }
        // Ensure up to date value is stored in "<quantity>Field", not "<quantity>FieldLastIter".
        (presCorrectFieldLastIter, presCorrectField) = (presCorrectField, presCorrectFieldLastIter);

        // Update pressure using p', p* = p^t + p'. We use "presFieldLastTime" to represent
        // pressure field of current time step, to save memory.
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
                for (int x = 0; x < gridRes.x; x++)
                {
                    presFieldLastTime[x, y, z] += presCorrectField[x, y, z];
                }

        // Pressure field normalization.
        float refPres = presFieldLastTime[gridRes.x / 2, gridRes.y / 2, gridRes.z / 2];
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
                for (int x = 0; x < gridRes.x; x++)
                {
                    presFieldLastTime[x, y, z] -= refPres;
                }
    }

    void NeumannPressureBoundary()
    {
        // Implicit corner cell filling for CPU solver.
        // Neumann boundary condition for pressure correction, x = 0 and x = gridRes.x - 1
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
            {
                presCorrectField[0, y, z] = presCorrectField[1, y, z];
                presCorrectField[gridRes.x - 1, y, z] = presCorrectField[gridRes.x - 2, y, z];
            }

        // Neumann boundary condition for pressure correction, y = 0 and y = gridRes.y - 1
        for (int z = 0; z < gridRes.z; z++)
            for (int x = 0; x < gridRes.x; x++)
            {
                presCorrectField[x, 0, z] = presCorrectField[x, 1, z];
                presCorrectField[x, gridRes.y - 1, z] = presCorrectField[x, gridRes.y - 2, z];
            }

        // Neumann boundary condition for pressure correction, z = 0 and z = gridRes.z - 1
        for (int y = 0; y < gridRes.y; y++)
            for (int x = 0; x < gridRes.x; x++)
            {
                presCorrectField[x, y, 0] = presCorrectField[x, y, 1];
                presCorrectField[x, y, gridRes.z - 1] = presCorrectField[x, y, gridRes.z - 2];
            }
    }

    void ApplyVelCorrection()
    {
        float DInv = 1 / (dv / dt + 6 * nu * ds / dx);

        // u** = u* + u' (calculated from p')
        for (int z = 1; z < gridRes.z - 1; z++)
            for (int y = 1; y < gridRes.y - 1; y++)
                for (int x = 1; x < gridRes.x - 1; x++)
                {
                    velField[x, y, z].x += -DInv * (presCorrectField[x + 1, y, z] - presCorrectField[x - 1, y, z]) / (2 * dx);
                    velField[x, y, z].y += -DInv * (presCorrectField[x, y + 1, z] - presCorrectField[x, y - 1, z]) / (2 * dx);
                    velField[x, y, z].z += -DInv * (presCorrectField[x, y, z + 1] - presCorrectField[x, y, z - 1]) / (2 * dx);
                }
    }

    void CopyVelField(float3[,,] dest, float3[,,] source)
    {
        for (int z = 0; z < gridRes.z; z++)
            for (int y = 0; y < gridRes.y; y++)
                for (int x = 0; x < gridRes.x; x++)
                {
                    dest[x, y, z] = source[x, y, z];
                }
    }

    public object GetVelField()
    {
        // Return the velocity field for visualization.
        return velField;
    }

    public object GetPresField()
    {
        // Return the pressure field for visualization.
        return presFieldLastTime;
    }

    public void InitBuilding(GameObject model)
    {
        // Initialize the building model if needed.
        // Currently, this method does nothing as the CPU solver does not require a building model.
        UnityEngine.Debug.LogWarning("InitBuilding is not implemented for FvmSolverCpu.");
    }

    public void InitFlags(int[] flags)
    {
        throw new NotImplementedException();
    }

    public object GetFlagField()
    {
        throw new NotImplementedException();
    }

    public void ChangePhysFieldPos()
    {
        throw new NotImplementedException();
    }

    public void ChangePhysDomainSize()
    {
        throw new NotImplementedException();
    }

    public void InitVelPresFields()
    {
        throw new NotImplementedException();
    }

    public void InitVelPresFieldsFromBackGroundFlow(FluidSimConfig cfBackGround, RenderTexture velTexBackGround, RenderTexture presTexBackGround)
    {
        throw new NotImplementedException();
    }

    public void SetFixedValueVelBndCond()
    {
        throw new NotImplementedException();
    }
}