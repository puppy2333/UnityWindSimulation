using UnityEngine;
using Unity.Mathematics;

// This class holds configuration settings that are adjustable at runtime, and discarded when the
// application stops.
public class RuntimeConfig
{
    public float flowFieldOrientation = 0.0f;
    public float windSpeed = 10.0f;
    public double3 llhCoord = new(0.0, 0.0, 0.0);

    public RuntimeConfig(FluidSimConfig cf, double3 llhCoordIn)
    {
        windSpeed = cf.velX0.x;
        llhCoord = llhCoordIn;
    }
}
