using System;
using System.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

public class VisualizerCpu : IVisualizer
{
    FluidSimConfig cf;

    Texture2D velTex;
    Texture2D presTex;
    Renderer displayRenderer;
    public float3[,,] velFieldVis;
    public float[,,] presFieldVis;

    // User-defined parameters for fluid simulation.
    float3 physicalDomainSize;
    int gridResX;
    ColorMap colorMap;

    // Visualization parameters.
    int zSlice;
    float minVel, maxVel;
    float minPres, maxPres;
    float cameraHeight;
    float cameraAngle;

    // Fluid visualization parameters
    int3 gridRes;

    // Fluid solver
    IFvmSolver solver;

    // Turbo color map coefficients
    float4 kRedVec4 = new(0.13572138f, 4.61539260f, -42.66032258f, 132.13108234f);
    float4 kGreenVec4 = new(0.09140261f, 2.19418839f, 4.84296658f, -14.18503333f);
    float4 kBlueVec4 = new(0.10667330f, 12.64194608f, -60.58204836f, 110.36276771f);
    float2 kRedVec2 = new(-152.94239396f, 59.28637943f);
    float2 kGreenVec2 = new(4.27729857f, 2.82956604f);
    float2 kBlueVec2 = new(-89.90310912f, 27.34824973f);

    public VisualizerCpu(FluidSimConfig config, IFvmSolver solverIn)
    {
        solver = solverIn;

        // Load configuration file.
        if (config == null)
        {
            Debug.LogError("FluidSimConfig is not set. Please assign a configuration file.");
            return;
        }
        cf = config;
        cf.Init();

        physicalDomainSize = cf.physDomainSize;
        gridResX = cf.gridResX;
        colorMap = cf.colorMap;

        // Init fluid simulation parameters.
        float dx = physicalDomainSize.x / gridResX;
        int gridResY = Mathf.RoundToInt(physicalDomainSize.y / dx);
        int gridResZ = Mathf.RoundToInt(physicalDomainSize.z / dx);
        gridRes = new int3(gridResX, gridResY, gridResZ);

        // Init visualization parameters.
        zSlice = cf.ySlice;
        minVel = cf.minVel;
        maxVel = cf.maxVel;
        minPres = cf.minPres;
        maxPres = cf.maxPres;
        cameraHeight = cf.cameraHeight;
        cameraAngle = cf.cameraAngle;

        if (zSlice < 0 || zSlice >= gridResZ)
        {
            zSlice = gridResZ / 2;
        }

        velFieldVis = (float3[,,])solver.GetVelField();
        presFieldVis = (float[,,])solver.GetPresField();

        // Cameara setup.
        Camera.main.fieldOfView = cameraAngle;
        Camera.main.transform.SetPositionAndRotation(new Vector3(0, cameraHeight, 0), Quaternion.Euler(90, 0, 0));

        // Velocity field visualization.
        GameObject velQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        velQuad.transform.localScale = new Vector3(gridRes.x, gridRes.y, 1);
        if (cf.showPressureField == true)
            velQuad.transform.SetPositionAndRotation(new Vector3(-gridRes.x / 2 - 10, 0, 0), Quaternion.Euler(90, 0, 0));
        else
            velQuad.transform.SetPositionAndRotation(new Vector3(0, 0, 0), Quaternion.Euler(90, 0, 0));

        velTex = new(gridRes.x, gridRes.y);
        velTex.filterMode = FilterMode.Point;

        Color[] pixels = new Color[gridRes.x * gridRes.y];
        //for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.black;
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.blue;

        velTex.SetPixels(pixels);
        velTex.Apply();

        Material velMaterial = new(Shader.Find("HDRP/Lit"));
        velMaterial.SetTexture("_BaseColorMap", velTex);
        velMaterial.SetFloat("_Smoothness", 0);

        velQuad.GetComponent<Renderer>().material = velMaterial;

        // Pressure field visualization.
        if (cf.showPressureField == true)
        {
            GameObject presQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            presQuad.transform.localScale = new Vector3(gridRes.x, gridRes.y, 1);
            presQuad.transform.SetPositionAndRotation(new Vector3(gridRes.x / 2 + 10, 0, 0), Quaternion.Euler(90, 0, 0));

            presTex = new(gridRes.x, gridRes.y);
            presTex.filterMode = FilterMode.Point;

            presTex.SetPixels(pixels);
            presTex.Apply();

            Material presMaterial = new(Shader.Find("HDRP/Lit"));
            presMaterial.SetTexture("_BaseColorMap", presTex);
            presMaterial.SetFloat("_Smoothness", 0);

            presQuad.GetComponent<Renderer>().material = presMaterial;
        }
    }

