using Unity.Mathematics;
using UnityEngine;
using System;
using System.IO;

using static UnityEngine.Rendering.HighDefinition.ScalableSettingLevelParameter;
using UnityEngine.UIElements;

public enum GridType
{
    Collocated,
    CollNonUniform,
    Staggered,
}

public enum VelBndCond
{
    FixedValue = 0,
    ZeroGrad = 1,
    Symmetry = 2,
}

public enum PresBndCond
{
    FixedValue = 0,
    ZeroGrad = 1,
    Symmetry = 2,
}

public enum FvmSolverType
{
    SIMPLE,
    PISO,
}

public enum ColorMap
{
    Turbo,
    Linear,
}

public enum SolidType
{
    NoSolid,
    Box,
    Model,
}

public enum Mode
{
    SimulateAndVisualize,
    Visualize,
    OutputBuildingVoxels,
}

public enum ConvectionScheme
{
    CDS,
    UDS,
}

public enum FaceVelInterpScheme
{
    RhieChow,
    Linear,
}

public enum VisInterpolateType
{
    Point,
    Bilinear,
}
public enum TurbulenceModel
{
    None,
    Smagorinsky,
}

public enum VoxelizerType
{
    YDirFromTopOneColli,
    YDirFromTop,
    XDirFromLeft,
}

public enum InflowType
{
    Constant,
    Log,
}

public enum VisualizeMode
{
    Copy,
    ZeroCopy,
}


[CreateAssetMenu(fileName = "FluidSimConfig", menuName = "Simulation/FluidSimConfig")]
public class FluidSimConfig : ScriptableObject
{
    // ----- Physical position strategy -----
    // 1. User specify model physical position and physical size in Unity editor.
    // 2. User specify flow field physical size in config file.
    // 3. User specify the relative position of model to flow field in config file.

    #region BehaviorController
    public Mode mode = Mode.SimulateAndVisualize;
    #endregion

    #region SimulationParameters
    [Header("Simulation Parameters")]
    public float3 physDomainSize = new(10f, 5f, 10f);
    public int gridResX = 100;
    public float dt = 0.1f;
    public float mu = 0.005f; // Dynamic viscosity
    public float den = 1.0f;
    [Tooltip("Only used when inflowType is Jet")]
    public float Umax = 0.5f; // Maximum velocity for jet flow
    [Tooltip("Only used when inflowType is Jet")]
    public int2 R = new(10, 10); // Radius of the jet inlet
    public int2 jetCenter = new(25, 50);
    public float3 externalForce = new(0f, 0f, 0f);

    [Header("Velocity boundary conditions")]
    public VelBndCond velBndCondX0 = VelBndCond.FixedValue;
    public VelBndCond velBndCondXn = VelBndCond.FixedValue;
    public VelBndCond velBndCondY0 = VelBndCond.FixedValue;
    public VelBndCond velBndCondYn = VelBndCond.FixedValue;
    public VelBndCond velBndCondZ0 = VelBndCond.FixedValue;
    public VelBndCond velBndCondZn = VelBndCond.FixedValue;

    [Header("Pressure boundary conditions")]
    public PresBndCond presBndCondX0 = PresBndCond.ZeroGrad;
    public PresBndCond presBndCondXn = PresBndCond.ZeroGrad;
    public PresBndCond presBndCondY0 = PresBndCond.ZeroGrad;
    public PresBndCond presBndCondYn = PresBndCond.ZeroGrad;
    public PresBndCond presBndCondZ0 = PresBndCond.ZeroGrad;
    public PresBndCond presBndCondZn = PresBndCond.ZeroGrad;

    public float3 velX0 = new(0f, 0f, 0f); // For inlet boundary condition
    public float3 velZn = new(0f, 0f, 0f); // For lid-driven cavity flow

    private float _dirichletVelX = 0.0f;
    public SolidType solidType = SolidType.NoSolid;
    public ConvectionScheme convectionScheme = ConvectionScheme.CDS;
    public FaceVelInterpScheme faceVelInterpScheme = FaceVelInterpScheme.RhieChow;
    public FvmSolverType fvmSolverType = FvmSolverType.SIMPLE;
    public int PISONumCorrectors = 2;
    public TurbulenceModel turbulenceModel = TurbulenceModel.None;
    public float smagorinskyConstant = 0.15f;
    public InflowType inflowType = InflowType.Constant;

    public float dirichletVelX
    {
        get => _dirichletVelX;
        set => _dirichletVelX = value;
    }
    #endregion

    #region GridParameters
    public GridType gridType = GridType.Collocated;
    #endregion

    #region BackGroundFlowParameters
    bool isBackGroundFlow = false;
    #endregion

    #region MatrixSolverParameters
    public bool calResidual = false;

    public int velResidualCheckInterval = 10;
    public float velTolerance = 1e-15f;
    public int velMaxNumIter = 50;

    public int presResidualCheckInterval = 20;
    public float presTolerance = 1e-3f;
    public int presMaxNumIter = 400;
    #endregion

    #region VisualizationParameters
    [Header("Visualization Parameters")]
    public VisualizeMode visualizeMode = VisualizeMode.Copy;
    public ColorMap colorMap = ColorMap.Turbo;
    public bool showVelocityField = true;
    public bool showPressureField = false;
    public bool showFlagField = false;
    public bool showVfx = false;
    public bool vFxArrowLengthFollowVelMag = true;
    public int ySlice = 25;
    public float minVel = 0f, maxVel = 0.5f;
    public float minPres = -0.5f, maxPres = 0.5f;
    public float cameraHeight = 100f;
    public float cameraAngle = 60f;
    public bool showSimulationTime = false;
    public bool showDomainBoundary = false;
    public float visScale = 1.0f;
    public VisInterpolateType visInterpolateType = VisInterpolateType.Point;
    private bool _colormapLoaded = false;
    public bool colormapLoaded
    {
        get => _colormapLoaded;
        set => _colormapLoaded = value;
    }
    #endregion

