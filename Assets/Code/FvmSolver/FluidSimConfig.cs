using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEngine.Rendering.HighDefinition.ScalableSettingLevelParameter;

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

public enum ConvectScheme
{
    CDS,
    UDS,
    LUST,
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

public enum SampleAxis
{
    X,
    Y,
    Z
}

[Serializable]
public abstract class Grid
{
}

[Serializable]
public class UniformGrid : Grid
{
    public float3 physDomainSize = new(80f, 40f, 40f);
    public int numCellsX = 80;
}

[Serializable]
public class NonUniformGridAuto : Grid
{
    public float3 unifRegionPhysDomainSize = new(200f, 20f, 200f);
    public int unifRegionNumCellsX = 8;
    public float3 stretchFactor = new(1.08f, 1.08f, 1.08f);
}

[Serializable]
public class NonUniformGridDefault : Grid
{
    public float3 userDefinedPhysDomainSize = new(80, 40, 40);
    public float3 unifRegionPhysStartOffset = new(20f, 0f, 18f);
    public float3 unifRegionPhysDomainSize = new(4f, 8f, 4f);
    public int unifRegionNumCellsX = 8;
    public float3 stretchFactor = new(1.08f, 1.08f, 1.08f);

    public float3 userBoxStartPhysOffset = new(20f, 0f, 18f);
    public float3 userBoxEndPhysOffset = new(24f, 8f, 22f);
}

[Serializable]
public class SampleLineConfig
{
    public SampleAxis sampleAxis = SampleAxis.Y;
    public Vector2 fixedCoords = new(25f, 20f);
    public Vector2 sampleRange = new(0.25f, 39.75f);
    public int numSamples = 80;
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

    [SerializeReference, SubclassSelector]
    public Grid grid = new UniformGrid();

    [Header("Simulation Parameters")]

    public float dt = 0.1f;
    public float mu = 0.005f; // Dynamic viscosity
    public float den = 1.0f;
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
    public ConvectScheme convectScheme = ConvectScheme.CDS;
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

    #region BackGroundFlowParameters
    bool isBackGroundFlow = false;
    #endregion

    #region MatrixSolverParameters
    public bool calResidual = false;
    public bool calMaxCfl = false;

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
    private float _nu; // Kinematic viscosity
    private float _currPhyTime = 0; // Current simulated physics time
    private int _currSimStep = 0; // Current total simulated steps.
    private int _numStepsSaved = 0; // Number of steps saved.

    public float nu => _nu;
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
    private int3 _boxStartIdx;
    private int3 _boxEndIdx;
    public int3 boxStartIdx
    {
        get => _boxStartIdx;
        set => _boxStartIdx = value;
    }
    public int3 boxEndIdx
    {
        get => _boxEndIdx;
        set => _boxEndIdx = value;
    }
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
    public bool saveSampleLine = false;
    [Tooltip("If false, files will be saved to /Data by default")]
    public bool useAbsSaveDir = false;
    [Tooltip("Only valid if useAbsSavePath is true")]
    public string absSaveDir = "D:/Projects/Unity/UnityProjects/TestWindNoiseSimulation/Data";
    private string fileName = null;
    private string _savePath = null;
    private string _savePath2 = null;
    public int saveBeginStep = 1000;
    public int saveInterval = 1000;
    public string flagFileName = null;
    private string _flagSavePath = null;

    // ----- Sample lines -----
    public int sampleLineSaveBeginStep = 0;
    public int sampleLineSaveInterval = 10;
    public List<SampleLineConfig> sampleLines = new List<SampleLineConfig>();

    public string savePath
    {
        get => _savePath;
        set => _savePath = value;
    }
    public string savePath2
    {
        get => _savePath2;
        set => _savePath2 = value;
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

    private void _InitNonUnifGridOpenFOAMConfig()
    {
        throw new NotImplementedException();
    }

    public void Init()
    {
        _nu = mu / den;

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

        string fileName2 = $"Field_{DateTime.Now:yyyyMMdd_HHmmss}_sampleline.nc";

        _savePath = Path.Combine(saveDir, fileName);
        _savePath2 = Path.Combine(saveDir, fileName2);
        _flagSavePath = Path.Combine(saveDir, flagFileName);

        if (useAbsReadDir)
            readDir = absReadDir;
        else
            readDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Data"));

        _readPath = Path.Combine(readDir, readFileName);
    }
}