    // Update is called once per frame
    public void UpdateVis()
    {
        UpdateVelTexture();
        if (cf.showPressureField == true)
            UpdatePresTexture();
    }

    // Polynomial approximation for Turbo color map, found at here:
    // https://gist.github.com/mikhailov-work/0d177465a8151eb6ede1768d51d476c7
    // Which is referenced in the Google research blog:
    // https://research.google/blog/turbo-an-improved-rainbow-colormap-for-visualization/
    float3 TurboColorMap(float x)
    {
        x = Mathf.Clamp01(x);

        float4 v4 = new(1.0f, x, x * x, x * x * x);
        float2 v2 = v4.zw * v4.z;
        float3 result = new(
            math.dot(v4, kRedVec4) + math.dot(v2, kRedVec2),
            math.dot(v4, kGreenVec4) + math.dot(v2, kGreenVec2),
            math.dot(v4, kBlueVec4) + math.dot(v2, kBlueVec2)
        );

        return result;
    }

    void UpdateVelTexture()
    {
        for (int y = 0; y < gridRes.y; y++)
            for (int x = 0; x < gridRes.x; x++)
            {
                float3 v = velFieldVis[x, y, zSlice];
                float speed = math.length(v);
                float t = Mathf.InverseLerp(minVel, maxVel, speed);

                if (colorMap == ColorMap.Linear)
                {
                    velTex.SetPixel(x, y, Color.Lerp(Color.blue, Color.red, t));
                }
                else if (colorMap == ColorMap.Turbo)
                {
                    float3 colorVec = TurboColorMap(t);
                    Color color = new(colorVec.x, colorVec.y, colorVec.z);
                    velTex.SetPixel(x, y, color);
                }

            }
        velTex.Apply();
    }

    void UpdatePresTexture()
    {
        for (int y = 0; y < gridRes.y; y++)
            for (int x = 0; x < gridRes.x; x++)
            {
                float p = presFieldVis[x, y, zSlice];
                float t = Mathf.InverseLerp(minPres, maxPres, p);

                if (colorMap == ColorMap.Linear)
                {
                    presTex.SetPixel(x, y, Color.Lerp(Color.blue, Color.red, t));
                }
                else if (colorMap == ColorMap.Turbo)
                {
                    float3 colorVec = TurboColorMap(t);
                    Color color = new(colorVec.x, colorVec.y, colorVec.z);
                    presTex.SetPixel(x, y, color);
                }

            }
        presTex.Apply();
    }

    public void UpdateQuadPosBySlider(float handlePos)
    {
        throw new NotImplementedException("UpdateQuadPosBySlider is not implemented for CPU visualizer.");
    }

    public void UpdateQuadPosByConfig()
    {
        throw new NotImplementedException("UpdateQuadPosByConfig is not implemented for CPU visualizer.");
    }

    public void UpdateQuadOrientationByConfig()
    {
        throw new NotImplementedException("UpdateQuadOrientationByConfig is not implemented for CPU visualizer.");
    }

    public void UpdateQuadSizeByConfig()
    {
        throw new NotImplementedException("UpdateQuadSizeByConfig is not implemented for CPU visualizer.");
    }

    public void LoadColorMapFromCsv(string csvText)
    {
        throw new NotImplementedException("LoadColorMapFromCsv is not implemented for CPU visualizer.");
    }

    public RenderTexture GetVelDirSliceTex()
    {
        throw new NotImplementedException("GetVelDirSliceTex is not implemented for CPU visualizer.");
    }

    public RenderTexture GetVelMagSliceTex()
    {
        throw new NotImplementedException("GetVelMagSliceTex is not implemented for CPU visualizer.");
    }
}
