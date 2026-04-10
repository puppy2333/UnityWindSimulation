using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using static UnityEngine.Rendering.STP;

public class VisualizerGpu : IVisualizer
{
    FluidSimConfig cf;
    RuntimeConfig rcf;

    // Colormap texture.
    Texture2D colormapTex;
    Color[] colormapArray;

    // Shader for visualizing the fields.
    ComputeShader visShader;

    // Textures for visualizing the fields.
    RenderTexture velMagVisTex;
    RenderTexture velDirSliceTex;
    RenderTexture presVisTex;
    RenderTexture flagVisTex;

    RenderTexture velSimTex;
    RenderTexture presSimTex;
    RenderTexture flagSimTex;
    //Texture3D flagSimTex;

    GameObject velQuad;
    GameObject presQuad;
    GameObject flagQuad;

    Material velMaterial;
    Material presMaterial;
    Material flagMaterial;

    // ----- Face pos arrays for non-uniform grid -----
    float[] facePosXArray;
    float[] facePosYArray;
    float[] facePosZArray;

    // ----- XYZ map texture for non-uniform grid -----
    Texture2D xMapTex;
    Texture2D yMapTex;
    Texture2D zMapTex;

    float[] xMapArray;
    float[] yMapArray;
    float[] zMapArray;

    int xyzMapTexSize = 2048;

    // Shader kernels
    int visVelKernel;
    int visVelDirKernel;
    int visVelDefaultColormapKernel;
    int visVelStagKernel;
    int visPresKernel;
    int visPresDefaultColormapKernel;
    int visFlagKernel;

    // User-defined parameters for fluid simulation.
    float3 physicalDomainSize;
    int gridResX;
    ColorMap colorMap;
    float dx;

    // Visualization parameters.
    int ySliceRunTime;
    float minVel, maxVel;
    float minPres, maxPres;
    float cameraHeight;
    float cameraAngle;

    // Fluid visualization parameters
    int3 gridRes;

    // Model parameters.
    private float3 physFieldPosVis;
    private float3 physDomainSizeVis;

    public VisualizerGpu(
        FluidSimConfig cfIn, RuntimeConfig rcfIn, 
        RenderTexture velIn, RenderTexture presIn, RenderTexture flagIn)
    {
        // Load configuration file and initialize simulation parameters.
        LoadConfig(cfIn, rcfIn);

        // Store the solver to be visualized.
        velSimTex = velIn;
        presSimTex = presIn;
        flagSimTex = flagIn;

        if (cf.grid is not NonUniformGridAuto)
        {
            // ----- Malloc all textures -----
            MallocSliceTex();
            MallocVfxTex();
            MallocColorMapTex();

            if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
                MallocXYZMapTex();

            // Pressure field visualization.
            if (cf.showVelocityField == true)
            {
                InitVelQuad();
            }
            if (cf.showPressureField == true)
            {
                InitPresQuad();
            }
            if (cf.showFlagField == true)
            {
                InitFlagQuad();
            }

            // Find the compute shader kernels.
            ComputeShader visShaderAsset = Resources.Load<ComputeShader>("Shaders/VisShaders/VisShader");
            visShader = UnityEngine.Object.Instantiate(visShaderAsset);

            SetSimShaderParameters();
            RegisterKernels();
        }
    }

    public void LoadConfig(FluidSimConfig config, RuntimeConfig rcfIn)
    {
        cf = config;
        rcf = rcfIn;

        // Init simulation parameters.
        physicalDomainSize = rcf.physDomainSize;
        colorMap = cf.colorMap;
        dx = rcf.dxUnif;
        gridRes = rcf.numCells;

        // Init visualization parameters.
        ySliceRunTime = cf.ySlice;
        minVel = cf.minVel;
        maxVel = cf.maxVel;
        minPres = cf.minPres;
        maxPres = cf.maxPres;
        cameraHeight = cf.cameraHeight;
        cameraAngle = cf.cameraAngle;

        // Model parameters.
        //physFieldPosVis = cf.physFieldPos * cf.visScale + new float3(0, 100, 0);
        physFieldPosVis = rcf.physFieldPos * cf.visScale;
        physDomainSizeVis = rcf.physDomainSize * cf.visScale;

        if (ySliceRunTime < 0 || ySliceRunTime >= gridRes.y)
        {
            ySliceRunTime = gridRes.y / 2;
        }
    }

