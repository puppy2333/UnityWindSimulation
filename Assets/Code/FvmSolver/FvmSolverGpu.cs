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
    private int3 presRes;
    private int3 velRes;
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
    private RenderTexture velFaceFluxFieldTex;

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
    int computePredictedVelKernel;
    int solvePresCorrectionKernel;
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

    // ----- Utility shader kernel -----
    int copyVelFieldKernel;
    int setPresFieldKernel;
    #endregion

    #region solverState
    CurrSolverStage currSolverStage = CurrSolverStage.Idle;
    int currItersInCurrSim = 0;
    bool readyForRender = true;
    #endregion

    #region StopWatch
    private Stopwatch stopwatch = new();
    #endregion

    #region InitFuncs
    public FvmSolverGpu(FluidSimConfig cfIn, RuntimeConfig rcfIn)
    {
        // Load from configuration file.
        LoadConfig(cfIn);
        rcf = rcfIn;

        // Malloc the velocity and pressure textures.
        MallocTextures();

        // Initialize shaders and kernels.
        InitInitShader();
        
        if (cf.gridType == GridType.Collocated || cf.gridType == GridType.Staggered)
            InitFvmShader();
        else if (cf.gridType == GridType.CollNonUniform)
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
        UnityEngine.Debug.Log($"CFL condition (should be less than 1): {cf.Umax * cf.dt / cf.dx}");

        // Init mesh.
        if (cf.gridType == GridType.CollNonUniform)
            InitMeshBuffers();

        // Init flag field.
        InitFlagField();

        // Init pressure and velocity fields (vel field must be inited after flag field).
        InitVelPresFields();

        if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
            InitFaceEddyVisField();

        if (cf.solidType == SolidType.Box)
            InitBox();

        // ----- Set boundary conditions -----
        SetZeroVelBndCond();
        SetFixedValueVelBndCond();
    }

    void LoadConfig(FluidSimConfig cfIn)
    {
        // Load configuration file.
        cf = cfIn;
        externalForce = new(cf.externalForce.x, cf.externalForce.y, cf.externalForce.z);

        gridRes = cf.gridRes;
        presRes = cf.presRes;
        velRes = cf.velRes;

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
        velFieldTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);
        velFieldLastTimeTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);
        velFieldLastIterTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);
        velCorrectFieldTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);

        presFieldLastTimeTex = CreateRenderTexture3D(presRes, RenderTextureFormat.RFloat);
        presCorrectFieldTex = CreateRenderTexture3D(presRes, RenderTextureFormat.RFloat);
        presCorrectFieldLastIterTex = CreateRenderTexture3D(presRes, RenderTextureFormat.RFloat);

        velFaceFluxFieldTex = CreateRenderTexture3D(velRes + new int3(1, 1, 1), RenderTextureFormat.ARGBFloat);

        flagTex = CreateRenderTexture3D(gridRes, RenderTextureFormat.RInt);

        DFieldTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);
        bFieldTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);
        DFieldPresCorrectTex = CreateRenderTexture3D(presRes, RenderTextureFormat.RFloat);
        bFieldPresCorrectTex = CreateRenderTexture3D(presRes, RenderTextureFormat.RFloat);

        // ----- PISO Texturees -----
        if (cf.fvmSolverType == FvmSolverType.PISO)
            AodUCorrectFieldTex = CreateRenderTexture3D(velRes, RenderTextureFormat.ARGBFloat);

        // ----- Residual textures -----
        gloResBufSize = numGroups.x * numGroups.y * numGroups.z;
        gloResBufCpu = new float[gloResBufSize];
        gloResBuf = new ComputeBuffer(gloResBufSize, sizeof(float));
        gloResBuf.SetData(gloResBufCpu);

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
        initShader.SetInts("R", cf.R.x, cf.R.y);
        initShader.SetInts("jetCenter", cf.jetCenter.x, cf.jetCenter.y);

        // ----- Set shader parameters (bnd conds) -----
        initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
        initShader.SetFloats("velZn", cf.velZn.x, cf.velZn.y, cf.velZn.z);

        // ----- Set shader parameters (solid) -----
        initShader.SetFloats("boxStart", cf.boxStart.x, cf.boxStart.y, cf.boxStart.z);
        initShader.SetFloats("boxEnd", cf.boxEnd.x, cf.boxEnd.y, cf.boxEnd.z);

        // ----- Set shader parameters (fluid field) -----
        initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);

        // ----- Register kernels (fluid field) -----
        initVelPresFieldsKernel = initShader.FindKernel("CSInitVelPresFields");
        if (cf.gridType == GridType.Collocated)
            initLogVelPresFieldsKernel = initShader.FindKernel("CSInitLogVelPresFields");
        else if (cf.gridType == GridType.CollNonUniform)
            initLogVelPresFieldsKernel = initShader.FindKernel("CSInitLogVelPresFieldsNonUniform");
        initVelPresFieldsFromBGFlowKernel = initShader.FindKernel("CSInitVelPresFieldsFromBGFlowRotate");

        // ----- Register kernels (bnd conds) -----
        setZeroVelBndCondKernel = initShader.FindKernel("CSSetZeroVelBndCond");
        setFixedValueVelBndCondKernel = initShader.FindKernel("CSSetFixedValueVelBndCond");
        if (cf.gridType == GridType.Collocated)
            setLogVelBndCondKernel = initShader.FindKernel("CSSetLogVelBndCond");
        else if (cf.gridType == GridType.CollNonUniform)
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
        if (cf.gridType == GridType.Collocated)
        {
            ComputeShader fvmShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmColl");
            fvmShader = UnityEngine.Object.Instantiate(fvmShaderAsset);
        }
        else if (cf.gridType == GridType.Staggered)
        {
            fvmShader = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmStag");
        }
        else
        {
            throw new NotImplementedException("No FVM shader implemented for current grid type.");
        }

        // Set shader parameters.
        fvmShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        fvmShader.SetInts("presRes", presRes.x, presRes.y, presRes.z);
        fvmShader.SetInts("velRes", velRes.x, velRes.y, velRes.z);
        fvmShader.SetFloat("dt", cf.dt);
        fvmShader.SetFloat("dx", cf.dx);
        fvmShader.SetFloat("ds", cf.ds);
        fvmShader.SetFloat("dv", cf.dv);
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
        if (cf.gridType == GridType.Collocated)
        {
            // ----- SIMPLE kernels -----
            if (cf.turbulenceModel == TurbulenceModel.Smagorinsky)
                computePredictedVelKernel = fvmShader.FindKernel("CSComputePredictedVelLES");
            else
                computePredictedVelKernel = fvmShader.FindKernel("CSComputePredictedVel");

            if (cf.faceVelInterpScheme == FaceVelInterpScheme.RhieChow)
                solvePresCorrectionKernel = fvmShader.FindKernel("CSSolvePresCorrectionRhieChow");
            else
                solvePresCorrectionKernel = fvmShader.FindKernel("CSSolvePresCorrection");

            // ----- SIMPLE kernels (accelerated) -----
            velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictPreCompute");
            velPredictKernel = fvmShader.FindKernel("CSVelPredict");
            presCorrectPreComputeKernel = fvmShader.FindKernel("CSPresCorrectRhieChowPreCompute");
            presCorrectKernel = fvmShader.FindKernel("CSPresCorrectRhieChow");

            applyPresCorrectionKernel = fvmShader.FindKernel("CSApplyPresCorrection");
            presNormalizationKernel = fvmShader.FindKernel("CSPresNormalization");
            //applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrection");
            applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrectionDivergenceTheorem");

            // ----- Tolerence kernels -----
            computeVelPredictResidualKernel = fvmShader.FindKernel("CSComputeVelPredictResidual");
            computePresCorrectResidualKernel = fvmShader.FindKernel("CSComputePresCorrectResidual");
        }
        else
        {
            if (cf.convectionScheme == ConvectionScheme.CDS)
            {
                computePredictedVelKernel = fvmShader.FindKernel("CSComputePredictedVel");
            }
            else
            {
                computePredictedVelKernel = fvmShader.FindKernel("CSComputePredictedVelUpwind");
            }
            solvePresCorrectionKernel = fvmShader.FindKernel("CSSolvePresCorrection");
            neumannPresBndCondKernel = fvmShader.FindKernel("CSNeumannPresBndCond");
            applyPresCorrectionKernel = fvmShader.FindKernel("CSApplyPresCorrection");
            presNormalizationKernel = fvmShader.FindKernel("CSPresNormalization");
            applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrection");
        }
    }

    void InitFvmNonUniformShader()
    {
        // Load shader file.
        ComputeShader fvmShaderAsset = Resources.Load<ComputeShader>("Shaders/SimShaders/FvmCollNonUniform");
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
        velPredictPreComputeKernel = fvmShader.FindKernel("CSVelPredictPreCompute");
        velPredictKernel = fvmShader.FindKernel("CSVelPredict");
        presCorrectPreComputeKernel = fvmShader.FindKernel("CSPresCorrectRhieChowPreCompute");
        presCorrectKernel = fvmShader.FindKernel("CSPresCorrectRhieChow");

        applyPresCorrectionKernel = fvmShader.FindKernel("CSApplyPresCorrection");
        presNormalizationKernel = fvmShader.FindKernel("CSPresNormalization");
        applyVelCorrectionKernel = fvmShader.FindKernel("CSApplyVelCorrectionDivergenceTheorem");

        // ----- Tolerence kernels -----
        computeVelPredictResidualKernel = fvmShader.FindKernel("CSComputeVelPredictResidual");
        computePresCorrectResidualKernel = fvmShader.FindKernel("CSComputePresCorrectResidual");
    }

    void InitPisoShader()
    {
        // Load shader file.
        if (cf.gridType == GridType.Collocated)
            pisoShader = Resources.Load<ComputeShader>("Shaders/SimShaders/PisoColl");
        else
            throw new NotImplementedException("No Piso algorithm implemented for staggered grid.");

        // Set shader parameters.
        pisoShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        pisoShader.SetFloat("dt", cf.dt);
        pisoShader.SetFloat("dx", cf.dx);
        pisoShader.SetFloat("ds", cf.ds);
        pisoShader.SetFloat("dv", cf.dv);
        pisoShader.SetFloat("nu", cf.nu);
        pisoShader.SetFloat("den", cf.den);
        pisoShader.SetVector("externalForce", externalForce);

        // Register kernels.
        if (cf.gridType == GridType.Collocated)
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
        // Load shader file.
        ComputeShader lesShaderAsset = Resources.Load<ComputeShader>("Shaders/LESShaders/Les");
        lesShader = UnityEngine.Object.Instantiate(lesShaderAsset);

        // Set shader parameters.
        lesShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        lesShader.SetFloat("dx", cf.dx);
        lesShader.SetFloat("ds", cf.ds);
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
        // Load shader file.
        ComputeShader wallFuncShaderAsset = Resources.Load<ComputeShader>("Shaders/WallFuncShaders/WallFunc");
        wallFuncShader = UnityEngine.Object.Instantiate(wallFuncShaderAsset);

        // Set shader parameters.
        wallFuncShader.SetInts("gridRes", gridRes.x, gridRes.y, gridRes.z);
        wallFuncShader.SetFloat("dx", cf.dx);
        wallFuncShader.SetFloat("ds", cf.ds);
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
        utilsShader.SetInts("presRes", presRes.x, presRes.y, presRes.z);
        utilsShader.SetInts("velRes", velRes.x, velRes.y, velRes.z);

        // Register kernels.
        copyVelFieldKernel = utilsShader.FindKernel("CSCopyVelField");
        setPresFieldKernel = utilsShader.FindKernel("CSSetPresField");
    }

    void InitMeshBuffersTest()
    {
        facePosXBuf = new ComputeBuffer(gridRes.x + 1, sizeof(float)); // +1 for face positions
        facePosYBuf = new ComputeBuffer(gridRes.y + 1, sizeof(float));
        facePosZBuf = new ComputeBuffer(gridRes.z + 1, sizeof(float));

        facePosXArray = new float[gridRes.x + 1];
        facePosYArray = new float[gridRes.y + 1];
        facePosZArray = new float[gridRes.z + 1];

        // ----- Uniform grid -----
        //for (int x = 0; x < gridRes.x + 1; x++)
        //    facePosXArray[x] = x * cf.dx;
        //for (int y = 0; y < gridRes.y + 1; y++)
        //    facePosYArray[y] = y * cf.dx;
        //for (int z = 0; z < gridRes.z + 1; z++)
        //    facePosZArray[z] = z * cf.dx;

        // ----- Non uniform grid -----
        // Cell length array, from object to, e.g. x0 domain boundary.
        // Pattern: [dx, 1.08 * dx, 1.08^2 * dx, ...].
        float[] x0GridSizeArray = new float[45];
        float[] ynGridSizeArray = new float[30];

        float ratio = 1.04f;

        x0GridSizeArray[0] = cf.dx;
        //for (int i = 1; i < 45; i++)
        //    x0GridSizeArray[i] = x0GridSizeArray[i - 1] * ratio;
        for (int i = 1; i < 20; i++)
            x0GridSizeArray[i] = x0GridSizeArray[i - 1] * ratio;
        for (int i = 20; i < 45; i++)
            x0GridSizeArray[i] = x0GridSizeArray[i - 1];

        ynGridSizeArray[0] = cf.dx;
        for (int i = 1; i < 30; i++)
            ynGridSizeArray[i] = ynGridSizeArray[i - 1] * ratio;

        // ----- Fill in the x face position arrays -----
        facePosXArray[0] = 0;
        for (int x = 1; x < 46; x++) // Face number = cell number + 1
            facePosXArray[x] = facePosXArray[x - 1] + x0GridSizeArray[45 - x];
        for (int x = 46; x < 56; x++)
            facePosXArray[x] = facePosXArray[x - 1] + cf.dx;
        for (int x = 56; x < gridRes.x + 1; x++)
            facePosXArray[x] = facePosXArray[x - 1] + x0GridSizeArray[x - 56];

        //for (int x = 0; x < gridRes.x + 1; x++)
        //    facePosXArray[x] = 2 * x * cf.dx;

        // ----- Fill in the y face position arrays -----
        //for (int y = 0; y < 21; y++) // Face number = cell number + 1
        //    facePosYArray[y] = y * cf.dx;
        //for (int y = 21; y < 51; y++)
        //    facePosYArray[y] = facePosYArray[y - 1] + ynGridSizeArray[y - 21];

        for (int y = 0; y < gridRes.y + 1; y++)
            facePosYArray[y] = y * cf.dx;

        // ----- Fill in the z face position arrays -----
        //facePosZArray[0] = 0;
        //for (int z = 1; z < 46; z++) // Face number = cell number + 1
        //    facePosZArray[z] = facePosZArray[z - 1] + x0GridSizeArray[45 - z];
        //for (int z = 46; z < 56; z++)
        //    facePosZArray[z] = facePosZArray[z - 1] + cf.dx;
        //for (int z = 56; z < gridRes.z + 1; z++)
        //    facePosZArray[z] = facePosZArray[z - 1] + x0GridSizeArray[z - 56];

        for (int z = 0; z < gridRes.z + 1; z++)
            facePosZArray[z] = z * cf.dx;

        UnityEngine.Debug.Log(string.Join(", ", facePosXArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosYArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosZArray));

        facePosXBuf.SetData(facePosXArray);
        facePosYBuf.SetData(facePosYArray);
        facePosZBuf.SetData(facePosZArray);
    }

    void InitMeshBuffers()
    {
        facePosXBuf = new ComputeBuffer(gridRes.x + 1, sizeof(float)); // +1 for face positions
        facePosYBuf = new ComputeBuffer(gridRes.y + 1, sizeof(float));
        facePosZBuf = new ComputeBuffer(gridRes.z + 1, sizeof(float));

        facePosXArray = new float[gridRes.x + 1];
        facePosYArray = new float[gridRes.y + 1];
        facePosZArray = new float[gridRes.z + 1];

        int3 uniformSize = new int3(50, 50, 50);
        int3 numNonUniformCellsOneSide = new int3(25, 0, 25);
        //int3 uniformSize = new int3(100, 50, 100);
        //int3 numNonUniformCellsOneSide = new int3(0, 0, 0);
        float ratio = 1.08f;

        // ----- Non uniform grid -----
        // Cell length array, from object to, e.g. x0 domain boundary.
        // Pattern: [dx, 1.08 * dx, 1.08^2 * dx, ...].
        float[] x0GridSizeArray = new float[numNonUniformCellsOneSide.x];
        float[] xnGridSizeArray = new float[gridRes.x - uniformSize.x - numNonUniformCellsOneSide.x];
        float[] y0GridSizeArray = new float[numNonUniformCellsOneSide.y];
        float[] ynGridSizeArray = new float[gridRes.y - uniformSize.y - numNonUniformCellsOneSide.y];
        float[] z0GridSizeArray = new float[numNonUniformCellsOneSide.z];
        float[] znGridSizeArray = new float[gridRes.z - uniformSize.z - numNonUniformCellsOneSide.z];

        if (x0GridSizeArray.Length > 0)
        {
            x0GridSizeArray[0] = cf.dx;
            for (int i = 1; i < x0GridSizeArray.Length; i++)
                x0GridSizeArray[i] = x0GridSizeArray[i - 1] * ratio;
        }
        if (xnGridSizeArray.Length > 0)
        {
            xnGridSizeArray[0] = cf.dx;
            for (int i = 1; i < xnGridSizeArray.Length; i++)
                xnGridSizeArray[i] = xnGridSizeArray[i - 1] * ratio;
        }
        if (y0GridSizeArray.Length > 0)
        {
            y0GridSizeArray[0] = cf.dx;
            for (int i = 1; i < y0GridSizeArray.Length; i++)
                y0GridSizeArray[i] = y0GridSizeArray[i - 1] * ratio;
        }
        if (ynGridSizeArray.Length > 0)
        {
            ynGridSizeArray[0] = cf.dx;
            for (int i = 1; i < ynGridSizeArray.Length; i++)
                ynGridSizeArray[i] = ynGridSizeArray[i - 1] * ratio;
        }
        if (z0GridSizeArray.Length > 0)
        {
            z0GridSizeArray[0] = cf.dx;
            for (int i = 1; i < z0GridSizeArray.Length; i++)
                z0GridSizeArray[i] = z0GridSizeArray[i - 1] * ratio;
        }
        if (znGridSizeArray.Length > 0)
        {
            znGridSizeArray[0] = cf.dx;
            for (int i = 1; i < znGridSizeArray.Length; i++)
                znGridSizeArray[i] = znGridSizeArray[i - 1] * ratio;
        }

        // ----- Fill in x face position arrays -----
        facePosXArray[0] = 0;
        for (int x = 1; x < x0GridSizeArray.Length + 1; x++) // Face number = cell number + 1
            facePosXArray[x] = facePosXArray[x - 1] + x0GridSizeArray[x0GridSizeArray.Length - x];

        for (int x = x0GridSizeArray.Length + 1; x < x0GridSizeArray.Length + uniformSize.x + 1; x++)
            facePosXArray[x] = facePosXArray[x - 1] + cf.dx;

        for (int x = x0GridSizeArray.Length + uniformSize.x + 1; x < gridRes.x + 1; x++)
        {
            //UnityEngine.Debug.Log($"x: {x}, index in xn array: {x - (x0GridSizeArray.Length + uniformSize.x + 1)}");
            facePosXArray[x] = facePosXArray[x - 1] + x0GridSizeArray[x - (x0GridSizeArray.Length + uniformSize.x + 1)];
        }

        // ----- Fill in y face position arrays -----
        facePosYArray[0] = 0;
        for (int y = 1; y < y0GridSizeArray.Length + 1; y++) // Face number = cell number + 1
            facePosYArray[y] = facePosYArray[y - 1] + y0GridSizeArray[y0GridSizeArray.Length - y];

        for (int y = y0GridSizeArray.Length + 1; y < y0GridSizeArray.Length + uniformSize.y + 1; y++)
            facePosYArray[y] = facePosYArray[y - 1] + cf.dx;

        for (int y = y0GridSizeArray.Length + uniformSize.y + 1; y < gridRes.y + 1; y++)
            facePosYArray[y] = facePosYArray[y - 1] + y0GridSizeArray[y - (y0GridSizeArray.Length + uniformSize.y + 1)];

        // ----- Fill in the z face position arrays -----
        facePosZArray[0] = 0;
        for (int z = 1; z < z0GridSizeArray.Length + 1; z++) // Face number = cell number + 1
            facePosZArray[z] = facePosZArray[z - 1] + z0GridSizeArray[z0GridSizeArray.Length - z];

        for (int z = z0GridSizeArray.Length + 1; z < z0GridSizeArray.Length + uniformSize.z + 1; z++)
            facePosZArray[z] = facePosZArray[z - 1] + cf.dx;

        for (int z = z0GridSizeArray.Length + uniformSize.z + 1; z < gridRes.z + 1; z++)
            facePosZArray[z] = facePosZArray[z - 1] + z0GridSizeArray[z - (z0GridSizeArray.Length + uniformSize.z + 1)];

        UnityEngine.Debug.Log(string.Join(", ", facePosXArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosYArray));
        UnityEngine.Debug.Log(string.Join(", ", facePosZArray));

        facePosXBuf.SetData(facePosXArray);
        facePosYBuf.SetData(facePosYArray);
        facePosZBuf.SetData(facePosZArray);
    }

    public void InitVelPresFields()
    {
        if (cf.gridType == GridType.Collocated)
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
                initShader.Dispatch(initVelPresFieldsKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
            }
            else if (cf.inflowType == InflowType.Log)
            {
                // ----- ReInit wind speed -----
                initShader.SetFloats("internalVelField", rcf.windSpeed, 0, 0);
                initShader.SetFloat("dx", cf.dx);
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
                initShader.Dispatch(initLogVelPresFieldsKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
            }
        }
        else if (cf.gridType == GridType.CollNonUniform)
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
                initShader.Dispatch(initVelPresFieldsKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
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
                initShader.SetTexture(initLogVelPresFieldsKernel, "presFieldLastTime", presFieldLastTimeTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectField", presCorrectFieldTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                initShader.SetTexture(initLogVelPresFieldsKernel, "flagField", flagTex);
                // ----- Set mesh buffers (read-only) -----
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosX", facePosXBuf);
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosY", facePosYBuf);
                initShader.SetBuffer(initLogVelPresFieldsKernel, "facePosZ", facePosZBuf);
                // Init all fields to zero.
                initShader.Dispatch(initLogVelPresFieldsKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
            }
        }
        else if (cf.gridType == GridType.Staggered)
        {
            initShader.SetTexture(initPresFieldKernel, "presFieldLastTime", presFieldLastTimeTex);
            initShader.SetTexture(initPresFieldKernel, "presCorrectField", presCorrectFieldTex);
            initShader.SetTexture(initPresFieldKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
            initShader.Dispatch(initPresFieldKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);

            initShader.SetTexture(initVelFieldKernel, "flagField", flagTex);
            initShader.SetTexture(initVelFieldKernel, "velField", velFieldTex);
            initShader.SetTexture(initVelFieldKernel, "velFieldLastTime", velFieldLastTimeTex);
            initShader.SetTexture(initVelFieldKernel, "velFieldLastIter", velFieldLastIterTex);
            initShader.Dispatch(initVelFieldKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
        }
        else
        {
            throw new NotImplementedException("InitVelPresFields not implemented for current grid type.");
        }
    }

    public void InitVelPresFieldsFromBackGroundFlow(FluidSimConfig cfBackGround, RenderTexture velTexBackGround, RenderTexture presTexBackGround)
    {
        if (cf.gridType == GridType.Collocated)
        {
            // ----- Set shader parameters (re-init interpolation) -----
            initShader.SetFloat("dxFG", cf.dx);
            initShader.SetFloat("dxBG", cfBackGround.dx);
            initShader.SetFloats("physFieldPosFG", cf.physFieldPos.x, cf.physFieldPos.y, cf.physFieldPos.z);
            initShader.SetFloats("physFieldPosBG", cfBackGround.physFieldPos.x, cfBackGround.physFieldPos.y, cfBackGround.physFieldPos.z);
            initShader.SetFloat("fieldRotAng", rcf.flowFieldOrientation * (float)(3.1415926 / 180));
            initShader.SetInts("gridResBG", cfBackGround.gridRes.x, cfBackGround.gridRes.y, cfBackGround.gridRes.z);

            // Set the textures in the compute shader.
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velField", velFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldLastTime", velFieldLastTimeTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldLastIter", velFieldLastIterTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velCorrectField", velCorrectFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presFieldLastTime", presFieldLastTimeTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presCorrectField", presCorrectFieldTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "flagField", flagTex);

            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "velFieldBG", velTexBackGround);
            initShader.SetTexture(initVelPresFieldsFromBGFlowKernel, "presFieldBG", presTexBackGround);

            // Init all fields to zero.
            initShader.Dispatch(initVelPresFieldsFromBGFlowKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
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
        initShader.Dispatch(initFlagFieldKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
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
        initShader.Dispatch(initBoxFlagKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
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
        initShader.Dispatch(initModelFlagKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);

        currSolverStage = CurrSolverStage.Idle;
        currItersInCurrSim = 0;
        readyForRender = false;
    }

    void SetZeroVelBndCond()
    {
        SetBndTextures(initShader, setZeroVelBndCondKernel);

        initShader.Dispatch(setZeroVelBndCondKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    public void SetFixedValueVelBndCond()
    {
        if (cf.inflowType == InflowType.Constant)
        {
            initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
            SetBndTextures(initShader, setFixedValueVelBndCondKernel);
            initShader.Dispatch(setFixedValueVelBndCondKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
        }
        else if (cf.inflowType == InflowType.Log)
        {
            initShader.SetFloats("velX0", rcf.windSpeed, 0, 0);
            initShader.SetFloat("dx", cf.dx);
            initShader.SetFloat("karmanConst", 0.4f);
            initShader.SetFloat("roughParam", 1.0f);
            initShader.SetFloat("refHeight", 10.0f);

            if (cf.gridType == GridType.CollNonUniform)
            {
                initShader.SetBuffer(setLogVelBndCondKernel, "facePosX", facePosXBuf);
                initShader.SetBuffer(setLogVelBndCondKernel, "facePosY", facePosYBuf);
                initShader.SetBuffer(setLogVelBndCondKernel, "facePosZ", facePosZBuf);
            }

            SetBndTextures(initShader, setLogVelBndCondKernel);
            initShader.Dispatch(setLogVelBndCondKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
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
        gridRes = cf.gridRes;
        presRes = cf.presRes;
        velRes = cf.velRes;
        numGroups = new int3((gridRes.x + 7) / 8, (gridRes.y + 7) / 8, (gridRes.z + 7) / 8);

        // ----- Reinitialize fields -----

        // Init pressure and velocity fields (vel field must be inited after flag field).
        InitVelPresFields();
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

        if (currSolverStage == CurrSolverStage.Finished)
        {
            (velFieldLastTimeTex, velFieldTex) = (velFieldTex, velFieldLastTimeTex);
            CopyVelField(velFieldLastIterTex, velFieldLastTimeTex);

            cf.currPhyTime += cf.dt;
            cf.currSimStep += 1;

            if (cf.showSimulationTime)
            {
                UnityEngine.Debug.Log($"Simulated physical time: {cf.currPhyTime:F2} seconds, consumed time: {stopwatch.ElapsedMilliseconds} ms.");
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
        SetBndTextures(lesShader, eddyVisKernel);
        lesShader.Dispatch(eddyVisKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);

        lesShader.SetTexture(lesDeferCorrectTermKernel, "velFieldLastTime", velFieldLastTimeTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "eddyVisField", eddyVisFieldTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only in les shader
        lesShader.SetTexture(lesDeferCorrectTermKernel, "bField", bFieldTex);
        lesShader.SetTexture(lesDeferCorrectTermKernel, "flagField", flagTex); // Read-only in les shader
        SetBndTextures(lesShader, lesDeferCorrectTermKernel);
        lesShader.Dispatch(lesDeferCorrectTermKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void CalWallFunc()
    {
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "velFieldLastTime", velFieldLastTimeTex); // Read-only in wall func shader
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "faceEddyVisField", faceEddyVisFieldTex);
        wallFuncShader.SetTexture(calLogLawWallFuncKernel, "flagField", flagTex); // Read-only in wall func shader
        SetBndTextures(wallFuncShader, calLogLawWallFuncKernel);
        wallFuncShader.Dispatch(calLogLawWallFuncKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void _VelPredictPreCompute()
    {
        if (cf.gridType == GridType.CollNonUniform)
        {
            fvmShader.SetBuffer(velPredictPreComputeKernel, "facePosX", facePosXBuf);
            fvmShader.SetBuffer(velPredictPreComputeKernel, "facePosY", facePosYBuf);
            fvmShader.SetBuffer(velPredictPreComputeKernel, "facePosZ", facePosZBuf);
        }

        fvmShader.SetTexture(velPredictPreComputeKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "presFieldLastTime", presFieldLastTimeTex);
        //fvmShader.SetTexture(velPredictPreComputeKernel, "velFaceFluxField", velFaceFluxFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "flagField", flagTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "DField", DFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "bField", bFieldTex);
        fvmShader.SetTexture(velPredictPreComputeKernel, "eddyVisField", eddyVisFieldTex); // Read-only in fvm shader
        fvmShader.SetTexture(velPredictPreComputeKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only in les shader
        SetBndTextures(fvmShader, velPredictPreComputeKernel);
        fvmShader.Dispatch(velPredictPreComputeKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void _VelPredictColl()
    {
        fvmShader.SetTexture(computePredictedVelKernel, "velField", velFieldTex);
        fvmShader.SetTexture(computePredictedVelKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(computePredictedVelKernel, "velFieldLastIter", velFieldLastIterTex);
        fvmShader.SetTexture(computePredictedVelKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(computePredictedVelKernel, "flagField", flagTex);
        fvmShader.SetTexture(computePredictedVelKernel, "DField", DFieldTex);
        fvmShader.SetTexture(computePredictedVelKernel, "eddyVisField", eddyVisFieldTex); // Read-only in fvm shader
        fvmShader.SetTexture(computePredictedVelKernel, "bField", bFieldTex);
        SetBndTextures(fvmShader, computePredictedVelKernel);
        fvmShader.Dispatch(computePredictedVelKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void _VelPredictCollAccelerated()
    {
        if (cf.gridType == GridType.CollNonUniform)
        {
            fvmShader.SetBuffer(velPredictKernel, "facePosX", facePosXBuf);
            fvmShader.SetBuffer(velPredictKernel, "facePosY", facePosYBuf);
            fvmShader.SetBuffer(velPredictKernel, "facePosZ", facePosZBuf);
        }

        fvmShader.SetTexture(velPredictKernel, "velField", velFieldTex);
        fvmShader.SetTexture(velPredictKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(velPredictKernel, "velFieldLastIter", velFieldLastIterTex);
        //fvmShader.SetTexture(velPredictKernel, "velFaceFluxField", velFaceFluxFieldTex);
        fvmShader.SetTexture(velPredictKernel, "flagField", flagTex);
        fvmShader.SetTexture(velPredictKernel, "DField", DFieldTex);
        fvmShader.SetTexture(velPredictKernel, "bField", bFieldTex);
        fvmShader.SetTexture(velPredictKernel, "eddyVisField", eddyVisFieldTex); // Read-only
        fvmShader.SetTexture(velPredictKernel, "faceEddyVisField", faceEddyVisFieldTex); // Read-only
        fvmShader.Dispatch(velPredictKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void _VelPredictStagg()
    {
        fvmShader.SetTexture(computePredictedVelKernel, "velField", velFieldTex);
        fvmShader.SetTexture(computePredictedVelKernel, "velFieldLastTime", velFieldLastTimeTex);
        fvmShader.SetTexture(computePredictedVelKernel, "velFieldLastIter", velFieldLastIterTex);
        fvmShader.SetTexture(computePredictedVelKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(computePredictedVelKernel, "flagField", flagTex);
        fvmShader.SetTexture(computePredictedVelKernel, "DField", DFieldTex);
        fvmShader.Dispatch(computePredictedVelKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
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

    bool VelPredict()
    {
        // ----- Vel prediction precompute -----
        if (currItersInCurrSim == 0)
            _VelPredictPreCompute();

        for (int k = currItersInCurrSim; k < cf.velMaxNumIter; k++)
        {
            // ----- Solver -----
            _VelPredictCollAccelerated();

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
        if (cf.gridType == GridType.CollNonUniform)
        {
            fvmShader.SetBuffer(presCorrectPreComputeKernel, "facePosX", facePosXBuf);
            fvmShader.SetBuffer(presCorrectPreComputeKernel, "facePosY", facePosYBuf);
            fvmShader.SetBuffer(presCorrectPreComputeKernel, "facePosZ", facePosZBuf);
        }

        fvmShader.SetTexture(presCorrectPreComputeKernel, "velField", velFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "velFaceFluxField", velFaceFluxFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "flagField", flagTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "DField", DFieldTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        fvmShader.SetTexture(presCorrectPreComputeKernel, "bFieldPresCorrect", bFieldPresCorrectTex);
        SetBndTextures(fvmShader, presCorrectPreComputeKernel);
        fvmShader.Dispatch(presCorrectPreComputeKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
    }

    void _PresCorrectColl()
    {
        fvmShader.SetTexture(solvePresCorrectionKernel, "velField", velFieldTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "presFieldLastTime", presFieldLastTimeTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "flagField", flagTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "DField", DFieldTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        SetBndTextures(fvmShader, solvePresCorrectionKernel);
        fvmShader.Dispatch(solvePresCorrectionKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
    }

    void _PresCorrectCollAccelerated()
    {
        if (cf.gridType == GridType.CollNonUniform)
        {
            fvmShader.SetBuffer(presCorrectKernel, "facePosX", facePosXBuf);
            fvmShader.SetBuffer(presCorrectKernel, "facePosY", facePosYBuf);
            fvmShader.SetBuffer(presCorrectKernel, "facePosZ", facePosZBuf);
        }

        fvmShader.SetTexture(presCorrectKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(presCorrectKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
        fvmShader.SetTexture(presCorrectKernel, "flagField", flagTex);
        fvmShader.SetTexture(presCorrectKernel, "DField", DFieldTex);
        fvmShader.SetTexture(presCorrectKernel, "DFieldPresCorrect", DFieldPresCorrectTex);
        fvmShader.SetTexture(presCorrectKernel, "bFieldPresCorrect", bFieldPresCorrectTex);
        fvmShader.Dispatch(presCorrectKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
    }

    void _PresCorrectStag()
    {
        fvmShader.SetTexture(solvePresCorrectionKernel, "velField", velFieldTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "flagField", flagTex);
        fvmShader.SetTexture(solvePresCorrectionKernel, "DField", DFieldTex);
        fvmShader.Dispatch(solvePresCorrectionKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
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

            // ----- Apply pressure correction Neumann boundary condition -----
            if (cf.gridType == GridType.Staggered)
            {
                fvmShader.SetTexture(neumannPresBndCondKernel, "presCorrectField", presCorrectFieldTex);
                fvmShader.Dispatch(neumannPresBndCondKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
            }
            // ----- End of apply pressure correction Neumann boundary condition -----

            // Calculate residual
            if (cf.calResidual)
            {
                if (k % cf.presResidualCheckInterval == 0)
                {
                    float residual = _ComputePresCorrectResidual();

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
        fvmShader.Dispatch(applyPresCorrectionKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
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
            fvmShader.Dispatch(presNormalizationKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
        }

        return true;
    }

    void ApplyVelCorrection()
    {
        if (cf.gridType == GridType.Collocated)
        {
            fvmShader.SetTexture(applyVelCorrectionKernel, "velField", velFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "velCorrectField", velCorrectFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "flagField", flagTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "DField", DFieldTex);

            fvmShader.Dispatch(applyVelCorrectionKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
        }
        else if (cf.gridType == GridType.CollNonUniform)
        {
            fvmShader.SetBuffer(applyVelCorrectionKernel, "facePosX", facePosXBuf);
            fvmShader.SetBuffer(applyVelCorrectionKernel, "facePosY", facePosYBuf);
            fvmShader.SetBuffer(applyVelCorrectionKernel, "facePosZ", facePosZBuf);

            fvmShader.SetTexture(applyVelCorrectionKernel, "velField", velFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "velCorrectField", velCorrectFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "flagField", flagTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "DField", DFieldTex);

            fvmShader.Dispatch(applyVelCorrectionKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
        }
        else if (cf.gridType == GridType.Staggered)
        {
            fvmShader.SetTexture(applyVelCorrectionKernel, "velField", velFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "flagField", flagTex);
            fvmShader.SetTexture(applyVelCorrectionKernel, "DField", DFieldTex);

            fvmShader.Dispatch(applyVelCorrectionKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
        }
        else
        {
            throw new NotImplementedException("ApplyVelCorrection not implemented for current grid type.");
        }
    }

    void PisoCorrection()
    {
        // ----- Calculate AodUCorrectField -----
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "AodUCorrectField", AodUCorrectFieldTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "velFieldLastTime", velFieldLastTimeTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "velCorrectField", velCorrectFieldTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "flagField", flagTex);
        pisoShader.SetTexture(pisoCalAodUCorrectKernel, "DField", DFieldTex);
        pisoShader.Dispatch(pisoCalAodUCorrectKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);

        // ----- Compute pressure correction -----
        for (int k = 0; k < cf.presMaxNumIter; k++)
        {
            if (cf.gridType == GridType.Collocated)
            {
                pisoShader.SetTexture(pisoPresCorrectionKernel, "AodUCorrectField", AodUCorrectFieldTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "presCorrectField", presCorrectFieldTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "presCorrectFieldLastIter", presCorrectFieldLastIterTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "flagField", flagTex);
                pisoShader.SetTexture(pisoPresCorrectionKernel, "DField", DFieldTex);
                pisoShader.Dispatch(pisoPresCorrectionKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
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
        pisoShader.Dispatch(pisoApplyPresCorrectionKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);

        // ----- Update velocity field -----
        pisoShader.SetTexture(pisoVelCorrectionKernel, "velField", velFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "velCorrectField", velCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "presCorrectField", presCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "AodUCorrectField", AodUCorrectFieldTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "flagField", flagTex);
        pisoShader.SetTexture(pisoVelCorrectionKernel, "DField", DFieldTex);
        pisoShader.Dispatch(pisoVelCorrectionKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);

        // ----- Reset presCorrectField to zero -----
        SetPresCorrectFieldtoZero();
    }
    #endregion

    #region UtilFuncs
    void CopyVelField(RenderTexture destVel, RenderTexture srcVel)
    {
        utilsShader.SetTexture(copyVelFieldKernel, "destVel", destVel);
        utilsShader.SetTexture(copyVelFieldKernel, "srcVel", srcVel);

        utilsShader.Dispatch(copyVelFieldKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    void SetPresCorrectFieldtoZero()
    {
        utilsShader.SetFloat("presValue", 0f);

        utilsShader.SetTexture(setPresFieldKernel, "destPres", presCorrectFieldTex);
        utilsShader.Dispatch(setPresFieldKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);

        utilsShader.SetTexture(setPresFieldKernel, "destPres", presCorrectFieldLastIterTex);
        utilsShader.Dispatch(setPresFieldKernel, (presRes.x + 7) / 8, (presRes.y + 7) / 8, (presRes.z + 7) / 8);
    }

    void SetBndTextures(ComputeShader shader, int kernel)
    {
        shader.SetTexture(kernel, "bndFieldX0", bndX0Tex);
        shader.SetTexture(kernel, "bndFieldXn", bndXnTex);
        shader.SetTexture(kernel, "bndFieldY0", bndY0Tex);
        shader.SetTexture(kernel, "bndFieldYn", bndYnTex);
        shader.SetTexture(kernel, "bndFieldZ0", bndZ0Tex);
        shader.SetTexture(kernel, "bndFieldZn", bndZnTex);
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
    #endregion
}