using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

public class FvmSolverGpu : IFvmSolver
{
    private enum CurrSolverStage
    {
        Idle,
        LES,
        VelPredict, // LES + velocity prediction step.
        PresCorrect, // Pressure correction step.
        Finished
    }

    #region SimulationConfig
    // User-defined parameters for fluid simulation, passed from the configuration file.
    FluidSimConfig cf;
    RuntimeConfig rcf;

    private Vector3 externalForce;

    // Fluid simulation parameters, will be derived from user-defined parameters.
    private int3 gridRes;
    #endregion

    #region Shaders
    public ComputeShader initShader;
    public ComputeShader fvmShader;
    public ComputeShader pisoShader;
    public ComputeShader lesShader;
    public ComputeShader utilsShader;
    public ComputeShader wallFuncShader;
    #endregion

    #region Textures
    // Textures fluid field, 3 fields for both velocity and pressure are the minimum for Jacobi iteration.
    private RenderTexture velFieldTex;
    private RenderTexture velFieldLastTimeTex;
    private RenderTexture velFieldLastIterTex;
    private RenderTexture velCorrectFieldTex;

    private RenderTexture presFieldLastTimeTex;
    private RenderTexture presCorrectFieldTex;
    private RenderTexture presCorrectFieldLastIterTex;

    // Velocity flux at cell faces, used in Rhie-Chow interpolation (currently only implemented in
    // non-uniform grids).
    private RenderTexture massFluxFieldTex;

    // ----- Flag textures -----
    private RenderTexture flagTex;
    // Help buffer that transforms flags from CPU to GPU.
    private ComputeBuffer flagBuffer;
    // CPU-side flag array.
    private int[] flagArray;

    // Diagonal term of A, and b in Ax = b for velocity prediction in SIMPLE.
    private RenderTexture DFieldTex;
    private RenderTexture bFieldTex;
    // Diagonal term of A, and b in Ax = b for pressure correction in SIMPLE.
    private RenderTexture DFieldPresCorrectTex;
    private RenderTexture bFieldPresCorrectTex;
    // Multiplication of off-diagonal component of the velocity prediction coefficient matrix and
    // velocity correction. Used in PISO.
    private RenderTexture AodUCorrectFieldTex;

    // LES textures.
    private RenderTexture eddyVisFieldTex;
    private RenderTexture faceEddyVisFieldTex;

    // Mesh face position buffers. Start from 0, faceXPosBuffer[0] = 0, faceXPosBuffer[1] = dx[0],
    // ... (not physical position)
    private ComputeBuffer facePosXBuf;
    private ComputeBuffer facePosYBuf;
    private ComputeBuffer facePosZBuf;

    float[] facePosXArray;
    float[] facePosYArray;
    float[] facePosZArray;

    // For calculating residual.
    int gloResBufSize;
    private ComputeBuffer gloResBuf;
    private float[] gloResBufCpu;

    // For calculating CFL.
    int gloCflBufSize;
    private ComputeBuffer gloCflBuf;
    private float[] gloCflBufCpu;

    // ----- Boundary condition textures -----
    private RenderTexture bndX0Tex;
    private RenderTexture bndXnTex;
    private RenderTexture bndY0Tex;
    private RenderTexture bndYnTex;
    private RenderTexture bndZ0Tex;
    private RenderTexture bndZnTex;

    // Kernel settings
    private int3 numGroups;
    #endregion

    #region ShaderKernels
    // Compute shader kernel indices.
    // ----- Initialization kernels -----
    int initBoxFlagKernel;
    int initModelFlagKernel;

    int initVelPresFieldsKernel;
    int initLogVelPresFieldsKernel;
    int initVelPresFieldsFromBGFlowKernel;
    int initFlagFieldKernel;
    int initPresFieldKernel;
    int initVelFieldKernel;

    int setZeroVelBndCondKernel;
    int initJetFlowBndCondKernel;
    int setFixedValueVelBndCondKernel;
    int setLogVelBndCondKernel;

    int flagGpuToCpuKernel;

    int initFaceEddyVisFieldKernel;

    // ----- SIMPLE kernels -----
    int neumannPresBndCondKernel;
    int applyPresCorrectionKernel;
    int presNormalizationKernel;
    int applyVelCorrectionKernel;

    // ----- SIMPLE kernels (accelerated) -----
    int velPredictPreComputeKernel;
    int velPredictKernel;
    int presCorrectPreComputeKernel;
    int presCorrectKernel;

    // ----- PISO kernels -----
    int pisoCalAodUCorrectKernel;
    int pisoPresCorrectionKernel;
    int pisoApplyPresCorrectionKernel;
    int pisoVelCorrectionKernel;

    // ----- LES kernels -----
    int eddyVisKernel;
    int lesDeferCorrectTermKernel;
    int calLogLawWallFuncKernel;

    // ----- Residual calculation kernels -----
    int computeVelPredictResidualKernel;
    int computePresCorrectResidualKernel;
    int calCflKernel;

    // ----- Utility shader kernel -----
    int copyVelFieldKernel;
    int setPresFieldKernel;
    #endregion

    #region solverState
    CurrSolverStage currSolverStage = CurrSolverStage.Idle;
    int currItersInCurrSim = 0;
    bool readyForRender = true;
    float maxCfl = 0f;
    #endregion

    #region StopWatch
    private Stopwatch stopwatch = new();
    #endregion

    #region InitFuncs
    public FvmSolverGpu(FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        // Load from configuration file.
        LoadConfig(cfIn, rcfIn);

        if (cf.grid is not NonUniformGridAuto)
        {
            // Malloc the velocity and pressure textures.
            MallocTextures();

            // Initialize shaders and kernels.
            InitInitShader();

            if (cf.grid is UniformGrid)
                InitFvmShader();
            else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
                InitFvmNonUniformShader();

            InitUtilsShader();
            if (cf.fvmSolverType == FvmSolverType.PISO)
                InitPisoShader();
            if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
            {
                InitLESShader();
                InitWallFuncShader();
            }

            // Print grid information.
            UnityEngine.Debug.Log($"Grid Resolution: {gridRes.x} x {gridRes.y} x {gridRes.z}");
            UnityEngine.Debug.Log($"CFL condition (should be less than 1): {cf.velX0.x * cf.dt / rcf.dxUnif}");

            // Init mesh.
            if (cf.grid is NonUniformGridDefault)
                GenNonUnifMesh();

            // Init flag field.
            InitFlagField();

            // Init pressure and velocity fields (vel field must be inited after flag field).
            InitVelPresFields();

            if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
                InitFaceEddyVisField();

            if (cf.solidType == SolidType.Box)
            {
                if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
                    CalcBoxPos();
                InitBox();
            }

            // ----- Set boundary conditions -----
            SetZeroVelBndCond();
            SetFixedValueVelBndCond();
        }
    }

    void LoadConfig(FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        // Load configuration file.
        cf = cfIn;
        rcf = rcfIn;
        externalForce = new(cf.externalForce.x, cf.externalForce.y, cf.externalForce.z);

        gridRes = rcf.numCells;

        numGroups = new int3((gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    RenderTexture CreateRenderTexture3D(int3 dim, RenderTextureFormat format)
    {
        RenderTexture tex = new(dim.x, dim.y, 0, format)
        {
            volumeDepth = dim.z,
            enableRandomWrite = true,
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            //filterMode = FilterMode.Point
            filterMode = FilterMode.Trilinear
        };
        tex.Create();
        return tex;
    }

    RenderTexture CreateRenderTexture2D(int2 dim, RenderTextureFormat format)
    {
        RenderTexture tex = new(dim.x, dim.y, 0, format)
        {
            enableRandomWrite = true,
            dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
            filterMode = FilterMode.Point
        };
        tex.Create();
        return tex;
    }

    void MallocTextures()
    {
        // ----- SIMPLE textures -----
        velFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);
        velFieldLastTimeTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);
        velFieldLastIterTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);
        velCorrectFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);

        presFieldLastTimeTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);
        presCorrectFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);
        presCorrectFieldLastIterTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);

        massFluxFieldTex = CreateRenderTexture3D(gridRes + new int3(1, 1, 1), RenderTextureFormat.ARGBFloat);

        flagTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RInt);

        DFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);
        bFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);
        DFieldPresCorrectTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);
        bFieldPresCorrectTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);

        // ----- PISO Texturees -----
        if (cf.fvmSolverType == FvmSolverType.PISO)
            AodUCorrectFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.ARGBFloat);

        // ----- Residual textures -----
        gloResBufSize = numGroups.x * numGroups.y * numGroups.z;
        gloResBufCpu = new float[gloResBufSize];
        gloResBuf = new ComputeBuffer(gloResBufSize, sizeof(float));
        gloResBuf.SetData(gloResBufCpu);

        // ----- CFL textures -----
        gloCflBufSize = numGroups.x * numGroups.y * numGroups.z;
        gloCflBufCpu = new float[gloCflBufSize];
        gloCflBuf = new ComputeBuffer(gloCflBufSize, sizeof(float));
        gloCflBuf.SetData(gloCflBufCpu);

        // ----- Boundary textures -----
        bndX0Tex = CreateRenderTexture2D(new int2(gridRes.y, gridRes.z), RenderTextureFormat.ARGBFloat);
        bndXnTex = CreateRenderTexture2D(new int2(gridRes.y, gridRes.z), RenderTextureFormat.ARGBFloat);
        bndY0Tex = CreateRenderTexture2D(new int2(gridRes.x, gridRes.z), RenderTextureFormat.ARGBFloat);
        bndYnTex = CreateRenderTexture2D(new int2(gridRes.x, gridRes.z), RenderTextureFormat.ARGBFloat);
        bndZ0Tex = CreateRenderTexture2D(new int2(gridRes.x, gridRes.y), RenderTextureFormat.ARGBFloat);
        bndZnTex = CreateRenderTexture2D(new int2(gridRes.x, gridRes.y), RenderTextureFormat.ARGBFloat);

        // ----- LES textures -----
        eddyVisFieldTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RFloat);
        faceEddyVisFieldTex = CreateRenderTexture3D(gridRes + new int3(1, 1, 1), RenderTextureFormat.RFloat);
    }

    void InitInitShader()
    {
        // Load shader file.
        ComputeShader initShaderAsset = Resources.Load<ComputeShader>("Shaders/InitShaders/InitShaderColl");
        initShader = UnityEngine.Object.Instantiate(initShaderAsset);

        // ----- Set shader parameters (general) -----
        initShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);

        // ----- Set shader parameters (bnd conds) -----
        initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
        initShader.SetFloats("velZn", cf.velZn.x, cf.velZn.y, cf.velZn.z);

        // ----- Set shader parameters (solid) -----
        initShader.SetInts("boxStartIdx", cf.boxStartIdx.x, cf.boxStartIdx.y, cf.boxStartIdx.z);
        initShader.SetInts("boxEndIdx", cf.boxEndIdx.x, cf.boxEndIdx.y, cf.boxEndIdx.z);

        // ----- Set shader parameters (fluid field) -----
        initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);

        // ----- Register kernels (fluid field) -----
        initVelPresFieldsKernel = initShader.FindKernel("CSInitVelPresFields");
        if (cf.grid is UniformGrid)
            initLogVelPresFieldsKernel = initShader.FindKernel("CSInitLogVelPresFields");
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            initLogVelPresFieldsKernel = initShader.FindKernel("CSInitLogVelPresFieldsNonUniform");
        initVelPresFieldsFromBGFlowKernel = initShader.FindKernel("CSInitVelPresFieldsFromBGFlowRotate");

        // ----- Register kernels (bnd conds) -----
        setZeroVelBndCondKernel = initShader.FindKernel("CSSetZeroVelBndCond");
        setFixedValueVelBndCondKernel = initShader.FindKernel("CSSetFixedValueVelBndCond");
        if (cf.grid is UniformGrid)
            setLogVelBndCondKernel = initShader.FindKernel("CSSetLogVelBndCond");
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            setLogVelBndCondKernel = initShader.FindKernel("CSSetLogVelBndCondNonUniform");

        // ----- Register kernels (flag field) -----
        initFlagFieldKernel = initShader.FindKernel("CSInitFlagField");
        initBoxFlagKernel = initShader.FindKernel("CSInitBoxFlag");
        initModelFlagKernel = initShader.FindKernel("CSInitModelFlag");
        flagGpuToCpuKernel = initShader.FindKernel("CSFlagGpuToCpu");
    }

    void InitFvmShader()
    {
        // Load shader file.
        if (cf.grid is UniformGrid)
        {
            ComputeShader fvmShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmColl");
            fvmShader = UnityEngine.Object.Instantiate(fvmShaderAsset);
        }
        else
        {
            throw new NotImplementedException("No FVM shader implemented for current grid type.");
        }

        // Set shader parameters.
        fvmShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        fvmShader.SetFloat("dt", cf.dt);
        fvmShader.SetFloat("dx", rcf.dxUnif);
        fvmShader.SetFloat("ds", rcf.dsUnif);
        fvmShader.SetFloat("dv", rcf.dvUnif);
        fvmShader.SetFloat("nu", cf.nu);
        fvmShader.SetFloat("den", cf.den);
        fvmShader.SetVector("externalForce", externalForce);
        fvmShader.SetFloat("dirichletVelX", cf.dirichletVelX);
        fvmShader.SetInts("numGroups", numGroups.x, numGroups.y, numGroups.z);
        fvmShader.SetBool("useLES", cf.turbulenceModel == TurbulenceModel.Smagorinsky);

        fvmShader.SetInt("velBndTypeX0", (int)cf.velBndCondX0);
        fvmShader.SetInt("velBndTypeXn", (int)cf.velBndCondXn);
        fvmShader.SetInt("velBndTypeY0", (int)cf.velBndCondY0);
        fvmShader.SetInt("velBndTypeYn", (int)cf.velBndCondYn);
        fvmShader.SetInt("velBndTypeZ0", (int)cf.velBndCondZ0);
        fvmShader.SetInt("velBndTypeZn", (int)cf.velBndCondZn);

        fvmShader.SetInt("presBndTypeX0", (int)cf.presBndCondX0);
        fvmShader.SetInt("presBndTypeXn", (int)cf.presBndCondXn);
        fvmShader.SetInt("presBndTypeY0", (int)cf.presBndCondY0);
        fvmShader.SetInt("presBndTypeYn", (int)cf.presBndCondYn);
        fvmShader.SetInt("presBndTypeZ0", (int)cf.presBndCondZ0);
        fvmShader.SetInt("presBndTypeZn", (int)cf.presBndCondZn);

        // Register kernels.
        if (cf.grid is UniformGrid)
        {
            // ----- SIMPLE kernels (vel predict) -----
            if (cf.convectScheme == ConvectScheme.CDS)
            {
                velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictPreCompute");
                velPredictKernel = fvmShader.FindKernel("CSVelPredict");
            }
            else if (cf.convectScheme == ConvectScheme.LUST)
            {
                velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictLUSTPreCompute");
                velPredictKernel = fvmShader.FindKernel("CSVelPredictLUST");
            }
            // ----- SIMPLE kernels (pres correct) -----
            presCorrectPreComputeKernel = fvmShader.FindKernel("CSPresCorrectRhieChowPreCompute");
            presCorrectKernel = fvmShader.FindKernel("CSPresCorrectRhieChow");
            // ----- SIMPLE kernels (apply correction) -----
            applyPresCorrectionKernel = fvmShader.FindKernel("CSApplyPresCorrection");
            presNormalizationKernel = fvmShader.FindKernel("CSPresNormalization");
            //applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrection");
            applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrectionDivergenceTheorem");

            // ----- Tolerence kernels -----
            computeVelPredictResidualKernel = fvmShader.FindKernel("CSComputeVelPredictResidual");
            computePresCorrectResidualKernel = fvmShader.FindKernel("CSComputePresCorrectResidual");
            
            // ----- CFL kernel -----
            calCflKernel = fvmShader.FindKernel("CSCalCfl");
        }
        else
        {
            throw new NotImplementedException("No FVM shader implemented for current grid type.");
        }
    }

    void InitFvmNonUniformShader()
    {
        // Load shader file.
        ComputeShader fvmShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmCollNonUniform");
        //ComputeShader fvmShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmColl2");
        fvmShader = UnityEngine.Object.Instantiate(fvmShaderAsset);

        // Set shader parameters.
        fvmShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        fvmShader.SetFloat("dt", cf.dt);
        fvmShader.SetFloat("nu", cf.nu);
        fvmShader.SetFloat("den", cf.den);
        fvmShader.SetVector("externalForce", externalForce);
        fvmShader.SetInts("numGroups", numGroups.x, numGroups.y, numGroups.z);
        fvmShader.SetBool("useLES", cf.turbulenceModel == TurbulenceModel.Smagorinsky);

        fvmShader.SetInt("velBndTypeX0", (int)cf.velBndCondX0);
        fvmShader.SetInt("velBndTypeXn", (int)cf.velBndCondXn);
        fvmShader.SetInt("velBndTypeY0", (int)cf.velBndCondY0);
        fvmShader.SetInt("velBndTypeYn", (int)cf.velBndCondYn);
        fvmShader.SetInt("velBndTypeZ0", (int)cf.velBndCondZ0);
        fvmShader.SetInt("velBndTypeZn", (int)cf.velBndCondZn);

        fvmShader.SetInt("presBndTypeX0", (int)cf.presBndCondX0);
        fvmShader.SetInt("presBndTypeXn", (int)cf.presBndCondXn);
        fvmShader.SetInt("presBndTypeY0", (int)cf.presBndCondY0);
        fvmShader.SetInt("presBndTypeYn", (int)cf.presBndCondYn);
        fvmShader.SetInt("presBndTypeZ0", (int)cf.presBndCondZ0);
        fvmShader.SetInt("presBndTypeZn", (int)cf.presBndCondZn);

        // Register kernels.
        // ----- SIMPLE kernels (accelerated) -----
        if (cf.convectScheme == ConvectScheme.CDS)
        {
            velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictPreCompute");
            velPredictKernel = fvmShader.FindKernel("CSVelPredict");
        }
        else if (cf.convectScheme == ConvectScheme.LUST)
        {
            velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictLUSTPreCompute");
            velPredictKernel = fvmShader.FindKernel("CSVelPredictLUST");
        }
        presCorrectPreComputeKernel = fvmShader.FindKernel("CSPresCorrectRhieChowPreCompute");
        presCorrectKernel = fvmShader.FindKernel("CSPresCorrectRhieChow");

        applyPresCorrectionKernel = fvmShader.FindKernel("CSApplyPresCorrection");
        presNormalizationKernel = fvmShader.FindKernel("CSPresNormalization");
        applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrectionDivergenceTheorem");

        // ----- Tolerence kernels -----
        computeVelPredictResidualKernel = fvmShader.FindKernel("CSComputeVelPredictResidual");
        computePresCorrectResidualKernel = fvmShader.FindKernel("CSComputePresCorrectResidual");

        // ---- CFL kernel -----
        calCflKernel = fvmShader.FindKernel("CSCalCfl");
    }

    void InitPisoShader()
    {
        // Load shader file.
        if (cf.grid is UniformGrid)
            pisoShader = Resources.Load<ComputeShader>("Shaders/SimShaders/PisoColl");
        else
            throw new NotImplementedException("No Piso algorithm implemented for staggered grid.");

        // Set shader parameters.
        pisoShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        pisoShader.SetFloat("dt", cf.dt);
        pisoShader.SetFloat("dx", rcf.dxUnif);
        pisoShader.SetFloat("ds", rcf.dsUnif);
        pisoShader.SetFloat("dv", rcf.dvUnif);
        pisoShader.SetFloat("nu", cf.nu);
        pisoShader.SetFloat("den", cf.den);
        pisoShader.SetVector("externalForce", externalForce);

        // Register kernels.
        if (cf.grid is UniformGrid)
        {
            pisoCalAodUCorrectKernel = pisoShader.FindKernel("CSPisoCalAodUCorrect");
            pisoPresCorrectionKernel = pisoShader.FindKernel("CSPisoPresCorrection");
            pisoApplyPresCorrectionKernel = pisoShader.FindKernel("CSPisoApplyPresCorrection");
            pisoVelCorrectionKernel = pisoShader.FindKernel("CSPisoVelCorrection");
        }
        else
        {
            throw new NotImplementedException("No Piso algorithm implemented for current grid type.");
        }
    }

    void InitLESShader()
    {
        if (cf.grid is UniformGrid)
        {
            ComputeShader lesShaderAsset = Resources.Load<ComputeShader>("Shaders/LESShaders/Les");
            lesShader = UnityEngine.Object.Instantiate(lesShaderAsset);

            lesShader.SetFloat("dx", rcf.dxUnif);
            lesShader.SetFloat("ds", rcf.dsUnif);
        }
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            ComputeShader lesShaderAsset = Resources.Load<ComputeShader>("Shaders/LESShaders/LesNonUniform");
            lesShader = UnityEngine.Object.Instantiate(lesShaderAsset);
        }

        // Set shader parameters.
        lesShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        lesShader.SetFloat("nu", cf.nu);
        lesShader.SetFloat("cs", cf.smagorinskyConstant);

        lesShader.SetInt("velBndTypeX0", (int)cf.velBndCondX0);
        lesShader.SetInt("velBndTypeXn", (int)cf.velBndCondXn);
        lesShader.SetInt("velBndTypeY0", (int)cf.velBndCondY0);
        lesShader.SetInt("velBndTypeYn", (int)cf.velBndCondYn);
        lesShader.SetInt("velBndTypeZ0", (int)cf.velBndCondZ0);
        lesShader.SetInt("velBndTypeZn", (int)cf.velBndCondZn);

        lesShader.SetInt("presBndTypeX0", (int)cf.presBndCondX0);
        lesShader.SetInt("presBndTypeXn", (int)cf.presBndCondXn);
        lesShader.SetInt("presBndTypeY0", (int)cf.presBndCondY0);
        lesShader.SetInt("presBndTypeYn", (int)cf.presBndCondYn);
        lesShader.SetInt("presBndTypeZ0", (int)cf.presBndCondZ0);
        lesShader.SetInt("presBndTypeZn", (int)cf.presBndCondZn);

        // Register kernels.
        eddyVisKernel = lesShader.FindKernel("CSCalEddyVis");
        lesDeferCorrectTermKernel = lesShader.FindKernel("CSCalLesDeferCorrectTerm");
    }

    void InitWallFuncShader()
    {
        if (cf.grid is UniformGrid)
        {
            ComputeShader wallFuncShaderAsset = Resources.Load<ComputeShader>("Shaders/WallFuncShaders/WallFunc");
            wallFuncShader = UnityEngine.Object.Instantiate(wallFuncShaderAsset);

            wallFuncShader.SetFloat("dx", rcf.dxUnif);
            wallFuncShader.SetFloat("ds", rcf.dsUnif);
        }
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            ComputeShader wallFuncShaderAsset = Resources.Load<ComputeShader>("Shaders/WallFuncShaders/WallFuncNonUniform");
            wallFuncShader = UnityEngine.Object.Instantiate(wallFuncShaderAsset);
        }

        // Set shader parameters.
        wallFuncShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        wallFuncShader.SetFloat("nu", cf.nu);
        wallFuncShader.SetFloat("den", cf.den);
        wallFuncShader.SetFloat("cs", cf.smagorinskyConstant);

        wallFuncShader.SetInt("velBndTypeX0", (int)cf.velBndCondX0);
        wallFuncShader.SetInt("velBndTypeXn", (int)cf.velBndCondXn);
        wallFuncShader.SetInt("velBndTypeY0", (int)cf.velBndCondY0);
        wallFuncShader.SetInt("velBndTypeYn", (int)cf.velBndCondYn);
        wallFuncShader.SetInt("velBndTypeZ0", (int)cf.velBndCondZ0);
        wallFuncShader.SetInt("velBndTypeZn", (int)cf.velBndCondZn);

        wallFuncShader.SetInt("presBndTypeX0", (int)cf.presBndCondX0);
        wallFuncShader.SetInt("presBndTypeXn", (int)cf.presBndCondXn);
        wallFuncShader.SetInt("presBndTypeY0", (int)cf.presBndCondY0);
        wallFuncShader.SetInt("presBndTypeYn", (int)cf.presBndCondYn);
        wallFuncShader.SetInt("presBndTypeZ0", (int)cf.presBndCondZ0);
        wallFuncShader.SetInt("presBndTypeZn", (int)cf.presBndCondZn);

        // Register kernels.
        calLogLawWallFuncKernel = wallFuncShader.FindKernel("CSCalLogLawWallFunc");
    }

    void InitUtilsShader()
    {
        // Load shader file.
        ComputeShader utilsShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/Utils");
        utilsShader = UnityEngine.Object.Instantiate(utilsShaderAsset);

        // Set shader parameters.
        utilsShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);

        // Register kernels.
        copyVelFieldKernel = utilsShader.FindKernel("CSCopyVelField");
        setPresFieldKernel = utilsShader.FindKernel("CSSetPresField");
    }

    void GenNonUnifMesh()
    {
        // ----- Malloc GPU buffers and CPU arrays, +1 for face positions -----
        facePosXBuf = new ComputeBuffer(gridRes.x + 1, sizeof(float));
        facePosYBuf = new ComputeBuffer(gridRes.y + 1, sizeof(float));
        facePosZBuf = new ComputeBuffer(gridRes.z + 1, sizeof(float));

        facePosXArray = new float[gridRes.x + 1];
        facePosYArray = new float[gridRes.y + 1];
        facePosZArray = new float[gridRes.z + 1];

        UnityEngine.Debug.Log($"Non-uniform region 0 num cells: {rcf.nonUnifRegion0NumCells}");
        UnityEngine.Debug.Log($"Uniform region num cells: {rcf.unifRegionNumCells}");
        UnityEngine.Debug.Log($"Non-uniform region N num cells: {rcf.nonUnifRegionNNumCells}");
        UnityEngine.Debug.Log($"Total num cells: {rcf.numCells}");

        // ----- Cell length array, from object to, e.g. x0 domain boundary.
        // Pattern: [dx, 1.08 * dx, 1.08^2 * dx, ...]. -----
        float[] x0GridSizeArray = new float[rcf.nonUnifRegion0NumCells.x];
        float[] xnGridSizeArray = new float[rcf.nonUnifRegionNNumCells.x];
        float[] y0GridSizeArray = new float[rcf.nonUnifRegion0NumCells.y];
        float[] ynGridSizeArray = new float[rcf.nonUnifRegionNNumCells.y];
        float[] z0GridSizeArray = new float[rcf.nonUnifRegion0NumCells.z];
        float[] znGridSizeArray = new float[rcf.nonUnifRegionNNumCells.z];

        if (x0GridSizeArray.Length > 0)
        {
            x0GridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < x0GridSizeArray.Length; i++)
                x0GridSizeArray[i] = x0GridSizeArray[i - 1] * rcf.stretchFactor.x;
        }
        if (xnGridSizeArray.Length > 0)
        {
            xnGridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < xnGridSizeArray.Length; i++)
                xnGridSizeArray[i] = xnGridSizeArray[i - 1] * rcf.stretchFactor.x;
        }
        if (y0GridSizeArray.Length > 0)
        {
            y0GridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < y0GridSizeArray.Length; i++)
                y0GridSizeArray[i] = y0GridSizeArray[i - 1] * rcf.stretchFactor.y;
        }
        if (ynGridSizeArray.Length > 0)
        {
            ynGridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < ynGridSizeArray.Length; i++)
                ynGridSizeArray[i] = ynGridSizeArray[i - 1] * rcf.stretchFactor.y;
        }
        if (z0GridSizeArray.Length > 0)
        {
            z0GridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < z0GridSizeArray.Length; i++)
                z0GridSizeArray[i] = z0GridSizeArray[i - 1] * rcf.stretchFactor.z;
        }
        if (znGridSizeArray.Length > 0)
        {
            znGridSizeArray[0] = rcf.dxUnif;
            for (int i = 1; i < znGridSizeArray.Length; i++)
                znGridSizeArray[i] = znGridSizeArray[i - 1] * rcf.stretchFactor.z;
        }

        // ----- Fill in x face position arrays, 0 - physDomainSize.x -----
        facePosXArray[0] = 0;
        for (int x = 1; x < x0GridSizeArray.Length + 1; x++) // Face number = cell number + 1
            facePosXArray[x] = facePosXArray[x - 1] + x0GridSizeArray[x0GridSizeArray.Length - x];

        for (int x = x0GridSizeArray.Length + 1; x < x0GridSizeArray.Length + rcf.unifRegionNumCells.x + 1; x++)
            facePosXArray[x] = facePosXArray[x - 1] + rcf.dxUnif;

        for (int x = x0GridSizeArray.Length + rcf.unifRegionNumCells.x + 1; x < gridRes.x + 1; x++)
        {
            //UnityEngine.Debug.Log($"x: {x}, index in xn array: {x - (x0GridSizeArray.Length + cf.unifRegionNumCells.x + 1)}");
            facePosXArray[x] = facePosXArray[x - 1] + xnGridSizeArray[x - (x0GridSizeArray.Length + rcf.unifRegionNumCells.x + 1)];
        }

        // ----- Fill in y face position arrays -----
        facePosYArray[0] = 0;
        for (int y = 1; y < y0GridSizeArray.Length + 1; y++) // Face number = cell number + 1
            facePosYArray[y] = facePosYArray[y - 1] + y0GridSizeArray[y0GridSizeArray.Length - y];

        for (int y = y0GridSizeArray.Length + 1; y < y0GridSizeArray.Length + rcf.unifRegionNumCells.y + 1; y++)
            facePosYArray[y] = facePosYArray[y - 1] + rcf.dxUnif;

        for (int y = y0GridSizeArray.Length + rcf.unifRegionNumCells.y + 1; y < gridRes.y + 1; y++)
            facePosYArray[y] = facePosYArray[y - 1] + ynGridSizeArray[y - (y0GridSizeArray.Length + rcf.unifRegionNumCells.y + 1)];

        // ----- Fill in the z face position arrays -----
        facePosZArray[0] = 0;
        for (int z = 1; z < z0GridSizeArray.Length + 1; z++) // Face number = cell number + 1
            facePosZArray[z] = facePosZArray[z - 1] + z0GridSizeArray[z0GridSizeArray.Length - z];

        for (int z = z0GridSizeArray.Length + 1; z < z0GridSizeArray.Length + rcf.unifRegionNumCells.z + 1; z++)
            facePosZArray[z] = facePosZArray[z - 1] + rcf.dxUnif;

        for (int z = z0GridSizeArray.Length + rcf.unifRegionNumCells.z + 1; z < gridRes.z + 1; z++)
            facePosZArray[z] = facePosZArray[z - 1] + znGridSizeArray[z - (z0GridSizeArray.Length + rcf.unifRegionNumCells.z + 1)];

        UnityEngine.Debug.Log(string.Join(", ", facePosXArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosYArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosZArray));

        facePosXBuf.SetData(facePosXArray);
        facePosYBuf.SetData(facePosYArray);
        facePosZBuf.SetData(facePosZArray);
    }

    void CalcBoxPos()
    {
        float delta = 1e-4f;

        int3 boxStartIdx = new int3(-1, -1, -1);
        int3 boxEndIdx = new int3(-1, -1, -1);

        UnityEngine.Debug.Log($"Box physical offset: {rcf.boxStartPhysOffset}, {rcf.boxEndPhysOffset}");

        for (int x = 0; x < gridRes.x; x++)
        {
            if (Mathf.Abs(facePosXArray[x] - rcf.boxStartPhysOffset.x) <= delta)
            {
                boxStartIdx.x = x;
            }

            if (Mathf.Abs(facePosXArray[x] - rcf.boxEndPhysOffset.x) <= delta)
            {
                boxEndIdx.x = x;
            }
        }

        for (int y = 0; y < gridRes.y; y++)
        {
            if (Mathf.Abs(facePosYArray[y] - rcf.boxStartPhysOffset.y) <= delta)
            {
                boxStartIdx.y = y;
            }

            if (Mathf.Abs(facePosYArray[y] - rcf.boxEndPhysOffset.y) <= delta)
            {
                boxEndIdx.y = y;
            }
        }

        for (int z = 0; z < gridRes.z; z++)
        {
            if (Mathf.Abs(facePosZArray[z] - rcf.boxStartPhysOffset.z) <= delta)
            {
                boxStartIdx.z = z;
            }

            if (Mathf.Abs(facePosZArray[z] - rcf.boxEndPhysOffset.z) <= delta)
            {
                boxEndIdx.z = z;
            }
        }

        if (boxStartIdx.x == -1 || boxEndIdx.x == -1 || 
            boxStartIdx.y == -1 || boxEndIdx.y == -1 || 
            boxStartIdx.z == -1 || boxEndIdx.z == -1)
        {
            throw new Exception("Box start/end physical offset does not align with any face " +
                "position, please adjust the offsets or the grid resolution.");
        }

        cf.boxStartIdx = boxStartIdx;
        cf.boxEndIdx = boxEndIdx;

        UnityEngine.Debug.Log($"Box start idx: {cf.boxStartIdx}, box end idx: {cf.boxEndIdx}");
    }

    public void InitVelPresFields()
    {
        if (cf.grid is UniformGrid)
        {
            if (cf.inflowType == InflowType.Constant)
            {
                // ----- ReInit wind speed -----
                initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);

                // Set the textures in the compute shader.
                initShader.SetTexture(initVelPresFieldsKernel, "velField", velFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velFieldLastTime", velFieldLastTimeTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velFieldLastIter", velFieldLastIterTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velCorrectField", velCorrectFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presFieldLastTime", presFieldLastTimeTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presCorrectField", presCorrectFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                initShader.SetTexture(initVelPresFieldsKernel, "flagField", flagTex);

                // Init all fields to zero.
                initShader.Dispatch(initVelPresFieldsKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
            }
            else if (cf.inflowType == InflowType.Log)
            {
                // ----- ReInit wind speed -----
                initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);
                initShader.SetFloat("dx", rcf.dxUnif);
                initShader.SetFloat("karmanConst", 0.4f);
                initShader.SetFloat("roughParam", 1.0f);
                initShader.SetFloat("refHeight", 10.0f);

                // Set the textures in the compute shader.
                initShader.SetTexture(initLogVelPresFieldsKernel, "velField", velFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velFieldLastTime", velFieldLastTimeTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velFieldLastIter", velFieldLastIterTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velCorrectField", velCorrectFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presFieldLastTime", presFieldLastTimeTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectField", presCorrectFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "flagField", flagTex);

                // Init all fields to zero.
                initShader.Dispatch(initLogVelPresFieldsKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
            }
        }
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            if (cf.inflowType == InflowType.Constant)
            {
                // ----- ReInit wind speed -----
                initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);

                // Set the textures in the compute shader.
                initShader.SetTexture(initVelPresFieldsKernel, "velField", velFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velFieldLastTime", velFieldLastTimeTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velFieldLastIter", velFieldLastIterTex);
                initShader.SetTexture(initVelPresFieldsKernel, "velCorrectField", velCorrectFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presFieldLastTime", presFieldLastTimeTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presCorrectField", presCorrectFieldTex);
                initShader.SetTexture(initVelPresFieldsKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                initShader.SetTexture(initVelPresFieldsKernel, "flagField", flagTex);

                // Init all fields to zero.
                initShader.Dispatch(initVelPresFieldsKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
            }
            else if (cf.inflowType == InflowType.Log)
            {
                // ----- ReInit wind speed -----
                initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);
                initShader.SetFloat("karmanConst", 0.4f);
                initShader.SetFloat("roughParam", 1.0f);
                initShader.SetFloat("refHeight", 10.0f);
                // ----- Set textures (RW) -----
                initShader.SetTexture(initLogVelPresFieldsKernel, "velField", velFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velFieldLastTime", velFieldLastTimeTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velFieldLastIter", velFieldLastIterTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "velCorrectField", velCorrectFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "massFluxField", massFluxFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presFieldLastTime", presFieldLastTimeTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectField", presCorrectFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "flagField", flagTex);
                // ----- Set mesh buffers (read-only) -----
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosX", facePosXBuf);
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosY", facePosYBuf);
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosZ", facePosZBuf);
                // Init all fields to zero.
                initShader.Dispatch(initLogVelPresFieldsKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
            }
        }
        else
        {
            throw new NotImplementedException("InitVelPresFields not implemented for current grid type.");
        }
    }

    public void InitVelPresFieldsFromBackGroundFlow(FluidSimConfig cfBG, RuntimeConfig rcfBG, RenderTexture velTexBG, RenderTexture presTexBG)
    {
        if (cf.grid is UniformGrid or NonUniformGridDefault)
        {
            // ----- Set shader parameters (re-init interpolation) -----
            initShader.SetFloat("dxFG", rcf.dxUnif);
            initShader.SetFloat("dxBG", rcfBG.dxUnif);
            initShader.SetFloats("physFieldPosFG", rcf.physFieldPos.x, rcf.physFieldPos.y, rcf.physFieldPos.z);
            initShader.SetFloats("physFieldPosBG", rcfBG.physFieldPos.x, rcfBG.physFieldPos.y, rcfBG.physFieldPos.z);
            initShader.SetFloat("fieldRotAng", rcf.flowFieldOrientation * (float)(3.1415926 / 180));
            initShader.SetInts("gridResBG", rcfBG.numCells.x, rcfBG.numCells.y, rcfBG.numCells.z);

            // Set the textures in the compute shader.
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velField", velFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldLastTime", velFieldLastTimeTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldLastIter", velFieldLastIterTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velCorrectField", velCorrectFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presFieldLastTime", presFieldLastTimeTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presCorrectField", presCorrectFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "flagField", flagTex);

            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldBG", velTexBG);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presFieldBG", presTexBG);

            // Init all fields to zero.
            initShader.Dispatch(initVelPresFieldsFromBGFlowKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
        }
        else
        {
            throw new NotImplementedException("Init from background flow not implemented for staggered grid.");
        }

        currSolverStage = CurrSolverStage.Idle;
        currItersInCurrSim = 0;
    }

    void InitFlagField()
    {
        initShader.SetTexture(initFlagFieldKernel, "flagField", flagTex);
        initShader.Dispatch(initFlagFieldKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void InitFaceEddyVisField()
    {
        initShader.SetTexture(initFaceEddyVisFieldKernel, "faceEddyVisField", faceEddyVisFieldTex);
        initShader.Dispatch(initFaceEddyVisFieldKernel, (gridRes.x + 8) / 8, (gridRes.y + 8) / 8, (gridRes.z + 8) / 8);
    }

    int _GetCellIdx(int3 id)
    {
        return id.z * (gridRes.y * gridRes.x) + id.y * gridRes.x + id.x;
    }

    void flagFieldGpuToCpu()
    {
        flagBuffer ??= new ComputeBuffer(gridRes.x * gridRes.y * gridRes.z, sizeof(int));
        initShader.SetTexture(flagGpuToCpuKernel, "flagField", flagTex);
        initShader.SetBuffer(flagGpuToCpuKernel, "flagFieldBuffer", flagBuffer);
        initShader.Dispatch(flagGpuToCpuKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        flagArray = new int[gridRes.x * gridRes.y * gridRes.z];
        flagBuffer.GetData(flagArray);
    }

    void InitBox()
    {
        initShader.SetTexture(initBoxFlagKernel, "flagField", flagTex);
        initShader.SetTexture(initBoxFlagKernel, "velField", velFieldTex);
        initShader.SetTexture(initBoxFlagKernel, "velFieldLastIter", velFieldLastIterTex);
        initShader.SetTexture(initBoxFlagKernel, "velFieldLastTime", velFieldLastTimeTex);
        initShader.Dispatch(initBoxFlagKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    public void InitFlags(int[] flags)
    {
        flagBuffer ??= new ComputeBuffer(gridRes.x * gridRes.y * gridRes.z, sizeof(int));
        flagBuffer.SetData(flags);

        initShader.SetTexture(initModelFlagKernel, "flagField", flagTex);
        initShader.SetBuffer(initModelFlagKernel, "flagFieldBuffer", flagBuffer);
        initShader.SetTexture(initModelFlagKernel, "velField", velFieldTex);
        initShader.SetTexture(initModelFlagKernel, "velFieldLastIter", velFieldLastIterTex);
        initShader.SetTexture(initModelFlagKernel, "velFieldLastTime", velFieldLastTimeTex);
        initShader.Dispatch(initModelFlagKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        currSolverStage = CurrSolverStage.Idle;
        currItersInCurrSim = 0;
        readyForRender = false;
    }

    public void InitNonUnifGridFlags(int[] flagsNonUnif)
    {
        flagBuffer ??= new ComputeBuffer(gridRes.x * gridRes.y * gridRes.z, sizeof(int));

        //int[] flagsNonUnif = new int[gridRes.x * gridRes.y * gridRes.z];

        //for (int z = 0; z < rcf.unifRegionNumCells.z; z++)
        //    for (int y = 0; y < rcf.unifRegionNumCells.y; y++)
        //    {
        //        int idx1Start = (rcf.unifRegionNumCells.x * rcf.unifRegionNumCells.y) * z + rcf.unifRegionNumCells.x * y;

        //        int idx2Start = (rcf.numCells.x * rcf.numCells.y) * (rcf.nonUnifRegion0NumCells.z + z) + rcf.numCells.x * (rcf.nonUnifRegion0NumCells.y + y) + rcf.nonUnifRegion0NumCells.x;

        //        Array.Copy(flags, idx1Start, flagsNonUnif, idx2Start, rcf.unifRegionNumCells.x);
        //    }

        //FillNonUnifRegions(flags, flagsNonUnif);

        flagBuffer.SetData(flagsNonUnif);

        initShader.SetTexture(initModelFlagKernel, "flagField", flagTex);
        initShader.SetBuffer(initModelFlagKernel, "flagFieldBuffer", flagBuffer);
        initShader.SetTexture(initModelFlagKernel, "velField", velFieldTex);
        initShader.SetTexture(initModelFlagKernel, "velFieldLastIter", velFieldLastIterTex);
        initShader.SetTexture(initModelFlagKernel, "velFieldLastTime", velFieldLastTimeTex);
        initShader.Dispatch(initModelFlagKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        currSolverStage = CurrSolverStage.Idle;
        currItersInCurrSim = 0;
        readyForRender = false;
    }

    public void FillNonUnifRegions(int[] flags, int[] flagsNonUnif)
    {
        // ----- Extract the height information from flags -----
        int smallSizeX = rcf.unifRegionNumCells.x;
        int smallSizeY = rcf.unifRegionNumCells.y;
        int smallSizeZ = rcf.unifRegionNumCells.z;

        int bigSizeX = rcf.numCells.x;
        int bigSizeY = rcf.numCells.y;
        int bigSizeZ = rcf.numCells.z;

        int offsetX = rcf.nonUnifRegion0NumCells.x;
        int offsetY = rcf.nonUnifRegion0NumCells.y;
        int offsetZ = rcf.nonUnifRegion0NumCells.z;

        int[] smallHeights = new int[smallSizeX * smallSizeZ];

        for (int z = 0; z < smallSizeZ; z++)
            for (int x = 0; x < smallSizeX; x++)
            {
                int heightIndex = smallSizeX * z + x;
                smallHeights[heightIndex] = 0;

                for (int y = smallSizeY - 1; y >= 0; y--)
                {
                    int idx = (smallSizeX * smallSizeY) * z + smallSizeX * y + x;
                    if (flags[idx] == 1)
                    {
                        smallHeights[heightIndex] = y;
                        break;
                    }
                }
            }

        // ----- Nearest neighbor assignment -----
        for (int z = 0; z < bigSizeZ; z++)
        {
            for (int x = 0; x < bigSizeX; x++)
            {
                bool isInsideX = (x >= offsetX && x < offsetX + smallSizeX);
                bool isInsideZ = (z >= offsetZ && z < offsetZ + smallSizeZ);

                if (!isInsideX || !isInsideZ)
                {
                    int nearestX = Math.Max(offsetX, Math.Min(x, offsetX + smallSizeX - 1));
                    int nearestZ = Math.Max(offsetZ, Math.Min(z, offsetZ + smallSizeZ - 1));

                    int localX = nearestX - offsetX;
                    int localZ = nearestZ - offsetZ;
                    int localHeightY = smallHeights[smallSizeX * localZ + localX];

                    // 对大数组该 (x, z) 的整根 Y 轴柱体进行赋值：绝对高度及以下赋 1，以上赋 0
                    for (int y = 0; y < bigSizeY; y++)
                    {
                        int idx = (bigSizeX * bigSizeY) * z + bigSizeX * y + x;
                        flagsNonUnif[idx] = (y <= localHeightY) ? 1 : 0;
                    }
                }
            }
        }
    }

    void SetZeroVelBndCond()
    {
        SetBndTex(initShader, setZeroVelBndCondKernel);

        initShader.Dispatch(setZeroVelBndCondKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    public void SetFixedValueVelBndCond()
    {
        if (cf.inflowType == InflowType.Constant)
        {
            initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
            SetBndTex(initShader, setFixedValueVelBndCondKernel);
            initShader.Dispatch(setFixedValueVelBndCondKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
        }
        else if (cf.inflowType == InflowType.Log)
        {
            initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
            initShader.SetFloat("dx", rcf.dxUnif);
            initShader.SetFloat("karmanConst", 0.4f);
            initShader.SetFloat("roughParam", 1.0f);
            initShader.SetFloat("refHeight", 10.0f);

            if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            {
                SetFacePosTex(initShader, setLogVelBndCondKernel);
            }

            SetBndTex(initShader, setLogVelBndCondKernel);
            initShader.Dispatch(setLogVelBndCondKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
        }
    }
    #endregion

    #region ReinitializeFuns
    public void ChangePhysFieldPos()
    {
        // Init pressure and velocity fields (vel field must be inited after flag field).
        InitVelPresFields();
        currSolverStage = CurrSolverStage.Idle;
        currItersInCurrSim = 0;
    }

    public void ChangePhysDomainSize()
    {
        // ----- Reload from the configuration file -----
        gridRes = rcf.numCells;
        numGroups = new int3((gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        // ----- Reinitialize fields -----

        // Init pressure and velocity fields (vel field must be inited after flag field).
        InitVelPresFields();
    }

    public void InitNonUnifGridAuto(FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        // Load from configuration file.
        LoadConfig(cfIn, rcfIn);

        // Malloc the velocity and pressure textures.
        MallocTextures();

        // Initialize shaders and kernels.
        InitInitShader();

        if (cf.grid is UniformGrid)
            InitFvmShader();
        else if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            InitFvmNonUniformShader();

        InitUtilsShader();
        if (cf.fvmSolverType == FvmSolverType.PISO)
            InitPisoShader();
        if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
        {
            InitLESShader();
            InitWallFuncShader();
        }

        // Print grid information.
        UnityEngine.Debug.Log($"Grid Resolution: {gridRes.x} x {gridRes.y} x {gridRes.z}");
        UnityEngine.Debug.Log($"CFL condition (should be less than 1): {cf.velX0.x * cf.dt / rcf.dxUnif}");

        // Init mesh.
        GenNonUnifMesh();

        // Init flag field.
        InitFlagField();

        // Init pressure and velocity fields (vel field must be inited after flag field).
        InitVelPresFields();

        if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
            InitFaceEddyVisField();

        if (cf.solidType == SolidType.Box)
        {
            if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
                CalcBoxPos();
            InitBox();
        }

        // ----- Set boundary conditions -----
        SetZeroVelBndCond();
        SetFixedValueVelBndCond();
    }
    #endregion

    #region SolverFuncs
    public bool Step()
    {
        stopwatch.Restart();

        if (currSolverStage == CurrSolverStage.Idle)
        {
            readyForRender = false;
            currSolverStage = CurrSolverStage.LES;
            currItersInCurrSim = 0;
        }

        // ----- LES -----
        if (currSolverStage == CurrSolverStage.LES)
        {
            if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
            {
                CalWallFunc();
                CalLesTerms();
            }
            currSolverStage = CurrSolverStage.VelPredict;
        }

        // ----- SIMPLE algorithm -----
        if (currSolverStage == CurrSolverStage.VelPredict)
        {
            bool stepFinished = VelPredict();
            if (stepFinished)
            {
                currSolverStage = CurrSolverStage.PresCorrect;
                currItersInCurrSim = 0;
            }
        }
        if (currSolverStage == CurrSolverStage.PresCorrect)
        {
            bool stepFinished = PresCorrect();
            if (stepFinished)
            {
                ApplyVelCorrection();
                SetPresCorrectFieldtoZero();
                currSolverStage = CurrSolverStage.Finished;
                currItersInCurrSim = 0;
            }
        }

        // ----- PISO algorithm (not in the stage management yet) -----
        if (cf.fvmSolverType == FvmSolverType.PISO)
        {
            for (int n = 0; n < cf.PISONumCorrectors; n++)
            {
                PisoCorrection();
            }
        }

        // ----- Post-processing -----
        if (currSolverStage == CurrSolverStage.Finished)
        {
            if (cf.calMaxCfl && cf.currSimStep % 10 == 0)
            {
                _ComputeMaxCfl();
            }

            (velFieldLastTimeTex, velFieldTex) = (velFieldTex, velFieldLastTimeTex);
            CopyVelField(velFieldLastIterTex, velFieldLastTimeTex);

            cf.currPhyTime += cf.dt;
            cf.currSimStep += 1;

            if (cf.showSimulationTime)
            {
                UnityEngine.Debug.Log($"Simulated physical time: {cf.currPhyTime:F3} seconds, consumed time: {stopwatch.ElapsedMilliseconds} ms.");
            }

            readyForRender = true;
            currSolverStage = CurrSolverStage.Idle;
        }

        return readyForRender;
    }

    void CalLesTerms()
    {
        lesShader.SetTexture(eddyVisKernel, "velFieldLastTime", velFieldLastTimeTex);
        lesShader.SetTexture(eddyVisKernel, "eddyVisField", eddyVisFieldTex);
        lesShader.SetTexture(eddyVisKernel, "flagField", flagTex); // Read-only in les shader
        SetBndTex(lesShader, eddyVisKernel);
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(lesShader, eddyVisKernel);
        }
        lesShader.Dispatch(eddyVisKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        lesShader.SetTexture(lesDeferCorrectTermKernel, "velFieldLastTime", velFieldLastTimeTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "eddyVisField", eddyVisFieldTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only in les shader
        lesShader.SetTexture(lesDeferCorrectTermKernel, "bField", bFieldTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "flagField", flagTex); // Read-only in les shader
        SetBndTex(lesShader, lesDeferCorrectTermKernel);
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(lesShader, lesDeferCorrectTermKernel);
        }
        lesShader.Dispatch(lesDeferCorrectTermKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void CalWallFunc()
    {
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "velFieldLastTime", velFieldLastTimeTex); // Read-only in wall func shader
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "faceEddyVisField", faceEddyVisFieldTex);
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "flagField", flagTex); // Read-only in wall func shader
        SetBndTex(wallFuncShader, calLogLawWallFuncKernel);
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(wallFuncShader, calLogLawWallFuncKernel);
        }
        wallFuncShader.Dispatch(calLogLawWallFuncKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void _VelPredictPreCompute()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(fvmShader, velPredictPreComputeKernel);
        }

        fvmShader.SetTexture(velPredictPreComputeKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "massFluxField", massFluxFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "flagField", flagTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "DField", DFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "bField", bFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "eddyVisField", eddyVisFieldTex); // Read-only in fvm shader
        fvmShader.SetTexture(velPredictPreComputeKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only in les shader
        SetBndTex(fvmShader, velPredictPreComputeKernel);
        fvmShader.Dispatch(velPredictPreComputeKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void _VelPredict()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(fvmShader, velPredictKernel);
        }

        fvmShader.SetTexture(velPredictKernel, "velField", velFieldTex);
        fvmShader.SetTexture(velPredictKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(velPredictKernel, "velFieldLastIter", velFieldLastIterTex);
        fvmShader.SetTexture(velPredictKernel, "massFluxField", massFluxFieldTex);
        fvmShader.SetTexture(velPredictKernel, "flagField", flagTex);
        fvmShader.SetTexture(velPredictKernel, "DField", DFieldTex);
        fvmShader.SetTexture(velPredictKernel, "bField", bFieldTex);
        fvmShader.SetTexture(velPredictKernel, "eddyVisField", eddyVisFieldTex); // Read-only
        fvmShader.SetTexture(velPredictKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only
        fvmShader.Dispatch(velPredictKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    float _ComputeVelPredictResidual()
    {
        fvmShader.SetTexture(computeVelPredictResidualKernel, "velField", velFieldTex);
        fvmShader.SetTexture(computeVelPredictResidualKernel, "velFieldLastIter", velFieldLastIterTex);
        fvmShader.SetTexture(computeVelPredictResidualKernel, "DField", DFieldTex);
        fvmShader.SetBuffer(computeVelPredictResidualKernel, "gloResBuf", gloResBuf);
        fvmShader.Dispatch(computeVelPredictResidualKernel, numGroups.x, numGroups.y, numGroups.z);
        gloResBuf.GetData(gloResBufCpu);

        // Sum the residuals.
        float residual = 0;
        for (int i = 0; i < gloResBufSize; i++)
        {
            residual += gloResBufCpu[i];
        }

        residual /= (3 * gridRes.x * gridRes.y * gridRes.z); // Normalization
        residual = Mathf.Sqrt(residual); // L2 norm

        return residual;
    }

    void _ComputeMaxCfl()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
            SetFacePosTex(fvmShader, calCflKernel);

        fvmShader.SetTexture(calCflKernel, "velField", velFieldTex);
        fvmShader.SetBuffer(calCflKernel, "gloCflBuf", gloCflBuf);
        fvmShader.Dispatch(calCflKernel, numGroups.x, numGroups.y, numGroups.z);

        int currStep = cf.currSimStep;

        AsyncGPUReadback.Request(gloCflBuf, request =>
        {
            if (request.hasError)
            {
                UnityEngine.Debug.LogError("GPU CFL buffer readback error");
                return;
            }

            var cflData = request.GetData<float>();
            float maxCflCurrTimeStep = 0;
            for (int i = 0; i < cflData.Length; i++)
                maxCflCurrTimeStep = Mathf.Max(maxCflCurrTimeStep, cflData[i]);

            maxCfl = Mathf.Max(maxCfl, maxCflCurrTimeStep);
            UnityEngine.Debug.Log($"Max CFL at step {currStep}: {maxCflCurrTimeStep:F6}, Overall Max CFL: {maxCfl:F6}");
        });
    }

    bool VelPredict()
    {
        // ----- Vel prediction precompute -----
        if (currItersInCurrSim == 0)
            _VelPredictPreCompute();

        for (int k = currItersInCurrSim; k < cf.velMaxNumIter; k++)
        {
            // ----- Solver -----
            _VelPredict();

            // ----- Calculate residual -----
            if (cf.calResidual)
            {
                if (k % cf.velResidualCheckInterval == 0)
                {
                    float residual = _ComputeVelPredictResidual();

                    if (residual < cf.velTolerance)
                    {
                        //UnityEngine.Debug.Log($"Residual of vel predict iteration {k}: {residual}");
                        break;
                    }
                }
            }

            // Swap the current and last iteration velocity fields.
            (velFieldLastIterTex, velFieldTex) = (velFieldTex, velFieldLastIterTex);

            // ----- Remain time check -----
            if (k % 40 == 0)
            {
                if (stopwatch.ElapsedMilliseconds >= 30)
                {
                    currItersInCurrSim = k + 1;
                    return false;
                }
            }
        }

        // Ensure up to date value is stored in "<quantity>Field", not "<quantity>FieldLastIter".
        (velFieldLastIterTex, velFieldTex) = (velFieldTex, velFieldLastIterTex);

        return true;
    }

    void _PresCorrectPreCompute()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(fvmShader, presCorrectPreComputeKernel);
        }
        fvmShader.SetTexture(presCorrectPreComputeKernel, "velField", velFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "massFluxField", massFluxFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "flagField", flagTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "DField", DFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "bFieldPresCorrect", bFieldPresCorrectTex);
        SetBndTex(fvmShader, presCorrectPreComputeKernel);
        fvmShader.Dispatch(presCorrectPreComputeKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void _PresCorrectCollAccelerated()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(fvmShader, presCorrectKernel);
        }
        fvmShader.SetTexture(presCorrectKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(presCorrectKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
        fvmShader.SetTexture(presCorrectKernel, "flagField", flagTex);
        fvmShader.SetTexture(presCorrectKernel, "DField", DFieldTex);
        fvmShader.SetTexture(presCorrectKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        fvmShader.SetTexture(presCorrectKernel, "bFieldPresCorrect", bFieldPresCorrectTex);
        fvmShader.Dispatch(presCorrectKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    float _ComputePresCorrectResidual()
    {
        fvmShader.SetTexture(computePresCorrectResidualKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(computePresCorrectResidualKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
        fvmShader.SetTexture(computePresCorrectResidualKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        fvmShader.SetBuffer(computePresCorrectResidualKernel, "gloResBuf", gloResBuf);
        fvmShader.Dispatch(computePresCorrectResidualKernel, numGroups.x, numGroups.y, numGroups.z);
        gloResBuf.GetData(gloResBufCpu);

        // Sum the residuals.
        float residual = 0;
        for (int i = 0; i < gloResBufSize; i++)
        {
            residual += gloResBufCpu[i];
        }

        residual /= (gridRes.x * gridRes.y * gridRes.z); // Normalization
        residual = Mathf.Sqrt(residual); // L2 norm

        return residual;
    }

    bool PresCorrect()
    {
        if (currItersInCurrSim == 0)
            _PresCorrectPreCompute();

        for (int k = currItersInCurrSim; k < cf.presMaxNumIter; k++)
        {
            // ----- Solve pressure correction kernel -----
            _PresCorrectCollAccelerated();

            // Calculate residual
            if (cf.calResidual)
            {
                if (k % cf.presResidualCheckInterval == 0)
                {
                    float residual = _ComputePresCorrectResidual();
                    //UnityEngine.Debug.Log($"Residual of pres correct iteration {k}: {residual}");

                    if (residual < cf.presTolerance)
                    {
                        //UnityEngine.Debug.Log($"Residual of pres correct iteration {k}: {residual}");
                        break;
                    }
                }
            }

            // Swap the current and last iteration pressure correction fields.
            (presCorrectFieldLastIterTex, presCorrectFieldTex) = (presCorrectFieldTex, presCorrectFieldLastIterTex);

            // ----- Time check -----
            if (k % 40 == 0)
            {
                if (stopwatch.ElapsedMilliseconds >= 30)
                {
                    currItersInCurrSim = k;
                    return false;
                }
            }
        }

        // Ensure up to date value is stored in "<quantity>Field", not "<quantity>FieldLastIter".
        (presCorrectFieldLastIterTex, presCorrectFieldTex) = (presCorrectFieldTex, presCorrectFieldLastIterTex);

        // ----- Update pressure field using correction -----
        fvmShader.SetTexture(applyPresCorrectionKernel, "presFieldLastTime", presFieldLastTimeTex); 
        fvmShader.SetTexture(applyPresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(applyPresCorrectionKernel, "flagField", flagTex);
        fvmShader.Dispatch(applyPresCorrectionKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
        // ----- End of update pressure field using correction -----

        // Don't apply pressure normalization if fixed value pressure bnd cond is used.
        if (!(
            cf.presBndCondX0 == PresBndCond.FixedValue || 
            cf.presBndCondXn == PresBndCond.FixedValue || 
            cf.presBndCondY0 == PresBndCond.FixedValue ||
            cf.presBndCondYn == PresBndCond.FixedValue ||
            cf.presBndCondZ0 == PresBndCond.FixedValue ||
            cf.presBndCondZn == PresBndCond.FixedValue))
        {
            fvmShader.SetTexture(presNormalizationKernel, "presFieldLastTime", presFieldLastTimeTex);
            fvmShader.SetTexture(presNormalizationKernel, "flagField", flagTex);
            fvmShader.Dispatch(presNormalizationKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
        }

        return true;
    }

    void ApplyVelCorrection()
    {
        if (cf.grid is NonUniformGridAuto or NonUniformGridDefault)
        {
            SetFacePosTex(fvmShader, applyVelCorrectionKernel);
        }
        fvmShader.SetTexture(applyVelCorrectionKernel, "velField", velFieldTex);
        fvmShader.SetTexture(applyVelCorrectionKernel, "velCorrectField", velCorrectFieldTex);
        fvmShader.SetTexture(applyVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(applyVelCorrectionKernel, "massFluxField", massFluxFieldTex);
        fvmShader.SetTexture(applyVelCorrectionKernel, "flagField", flagTex);
        fvmShader.SetTexture(applyVelCorrectionKernel, "DField", DFieldTex);

        fvmShader.Dispatch(applyVelCorrectionKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void PisoCorrection()
    {
        // ----- Calculate AodUCorrectField -----
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "AodUCorrectField", AodUCorrectFieldTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "velFieldLastTime", velFieldLastTimeTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "velCorrectField", velCorrectFieldTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "flagField", flagTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "DField", DFieldTex);
        pisoShader.Dispatch(pisoCalAodUCorrectKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        // ----- Compute pressure correction -----
        for (int k = 0; k < cf.presMaxNumIter; k++)
        {
            if (cf.grid is UniformGrid)
            {
                pisoShader.SetTexture(pisoPresCorrectionKernel, "AodUCorrectField", AodUCorrectFieldTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "flagField", flagTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "DField", DFieldTex);
                pisoShader.Dispatch(pisoPresCorrectionKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
            }
            else
            {
                throw new NotImplementedException();
            }

            // Swap the current and last iteration velocity fields.
            (presCorrectFieldLastIterTex, presCorrectFieldTex) = (presCorrectFieldTex, presCorrectFieldLastIterTex);
        }

        // Ensure up to date value is stored in "<quantity>Field", not "<quantity>FieldLastIter".
        (presCorrectFieldLastIterTex, presCorrectFieldTex) = (presCorrectFieldTex, presCorrectFieldLastIterTex);

        // ----- Update pressure field -----
        pisoShader.SetTexture(pisoApplyPresCorrectionKernel, "presFieldLastTime", presFieldLastTimeTex);
        pisoShader.SetTexture(pisoApplyPresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        pisoShader.SetTexture(pisoApplyPresCorrectionKernel, "flagField", flagTex);
        pisoShader.Dispatch(pisoApplyPresCorrectionKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        // ----- Update velocity field -----
        pisoShader.SetTexture(pisoVelCorrectionKernel, "velField", velFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "velCorrectField", velCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "AodUCorrectField", AodUCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "flagField", flagTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "DField", DFieldTex);
        pisoShader.Dispatch(pisoVelCorrectionKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        // ----- Reset presCorrectField to zero -----
        SetPresCorrectFieldtoZero();
    }
    #endregion

    #region UtilFuncs
    void CopyVelField(RenderTexture destVel, RenderTexture srcVel)
    {
        utilsShader.SetTexture(copyVelFieldKernel, "destVel", destVel);
        utilsShader.SetTexture(copyVelFieldKernel, "srcVel", srcVel);

        utilsShader.Dispatch(copyVelFieldKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void SetPresCorrectFieldtoZero()
    {
        utilsShader.SetFloat("presValue", 0f);

        utilsShader.SetTexture(setPresFieldKernel, "destPres", presCorrectFieldTex);
        utilsShader.Dispatch(setPresFieldKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        utilsShader.SetTexture(setPresFieldKernel, "destPres", presCorrectFieldLastIterTex);
        utilsShader.Dispatch(setPresFieldKernel, (gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);
    }

    void SetBndTex(ComputeShader shader, int kernel)
    {
        shader.SetTexture(kernel, "bndFieldX0", bndX0Tex);
        shader.SetTexture(kernel, "bndFieldXn", bndXnTex);
        shader.SetTexture(kernel, "bndFieldY0", bndY0Tex);
        shader.SetTexture(kernel, "bndFieldYn", bndYnTex);
        shader.SetTexture(kernel, "bndFieldZ0", bndZ0Tex);
        shader.SetTexture(kernel, "bndFieldZn", bndZnTex);
    }

    void SetFacePosTex(ComputeShader shader, int kernel)
    {
        shader.SetBuffer(kernel, "facePosX", facePosXBuf);
        shader.SetBuffer(kernel, "facePosY", facePosYBuf);
        shader.SetBuffer(kernel, "facePosZ", facePosZBuf);
    }
    #endregion

    #region FieldInterfaceFuncs
    public object GetVelField()
    {
        return velFieldTex;
    }

    public object GetPresField()
    {
        return presFieldLastTimeTex;
        //return eddyVisFieldTex;
    }

    public object GetFlagField()
    {
        return flagTex;
    }

    public float[] GetFacePosXArray()
    {
        return facePosXArray;
    }

    public float[] GetFacePosYArray()
    {
        return facePosYArray;
    }

    public float[] GetFacePosZArray()
    {
        return facePosZArray;
    }

    #endregion
}