    void MallocSliceTex()
    {
        if (cf.visInterpolateType == VisInterpolateType.Point)
        {
            velMagVisTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };

            presVisTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
        }
        else
        {
            velMagVisTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear
            };

            presVisTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear
            };
        }

        flagVisTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };

        velMagVisTex.Create();
        presVisTex.Create();
        flagVisTex.Create();
    }

    void MallocVfxTex()
    {
        velDirSliceTex = new(gridRes.x, gridRes.z, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        velDirSliceTex.Create();
    }

    void MallocColorMapTex()
    {
        colormapArray = new Color[256];
    }

    void MallocXYZMapTex()
    {
        xMapTex = new Texture2D(2048, 1, TextureFormat.RFloat, false);
        yMapTex = new Texture2D(2048, 1, TextureFormat.RFloat, false);
        zMapTex = new Texture2D(2048, 1, TextureFormat.RFloat, false);

        xMapTex.filterMode = FilterMode.Point;
        xMapTex.wrapMode = TextureWrapMode.Clamp;
        xMapTex.Apply();

        yMapTex.filterMode = FilterMode.Point;
        yMapTex.wrapMode = TextureWrapMode.Clamp;
        yMapTex.Apply();

        zMapTex.filterMode = FilterMode.Point;
        zMapTex.wrapMode = TextureWrapMode.Clamp;
        zMapTex.Apply();
    }

    public void LoadColorMapFromCsv(string csvText)
    {
        string[] lines = csvText.Split(new char[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            string[] values = line.Split(',');

            float r = 0f;
            float g = 0f;
            float b = 0f;

            bool successR = float.TryParse(values[0], out r);
            bool successG = float.TryParse(values[1], out g);
            bool successB = float.TryParse(values[2], out b);

            if (successR && successG && successB)
            {
                colormapArray[i] = new Color(r, g, b, 1.0f);
            }
            else
            {
                Debug.LogError($"Column {i} data conversion failure {line}");
                colormapArray[i] = Color.magenta;
            }
        }
        //UnityEngine.Debug.Log($"{colormapArray[0].r}, {colormapArray[255].r}");

        InitTurboColormap();
    }

    void InitTurboColormap()
    {
        colormapTex = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        colormapTex.filterMode = FilterMode.Bilinear;
        colormapTex.wrapMode = TextureWrapMode.Clamp;

        colormapTex.SetPixels(colormapArray);
        colormapTex.Apply();

        if (cf.visualizeMode == VisualizeMode.ZeroCopy)
        {
            if (cf.showVelocityField == true)
            {
                velMaterial.SetTexture("_ColorMapTex", colormapTex);
            }
            else
            {
                presMaterial.SetTexture("_ColorMapTex", colormapTex);
            }
        }
    }

    void InitVelQuad()
    {
        // By default, the quad has normal (0, 0, 1) has up (0, 1, 0).
        // First scale it in x and y directions (scale in z direction has no effect), then
        // rotate 90 degrees around X-axis to make it face up.
        velQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);

        Collider collider = velQuad.GetComponent<Collider>();
        if (collider != null)
        {
            GameObject.Destroy(collider);
        }

        velQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        velQuad.transform.SetPositionAndRotation(physFieldPosVis, Quaternion.Euler(90, rcf.flowFieldOrientation, 0));        

        if (cf.visualizeMode == VisualizeMode.Copy)
        {
            velMaterial = new(Shader.Find("HDRP/Unlit"));
            velMaterial.SetTexture("_UnlitColorMap", velMagVisTex);
        }
        else
        {
            if (cf.grid is UniformGrid)
            {
                velMaterial = new Material(Shader.Find("Custom/VisShaderVel"));
            }
            else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            {
                velMaterial = new Material(Shader.Find("Custom/VisShaderVelNonUnif"));
                velMaterial.SetTexture("_XMapTex", xMapTex);
                velMaterial.SetTexture("_YMapTex", yMapTex);
                velMaterial.SetTexture("_ZMapTex", zMapTex);
            }
            velMaterial.SetTexture("_VelSimTex", velSimTex);
        }

        velQuad.GetComponent<Renderer>().material = velMaterial;

        Debug.Log($"velQuad.transform.localScale: {velQuad.transform.localScale}");
    }

    void InitPresQuad()
    {
        // Move the pressure quad to the left if velocity quad exists.
        float3 posPresQuad = physFieldPosVis;
        if (cf.showVelocityField == true)
        {
            posPresQuad += new float3(physDomainSizeVis.x + 2, 0, 0);
        }

        // By default, the quad has normal (0, 0, 1) has up (0, 1, 0). Rotate it 90 degrees around X-axis to make it face up.
        presQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);

        Collider collider = presQuad.GetComponent<Collider>();
        if (collider != null)
        {
            GameObject.Destroy(collider);
        }

        presQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        presQuad.transform.SetPositionAndRotation(posPresQuad, Quaternion.Euler(90, rcf.flowFieldOrientation, 0));

        if (cf.visualizeMode == VisualizeMode.Copy)
        {
            presMaterial = new(Shader.Find("HDRP/Unlit"));
            presMaterial.SetTexture("_UnlitColorMap", presVisTex);
        }
        else
        {
            presMaterial = new(Shader.Find("Custom/VisShaderPres"));
            presMaterial.SetTexture("_PresSimTex", presSimTex);
        }

        presQuad.GetComponent<Renderer>().material = presMaterial;
    }

    void InitFlagQuad()
    {
        // Move the flag quad to the left if velocity quad exists.
        float3 posFlagQuad = physFieldPosVis;
        if (cf.showVelocityField == true)
        {
            posFlagQuad -= new float3(physDomainSizeVis.x + 2, 0, 0);
        }

        // By default, the quad has normal (0, 0, 1) has up (0, 1, 0). Rotate it 90 degrees around X-axis to make it face up.
        flagQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);

        Collider collider = flagQuad.GetComponent<Collider>();
        if (collider != null)
        {
            GameObject.Destroy(collider);
        }

        flagQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        flagQuad.transform.SetPositionAndRotation(posFlagQuad, Quaternion.Euler(90, rcf.flowFieldOrientation, 0));

        if (cf.visualizeMode == VisualizeMode.Copy)
        {
            flagMaterial = new(Shader.Find("HDRP/Unlit"));
            flagMaterial.SetTexture("_UnlitColorMap", flagVisTex);
        }
        else
        {
            flagMaterial = new(Shader.Find("Custom/VisShaderFlag"));
            //flagMaterial.SetTexture("_FlagSimTex", flagSimTex);
            flagMaterial.SetTexture("_FlagSimTex", flagSimTex);
        }

        flagQuad.GetComponent<Renderer>().material = flagMaterial;
    }

    void SetSimShaderParameters()
    {
        visShader.SetInts("sliceRes", gridRes.x, gridRes.z);
        visShader.SetFloat("minVel", minVel);
        visShader.SetFloat("maxVel", maxVel);
        visShader.SetFloat("minPres", minPres);
        visShader.SetFloat("maxPres", maxPres);
        visShader.SetInt("ySlice", ySliceRunTime);

        if (cf.visualizeMode == VisualizeMode.ZeroCopy)
        {
            if (cf.showVelocityField == true)
            {
                velMaterial.SetFloat("_MinVel", minVel);
                velMaterial.SetFloat("_MaxVel", maxVel);
            }
            if (cf.showPressureField == true)
            {
                presMaterial.SetFloat("_MinPres", minPres);
                presMaterial.SetFloat("_MaxPres", maxPres);
            }
        }
    }

    void RegisterKernels()
    {
        visVelKernel = visShader.FindKernel("CSVisVel");
        visVelDirKernel = visShader.FindKernel("CSVisVelDir");
        visVelDefaultColormapKernel = visShader.FindKernel("CSVisVelDefaultColormap");
        visVelStagKernel = visShader.FindKernel("CSVisVelStag");
        visPresKernel = visShader.FindKernel("CSVisPres");
        visPresDefaultColormapKernel = visShader.FindKernel("CSVisPresDefaultColormap");
        visFlagKernel = visShader.FindKernel("CSVisFlag");
    }

    // ----- This function is not called if visualization mode is zero-copy -----
    public void UpdateVis()
    {
        //Debug.Assert(velSimTex != null, "Velocity simulation texture is null.");
        //Debug.Assert(presSimTex != null, "Pressure simulation texture is null.");
        //Debug.Assert(flagSimTex != null, "Flag simulation texture is null.");

        if (cf.showVelocityField == true)
        {
            if (cf.colormapLoaded)
            {
                visShader.SetTexture(visVelKernel, "velSim", velSimTex);
                visShader.SetTexture(visVelKernel, "velVis", velMagVisTex);
                visShader.SetTexture(visVelKernel, "colormapTex", colormapTex);
                visShader.Dispatch(visVelKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
            }
            else
            {
                visShader.SetTexture(visVelDefaultColormapKernel, "velSim", velSimTex);
                visShader.SetTexture(visVelDefaultColormapKernel, "velVis", velMagVisTex);
                visShader.Dispatch(visVelDefaultColormapKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
            }
        }
        if (cf.showPressureField == true)
        {
            if (cf.colormapLoaded)
            {
                visShader.SetTexture(visPresKernel, "presSim", presSimTex);
                visShader.SetTexture(visPresKernel, "presVis", presVisTex);
                visShader.SetTexture(visPresKernel, "colormapTex", colormapTex);
                visShader.Dispatch(visPresKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
            }
            else
            {
                visShader.SetTexture(visPresDefaultColormapKernel, "presSim", presSimTex);
                visShader.SetTexture(visPresDefaultColormapKernel, "presVis", presVisTex);
                visShader.Dispatch(visPresDefaultColormapKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
            }
            
        }
        if (cf.showFlagField == true)
        {
            visShader.SetTexture(visFlagKernel, "flagSim", flagSimTex);
            visShader.SetTexture(visFlagKernel, "flagVis", flagVisTex);
            visShader.Dispatch(visFlagKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
        }

        if (cf.showVfx == true)
        {
            visShader.SetTexture(visVelDirKernel, "velSim", velSimTex);
            visShader.SetTexture(visVelDirKernel, "velVisDir", velDirSliceTex);
            visShader.Dispatch(visVelDirKernel, (gridRes.x + 7) / 8, (gridRes.z + 7) / 8, 1);
        }

        //UnityEngine.Debug.Log("Vis called");
    }

    public void UpdateQuadPosBySlider(float handlePos)
    {
        float quadPhysPosY = physFieldPosVis.y + handlePos * physDomainSizeVis.y;

        //UnityEngine.Debug.Log($"handlePos: {handlePos}, quadPhysPosY: {quadPhysPosY}");

        if (cf.showVelocityField == true)
        {
            velQuad.transform.position = new Vector3(physFieldPosVis.x, quadPhysPosY, physFieldPosVis.z);
        }
        if (cf.showPressureField == true)
        {
            Vector3 oldPresQuadPos = presQuad.transform.position;
            presQuad.transform.position = new Vector3(oldPresQuadPos.x, quadPhysPosY, oldPresQuadPos.z);
        }
        if (cf.showFlagField == true)
        {
            Vector3 oldFlagQuadPos = flagQuad.transform.position;
            flagQuad.transform.position = new Vector3(oldFlagQuadPos.x, quadPhysPosY, oldFlagQuadPos.z);
        }

        if (cf.visualizeMode == VisualizeMode.Copy || cf.showVfx == true)
        {
            ySliceRunTime = Mathf.RoundToInt(handlePos * gridRes.y);
            Mathf.Clamp(ySliceRunTime, 0, gridRes.y - 1);
            visShader.SetInt("ySlice", ySliceRunTime);
        }
        
        if (cf.visualizeMode == VisualizeMode.ZeroCopy)
        {
            if (cf.showVelocityField == true)
                velMaterial.SetFloat("_YSlice", handlePos);
            if (cf.showPressureField == true)
                presMaterial.SetFloat("_YSlice", handlePos);
            if (cf.showFlagField == true)
                flagMaterial.SetFloat("_YSlice", handlePos);
        }
    }

    public void UpdateQuadPosByConfig()
    {
        physFieldPosVis = rcf.physFieldPos * cf.visScale;
        float3 quadPhysPos = physFieldPosVis;
        quadPhysPos.y = (physFieldPosVis.y - physDomainSizeVis.y / 2.0f) + ySliceRunTime * dx;

        if (cf.showVelocityField == true)
        {
            velQuad.transform.position = quadPhysPos;
        }
        if (cf.showPressureField == true)
        {
            Vector3 posPresQuad = quadPhysPos;
            if (cf.showVelocityField == true)
            {
                posPresQuad += new Vector3(physDomainSizeVis.x + 2, 0, 0);
            }
            presQuad.transform.position = posPresQuad;
        }
        if (cf.showFlagField == true)
        {
            Vector3 posFlagQuad = quadPhysPos;
            if (cf.showVelocityField == true)
            {
                posFlagQuad -= new Vector3(physDomainSizeVis.x + 2, 0, 0);
            }
            flagQuad.transform.position = posFlagQuad;
        }
    }

    public void UpdateQuadOrientationByConfig()
    {
        if (cf.showVelocityField == true)
        {
            velQuad.transform.SetPositionAndRotation(velQuad.transform.position, Quaternion.Euler(90, rcf.flowFieldOrientation, 0));
        }
    }

    public void UpdateQuadSizeByConfig()
    {
        physDomainSizeVis = rcf.physDomainSize * cf.visScale;

        if (cf.showVelocityField == true)
        {
            velQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        }
        if (cf.showPressureField == true)
        {
            presQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        }
        if (cf.showFlagField == true)
        {
            flagQuad.transform.localScale = new Vector3(physDomainSizeVis.x, physDomainSizeVis.z, 1);
        }
    }

    public void AddNonUnifLayers(FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        // Load configuration file and initialize simulation parameters.
        LoadConfig(cfIn, rcfIn);

        // ----- Malloc all textures -----
        MallocSliceTex();
        MallocVfxTex();
        MallocColorMapTex();

        MallocXYZMapTex();

        //physFieldPosVis += new float3(0.5f * rcf.physDomainSize.x - rcf.unifRegionPhysStartOffset.x - 0.5f * rcf.unifRegionPhysDomainSize.x, 0, 0);

        // Pressure field visualization.
        if (cf.showVelocityField == true)
        {
            InitVelQuad();
        }
        if (cf.showPressureField == true)
        {
            InitPresQuad();
        }
        if (cf.showFlagField == true)
        {
            InitFlagQuad();
        }

        // Find the compute shader kernels.
        ComputeShader visShaderAsset = Resources.Load<ComputeShader>("Shaders/VisShaders/VisShader");
        visShader = UnityEngine.Object.Instantiate(visShaderAsset);

        SetSimShaderParameters();
        RegisterKernels();
    }

    public RenderTexture GetVelDirSliceTex()
    {
        return velDirSliceTex;
    }

    public RenderTexture GetVelMagSliceTex()
    {
        return velMagVisTex;
    }

    void FillLUT(Texture2D lut, float[] facePosArrayNorm)
    {
        // ----- The LUT array on CPU -----
        float[] lutArray = new float[xyzMapTexSize];

        // ----- The face index used in the last LUT pixel query -----
        int jLast = 0;

        // ----- For each LUT pixel -----
        for (int i = 0; i < xyzMapTexSize; i++)
        {
            float physical_uv = (float)i / (xyzMapTexSize - 1);

            int cellIdx = 0;
            for (int j = jLast; j < facePosArrayNorm.Length - 1; j++)
            {
                if (physical_uv >= facePosArrayNorm[j] && physical_uv <= facePosArrayNorm[j + 1])
                {
                    jLast = j;
                    cellIdx = j;
                    break;
                }
            }
            
            lutArray[i] = (cellIdx + 0.5f) / (facePosArrayNorm.Length - 1);
        }

        lut.SetPixelData(lutArray, 0);

        lut.Apply();
    }

    public void SetFacePosArrays(float[] facePosXArrayIn, float[] facePosYArrayIn, float[] facePosZArrayIn)
    {
        // ----- Face pos x array -----
        facePosXArray = facePosXArrayIn;

        // Normalize the face position array to [0, 1]
        float[] facePosXArrayNorm = new float[facePosXArray.Length];
        for (int i = 0; i < facePosXArray.Length; i++)
            facePosXArrayNorm[i] = facePosXArray[i] / facePosXArray[^1];

        FillLUT(xMapTex, facePosXArrayNorm);

        // ----- Face pos y array -----
        facePosYArray = facePosYArrayIn;

        // Normalize the face position array to [0, 1]
        float[] facePosYArrayNorm = new float[facePosYArray.Length];
        for (int i = 0; i < facePosYArray.Length; i++)
            facePosYArrayNorm[i] = facePosYArray[i] / facePosYArray[^1];

        FillLUT(yMapTex, facePosYArrayNorm);

        // ----- Face pos z array -----
        facePosZArray = facePosZArrayIn;

        // Normalize the face position array to [0, 1]
        float[] facePosZArrayNorm = new float[facePosZArray.Length];
        for (int i = 0; i < facePosZArray.Length; i++)
            facePosZArrayNorm[i] = facePosZArray[i] / facePosZArray[^1];

        FillLUT(zMapTex, facePosZArrayNorm);
    }

    public void SetTextures(RenderTexture velSimTexIn, RenderTexture presSimTexIn, RenderTexture flagSimTexIn)
    {
        velSimTex = velSimTexIn;
        presSimTex = presSimTexIn;
        flagSimTex = flagSimTexIn;
    }
}