    #region DeducedParameters
    private float _dx;
    private float _ds;
    private float _dv;
    private int3 _gridRes; // User-defined grid resolution.
    private int3 _presRes; // Pressure grid resolution.
    private int3 _velRes; // Velocity grid resolution.
    private float _nu; // Kinematic viscosity
    private float _D; // Diagonal coefficients of velocity prediction coefficients matrix
    private float _DInv; // Inverse of D
    private float _currPhyTime = 0; // Current simulated physics time
    private int _currSimStep = 0; // Current total simulated steps.
    private int _numStepsSaved = 0; // Number of steps saved.

    public float dx => _dx;
    public float ds => _ds;
    public float dv => _dv;
    public int3 gridRes => _gridRes;
    public int3 presRes => _presRes;
    public int3 velRes => _velRes;
    public float nu => _nu;
    public float D => _D;
    public float DInv => _DInv;
    public float currPhyTime
    {
        get => _currPhyTime;
        set => _currPhyTime = value;
    }
    public int currSimStep
    {
        get => _currSimStep;
        set => _currSimStep = value;
    }
    public int numStepsSaved
    {
        get => _numStepsSaved;
        set => _numStepsSaved = value;
    }
    #endregion

    #region SolidParameters
    [Header("Solid Parameters")]
    public float3 boxStart = new(0.4f, 0.4f, 0.4f);
    public float3 boxEnd = new(0.6f, 0.6f, 0.6f);
    public VoxelizerType voxelizerType = VoxelizerType.YDirFromTop;
    [Tooltip("Whether to attach the fluid field to model bottom")]
    public bool attachFieldToModelBottom = false;
    public bool simulateGround = false;
    private float3 _physModelPos;
    public float3 physFieldPos = new(0.0f, 0.0f, 0.0f);
    public bool showVoxelization = false;
    private bool _fieldPositionLocated = false;
    public float3 physModelPos
    {
        get => _physModelPos;
        set => _physModelPos = value;
    }
    public bool fieldPositionLocated
    {
        get => _fieldPositionLocated;
        set => _fieldPositionLocated = value;
    }
    #endregion

    #region SliderParameters
    [Header("Slider Parameters")]
    public Vector2 slicePosSliderPos = new Vector2(-350, 0);
    public Vector2 slicePosSliderSize = new Vector2(20, 300);

    public Vector2 timeSliderPos = new Vector2(0, 300);
    public Vector2 timeSliderSize = new Vector2(300, 20);
    #endregion

    #region SaveParameters
    [Header("Save NetCDF Parameters")]
    public bool saveVelField = false;
    public bool savePresField = false;
    [Tooltip("If false, files will be saved to /Data by default")]
    public bool useAbsSaveDir = false;
    [Tooltip("Only valid if useAbsSavePath is true")]
    public string absSaveDir = "D:/Projects/Unity/UnityProjects/TestWindNoiseSimulation/Data";
    private string fileName = null;
    private string _savePath = null;
    public int saveBeginStep = 1000;
    public int saveInterval = 1000;
    public string flagFileName = null;
    private string _flagSavePath = null;

    public string savePath
    {
        get => _savePath;
        set => _savePath = value;
    }
    public string flagSavePath
    {
        get => _flagSavePath;
        set => _flagSavePath = value;
    }
    #endregion

    #region ReadParameters
    [Header("Read NetCDF Parameters")]
    [Tooltip("If false, files will be saved to /Data by default")]
    public bool useAbsReadDir = false;
    public string absReadDir = "D:/Projects/Unity/UnityProjects/TestWindNoiseSimulation/Data";
    public string readFileName = null;
    private string _readPath = null;

    public string readPath
    {
        get => _readPath;
        set => _readPath = value;
    }
    #endregion

    public void Init()
    {
        // Calculate derived parameters based on the user-defined parameters.
        _dx = physDomainSize.x / gridResX;
        _ds = _dx * _dx;
        _dv = _dx * _dx * _dx;

        int gridResY = Mathf.RoundToInt(physDomainSize.y / _dx);
        int gridResZ = Mathf.RoundToInt(physDomainSize.z / _dx);

        _gridRes = new int3(gridResX, gridResY, gridResZ);
        _presRes = new int3(gridResX, gridResY, gridResZ);
        if (gridType == GridType.Collocated)
            _velRes = new int3(gridResX, gridResY, gridResZ);
        else
            _velRes = new int3(gridResX + 1, gridResY + 1, gridResZ + 1);

        _nu = mu / den;

        _D = _dv / dt + 6 * _nu * _ds / _dx;
        _DInv = 1 / (_dv / dt + 6 * _nu * _ds / _dx);

        _currPhyTime = 0; // Current simulated physics time
        _currSimStep = 0; // Current total simulated steps.
        _numStepsSaved = 0; // Number of steps saved.

        // ----- Process save path -----
        string saveDir = "", readDir = "";

        if (useAbsSaveDir)
            saveDir = absSaveDir;
        else
            saveDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Data"));

        if (!Directory.Exists(saveDir))
            Directory.CreateDirectory(saveDir);

        fileName = $"Field_{DateTime.Now:yyyyMMdd_HHmmss}.nc";

        _savePath = Path.Combine(saveDir, fileName);
        _flagSavePath = Path.Combine(saveDir, flagFileName);

        if (useAbsReadDir)
            readDir = absReadDir;
        else
            readDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Data"));

        _readPath = Path.Combine(readDir, readFileName);
    }
}
