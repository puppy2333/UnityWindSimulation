using CesiumForUnity;
using System;
using System.Threading.Tasks;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.VFX;

public class FvmSolverController : MonoBehaviour
{
    public enum SimulationMode { CPU, GPU }
    public SimulationMode simulationMode;

    private Voxelizer voxelizer;
    private Voxelizer voxelizerBackGround;

    private IFvmSolver solver;
    private IFvmSolver solverBackGround;

    // Configuration file.
    public bool simulateBackGroundFlow = false;
    public FluidSimConfig cf;
    public FluidSimConfig bgcf;
    private RuntimeConfig rcf;

    // CPU visualizer
    public IVisualizer visualizer;
    public IVisualizer visualizerBackGround;

    public GameObject model;

    public TextAsset colormapCsv;

    public VisualEffect targetVfx;
    public VisualEffect flowDirArrow;
    Vector3 arrowPosition = Vector3.zero;

    // ----- Models can be added -----
    public GameObject model1;
    public GameObject model2;

    // ----- VFX exposed variable names -----
    // Exposed texture for arrow direction
    private string velDirSliceVfxName = "VelDirSlice";
    // Exposed texture for arrow length
    private string velFieldVfxName = "VelField";
    // Exposed physical domain size
    private string physDomainSizeVfxName = "PhysDomainSize";
    // Exposed Fluid field y position
    private string fluidFieldPosVfxName = "FluidFieldPos";
    // Exposed fluid field rotation
    private string fluidFieldRotVfxName = "FluidFieldRot";
    // Exposed Y slice physical position
    private string ySlicePhysPosVfxName = "YSlicePhysPos";
    // Exposed Y slice position (0~1)
    private string ySlicePosVfxName = "YSlicePos";
    // Exposed toggle for arrow length strategy
    private string vfxArrowLengthFollowVelMagVfxName = "VfxArrowLengthFollowVelMag";
    // Exposed arrow position
    private string arrowPosVfxName = "ArrowPos";

    // ----- Textures -----
    RenderTexture velTex, presTex, flagTex, nutTex;
    RenderTexture velDirSliceTex;
    RenderTexture velMagSliceTex;

    RenderTexture velTexBackGround, presTexBackGround, flagTexBackGround;

    // Visualization slider.
    private VisualizerSlider slicePosSlider;
    private VisualizerSlider backGroundSlicePosSlider;
    private VisualizerSlider orientationSlider;
    private VisualizerSlider timeSlider;

    private NcFlowFieldExporter exporter;

    public NcPlayBackManager playBackManager;

    int updateCount = 0;
    int backGroundFlowUpdateInterval = 1;

    // ----- UI canvas -----
    GameObject canvasObject;
    Canvas canvas;

    // ----- Input fields -----
    public TMP_InputField physFieldPosXField;
    public TMP_InputField physFieldPosYField;
    public TMP_InputField physFieldPosZField;
    public Button confirmPhysFieldPosButton;

    public Slider flowDirectionSlider;
    public Button confirmOrientationButtion;

    public Button vfxStartStopButton;

    public Button knmiButton;

    public TMP_InputField physDomainSizeXField;
    public TMP_InputField physDomainSizeYField;
    public TMP_InputField physDomainSizeZField;
    public Button confirmPhysDomainSizeButton;

    public Button model1Button;
    public Button model2Button;

    public Button exitButton;

    public TMP_Text knmiMsg;

    public TMP_InputField physFieldCoordXField;
    public TMP_InputField physFieldCoordYField;
    public TMP_InputField physFieldCoordZField;
    public Button confirmPhysFieldCoordButton;
    public TMP_Text coordMsg;

    // ----- Simulation status indicator -----
    private enum SimuStatus { Uninited, Running, Paused, Stopped };
    SimuStatus simuStatus = SimuStatus.Uninited;
    
    bool vfxStarted = false;

    // ----- Knmi api loader -----
    private KNMILoader knmiLoader;

    // ----- Cesium -----
    public CesiumGeoreference cesiumGeoreference;

    #region Initialization
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // ----- Set target frame rate -----
        Application.targetFrameRate = 60;

        // ----- Load configuration -----
        if (cf == null)
            throw new Exception("FluidSimConfig is not set. Please assign a configuration file.");
        cf.Init();

        // ----- Load background config -----
        if (simulateBackGroundFlow)
        {
            if (bgcf == null)
                throw new Exception("simulateBackGroundFlow is true, but background " +
                "FluidSimConfig is not set. Please assign a background configuration file.");
            bgcf.Init();
            backGroundFlowUpdateInterval = (int) math.round(bgcf.dt / cf.dt);
            Debug.Log("Background flow update interval: " + backGroundFlowUpdateInterval);
        }

        // ----- Initialize runtime config -----
        rcf = new RuntimeConfig(cf, new double3(cesiumGeoreference.longitude, cesiumGeoreference.latitude, cesiumGeoreference.height));

        // ----- Initialize canvas for sliders (legacy) -----
        initCanvas();

        if (cf.mode == Mode.SimulateAndVisualize)
        {
            // ----- This is only part of the init process. The other parts are inited in Update()
            // to be compatible with 3D tiles streaming -----

            // ----- Cameara setup -----
            Camera.main.fieldOfView = cf.cameraAngle;
            Camera.main.transform.SetPositionAndRotation(new Vector3(0, cf.cameraHeight, 0), Quaternion.Euler(45, 0, 0));

            // ----- Init solver -----
            solver = new FvmSolverGpu(cf, rcf);
            if (simulateBackGroundFlow)
                solverBackGround = new FvmSolverGpu(bgcf, rcf);

            // ----- Init voxelizer -----
            if (model != null && cf.solidType == SolidType.Model)
                voxelizer = new Voxelizer(cf, rcf, model);

            if (simulateBackGroundFlow)
                voxelizerBackGround = new Voxelizer(bgcf, rcf, model);

            // ----- Init visualizer -----
            velTex = (RenderTexture)solver.GetVelField();
            presTex = (RenderTexture)solver.GetPresField();
            flagTex = (RenderTexture)solver.GetFlagField();
            //nutTex = (RenderTexture)solver.GetNutField();
            visualizer = new VisualizerGpu(cf, rcf, velTex, presTex, flagTex);

            // ----- Load colormap -----
            if (colormapCsv != null)
            {
                visualizer.LoadColorMapFromCsv(colormapCsv.text);
                Debug.Log("Colormap loaded from CSV.");
                cf.colormapLoaded = true;
            }

            // ----- Init visualization position slider -----
            initSlicePosSlider();

            // ----- Init visualizer for background solver -----
            if (simulateBackGroundFlow)
            {
                velTexBackGround = (RenderTexture)solverBackGround.GetVelField();
                presTexBackGround = (RenderTexture)solverBackGround.GetPresField();
                flagTexBackGround = (RenderTexture)solverBackGround.GetFlagField();
                visualizerBackGround = new VisualizerGpu(bgcf, rcf, velTexBackGround, presTexBackGround, flagTexBackGround);

                // ----- Load colormap for background flow -----
                if (colormapCsv != null)
                {
                    visualizerBackGround.LoadColorMapFromCsv(colormapCsv.text);
                    Debug.Log("BackGround colormap loaded from CSV.");
                    bgcf.colormapLoaded = true;
                }

                // ----- Init visualization position slider -----
                initBackGroundSlicePosSlider();
            }

            // ----- Init exporter -----
            if ((cf.saveVelField || cf.savePresField) && cf.savePath != null)
            {
                exporter = new NcFlowFieldExporter(cf, velTex, presTex);
            }

            // ----- Init vfx -----
            if (cf.showVfx)
            {
                velDirSliceTex = visualizer.GetVelDirSliceTex();
                velMagSliceTex = visualizer.GetVelMagSliceTex();
            }

            // ----- Init vfx textures -----
            if (targetVfx != null && cf.showVfx)
            {
                targetVfx.SetTexture(velDirSliceVfxName, velDirSliceTex);
                targetVfx.SetTexture(velFieldVfxName, velTex);
            }

            // ----- Init vfx domain size -----
            if (cf.showVfx)
            {
                if (targetVfx == null)
                {
                    Debug.LogError("Target VFX is not assigned.");
                }
                else
                {
                    targetVfx.SetVector3(physDomainSizeVfxName, (Vector3)cf.physDomainSize);
                    targetVfx.SetVector3(fluidFieldPosVfxName, (Vector3)cf.physFieldPos);
                    targetVfx.SetBool(vfxArrowLengthFollowVelMagVfxName, cf.vFxArrowLengthFollowVelMag);
                }
            }
            // ----- Rotate the flow direction arrow -----
            arrowPosition = (Vector3)cf.physFieldPos + new Vector3(-cf.physDomainSize.x / 2, 0, 0);
            flowDirArrow.SetVector3(arrowPosVfxName, arrowPosition);

            // ----- Init knmi loader -----
            knmiLoader = new KNMILoader(cesiumGeoreference, cf, rcf);

            // ----- Add listeners to input fields -----
            physFieldPosXField.onEndEdit.AddListener(SetPhysFieldPosX);
            physFieldPosYField.onEndEdit.AddListener(SetPhysFieldPosY);
            physFieldPosZField.onEndEdit.AddListener(SetPhysFieldPosZ);
            confirmPhysFieldPosButton.onClick.AddListener(ConfirmPhysFieldPos);
            confirmOrientationButtion.onClick.AddListener(ConfirmPhysFieldOrientation);
            vfxStartStopButton.onClick.AddListener(VfxStartStop);
            flowDirectionSlider.onValueChanged.AddListener(SliderUpdateFlowFieldOrientation);
            knmiButton.onClick.AddListener(async () =>
            {
                await LoadKnmi();
            });
            model1Button.onClick.AddListener(LoadModel1);
            model2Button.onClick.AddListener(LoadModel2);
            exitButton.onClick.AddListener(QuitGame);
            physFieldCoordXField.onEndEdit.AddListener(SetPhysFieldCoordX);
            physFieldCoordYField.onEndEdit.AddListener(SetPhysFieldCoordY);
            physFieldCoordZField.onEndEdit.AddListener(SetPhysFieldCoordZ);
            confirmPhysFieldCoordButton.onClick.AddListener(ConfirmPhysFieldCoord);

            //physDomainSizeXField.onEndEdit.AddListener(SetPhysDomainSizeX);
            //physDomainSizeYField.onEndEdit.AddListener(SetPhysDomainSizeY);
            //physDomainSizeZField.onEndEdit.AddListener(SetPhysDomainSizeZ);
            //confirmPhysDomainSizeButton.onClick.AddListener(ConfirmPhysDomainSize);
        }
        else if (cf.mode == Mode.Visualize)
        {
            StartVisualize();

            initTimeSlider();
        }
        else if (cf.mode == Mode.OutputBuildingVoxels)
        {
            Voxelize();
        }
    }

    void initCanvas()
    {
        canvasObject = new GameObject("Canvas");
        canvasObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();
        canvas = canvasObject.GetComponent<Canvas>();
        canvas.pixelPerfect = false;
    }

    void initTimeSlider()
    {
        timeSlider = new VisualizerSlider(
                config: cf, canvas: canvas, initHandlePos: 0, anchorMin: new Vector2(0.5f, 0.0f), anchorMax: new Vector2(0.5f, 0.0f),
                sliderPos: cf.timeSliderPos, sliderSize: cf.timeSliderSize, Slider.Direction.LeftToRight
                );
        timeSlider.flowSlider.onValueChanged.AddListener(SliderUpdateVisualizationTime);
    }

    void initSlicePosSlider()
    {
        slicePosSlider = new VisualizerSlider(
            config: cf, canvas: canvas, initHandlePos: (float)cf.ySlice / (float)cf.gridRes.y, anchorMin: new Vector2(1.0f, 0.5f), anchorMax: new Vector2(1.0f, 0.5f),
            sliderPos: cf.slicePosSliderPos, sliderSize: cf.slicePosSliderSize, Slider.Direction.BottomToTop
            );
        slicePosSlider.flowSlider.onValueChanged.AddListener(SliderUpdateFlowFieldPosition);

        SliderUpdateFlowFieldPosition((float)cf.ySlice / (float)cf.gridRes.y);
    }

    void initBackGroundSlicePosSlider()
    {
        backGroundSlicePosSlider = new VisualizerSlider(
            config: bgcf, canvas: canvas, initHandlePos: (float)bgcf.ySlice / (float)bgcf.gridRes.y, 
            anchorMin: new Vector2(1.0f, 0.5f), anchorMax: new Vector2(1.0f, 0.5f),
            sliderPos: bgcf.slicePosSliderPos, sliderSize: bgcf.slicePosSliderSize, Slider.Direction.BottomToTop
            );
        backGroundSlicePosSlider.flowSlider.onValueChanged.AddListener(BackGroundSliderUpdateFlowFieldPosition);

        BackGroundSliderUpdateFlowFieldPosition((float)bgcf.ySlice / (float)bgcf.gridRes.y);
    }

    void Voxelize()
    {
        if (model != null && cf.solidType == SolidType.Model)
        {
            voxelizer = new Voxelizer(cf, rcf, model);
            int[] flags = voxelizer.VoxelizeMesh();
            
            FlagExporter flagExporter = new FlagExporter(cf.flagSavePath);
            flagExporter.ExportFlags(flags);
        }
    }

    void StartSimulateAndVisualize()
    {
        if (simulationMode == SimulationMode.CPU)
        {
            solver = new FvmSolverCpu(cf);
            visualizer = new VisualizerCpu(cf, solver);

            if (model != null && cf.solidType == SolidType.Model)
            {
                throw new NotImplementedException("Voxelizer is not implemented for CPU mode.");
            }
        }
        else if (simulationMode == SimulationMode.GPU)
        {
            // ----- Voxelization is executed here instead of Start() to make sure Cesium
            // buildings have been fully loaded -----
            if (model != null && cf.solidType == SolidType.Model)
            {
                int[] flags = voxelizer.VoxelizeMesh();
                solver.InitFlags(flags);

                if (simulateBackGroundFlow)
                {
                    int[] flagsBackGround = voxelizerBackGround.VoxelizeMesh();
                    solverBackGround.InitFlags(flagsBackGround);
                }
            }
        }
        else
        {
            Debug.LogError("Invalid simulation mode selected.");
            return;
        }
    }

    void StartVisualize()
    {
        if (simulationMode == SimulationMode.CPU)
        {
            throw new NotImplementedException("Data loading is not implemented for CPU mode.");
        }
        else if (simulationMode == SimulationMode.GPU)
        {
            // ----- Init playback manager -----
            playBackManager = new NcPlayBackManager(cf);

            // ----- Init visualizer -----
            RenderTexture velTex = playBackManager.velFieldTex;
            RenderTexture presTex = playBackManager.presFieldTex;
            RenderTexture flagTex = playBackManager.presFieldTex;

            visualizer = new VisualizerGpu(cf, rcf, velTex, presTex, flagTex);

            // ----- Load colormap -----
            if (visualizer != null && colormapCsv != null)
            {
                visualizer.LoadColorMapFromCsv(colormapCsv.text);
                Debug.Log("Colormap loaded from CSV.");
                cf.colormapLoaded = true;
            }
            else
            {
                cf.colormapLoaded = false;
            }

            // ----- Update visualization field -----
            visualizer.UpdateVis();
        }
        else
        {
            Debug.LogError("Invalid simulation mode selected.");
            return;
        }
    }
    #endregion

    #region Update
    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.V) && simuStatus != SimuStatus.Running)
        {
            if (simuStatus == SimuStatus.Uninited)
            {
                StartSimulateAndVisualize();
            }
            
            simuStatus = SimuStatus.Running;
        }

        if (simuStatus == SimuStatus.Running)
        {
            if (cf.mode == Mode.SimulateAndVisualize)
            {
                UpdateSimulateAndVisualize();
                //targetVfx.SendEvent("Spawn");
            }
            else if (cf.mode == Mode.Visualize)
            {
                UpdateVisualize();
            }
        }
    }

    void UpdateSimulateAndVisualize()
    {
        if ((cf.saveVelField || cf.savePresField) && cf.savePath != null)
        {
            if (updateCount % cf.saveInterval == 0 && updateCount >= cf.saveBeginStep)
            {
                exporter.EnqueueField();
            }
        }

        bool readyForRender = solver.Step();
        if (readyForRender)
        {
            if (cf.visualizeMode == VisualizeMode.Copy)
                visualizer.UpdateVis();
            updateCount++;
        }

        if (simulateBackGroundFlow && bgcf != null && bgcf.currSimStep * backGroundFlowUpdateInterval <= cf.currSimStep)
        {
            bool readyForRenderBG = solverBackGround.Step();
            if (readyForRenderBG)
            {
                if (bgcf.visualizeMode == VisualizeMode.Copy)
                    visualizerBackGround.UpdateVis();
            }
        }
    }

    void UpdateVisualize()
    {
        //visualizer.UpdateVis();
    }
    #endregion

    #region simulationDomainVisualization
    private void getFieldPos()
    {
        if (model == null)
            return;

        Renderer renderer = model.GetComponentInChildren<Renderer>();
        if (renderer == null)
        {
            UnityEngine.Debug.LogError("The building GO needs a Renderer component.");
            return;
        }

        cf.physModelPos = model.transform.position;
        
        if (cf.attachFieldToModelBottom)
        {
            float3 physFieldPos = cf.physFieldPos;
            physFieldPos.y += cf.physDomainSize.y / 2.0f - renderer.bounds.size.y / 2.0f;
            cf.physFieldPos = physFieldPos;
        }

        cf.fieldPositionLocated = true;
    }

    // Draw the gizmo of simulation domain in the editor.
    private void OnDrawGizmos()
    {
        if (!cf.showDomainBoundary)
            return;

        if (model != null && !cf.fieldPositionLocated)
            getFieldPos();

        // ----- Set wireframe angle -----
        if (rcf != null)
        {
            Quaternion rotation = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);
            Gizmos.matrix = Matrix4x4.TRS(cf.physFieldPos, rotation, Vector3.one);
        }
        else
        {
            Gizmos.matrix = Matrix4x4.Translate(cf.physFieldPos);
        }

        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, cf.physDomainSize);

        if (simulateBackGroundFlow) 
        {
            if (rcf != null)
            {
                Quaternion rotation = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);
                Gizmos.matrix = Matrix4x4.TRS(bgcf.physFieldPos, rotation, Vector3.one);
            }
            else
            {
                Gizmos.matrix = Matrix4x4.Translate(bgcf.physFieldPos);
            }

            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(Vector3.zero, bgcf.physDomainSize);
        }
    }
    #endregion

    #region SliderCallbacks
    public void SliderUpdateFlowFieldPosition(float value)
    {
        visualizer.UpdateQuadPosBySlider(value);
        visualizer.UpdateVis();

        // ----- Init vfx textures -----
        if (targetVfx != null && cf.showVfx)
        {
            float physFieldPosVisY = cf.physFieldPos.y * cf.visScale;
            float physDomainSizeVisY = cf.physDomainSize.y * cf.visScale;

            float quadPhysPosY = (physFieldPosVisY - physDomainSizeVisY / 2.0f) + value * physDomainSizeVisY;

            targetVfx.SetFloat(ySlicePosVfxName, value);
            targetVfx.SetFloat(ySlicePhysPosVfxName, quadPhysPosY);
        }
    }

    public void BackGroundSliderUpdateFlowFieldPosition(float value)
    {
        visualizerBackGround.UpdateQuadPosBySlider(value);
        visualizerBackGround.UpdateVis();
    }

    public void SliderUpdateFlowFieldOrientation(float value)
    {
        rcf.flowFieldOrientation = value * 360;
    }

    public void SliderUpdateVisualizationTime(float value)
    {
        playBackManager.UpdateTexture(value);
        visualizer.UpdateVis();
    }
    #endregion

    #region SettingsManagerCallbacks
    void SetPhysFieldPosX(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosX))
        {
            cf.physFieldPos.x = newPhysFieldPosX;
            Debug.Log("New PhysFieldPosX: " + cf.physFieldPos.x);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldPosX.");
        }
    }

    void SetPhysFieldPosY(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosY))
        {
            cf.physFieldPos.y = newPhysFieldPosY;
            Debug.Log("New PhysFieldPosY: " + cf.physFieldPos.y);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldPosY.");
        }
    }

    void SetPhysFieldPosZ(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosZ))
        {
            cf.physFieldPos.z = newPhysFieldPosZ;
            Debug.Log("New PhysFieldPosZ: " + cf.physFieldPos.z);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldPosZ.");
        }
    }

    void SetPhysFieldCoordX(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosX))
        {
            rcf.llhCoord.x = newPhysFieldPosX;
            Debug.Log("New PhysFieldCoordX: " + rcf.llhCoord.x);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldCoordX.");
        }
    }

    void SetPhysFieldCoordY(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosY))
        {
            rcf.llhCoord.y = newPhysFieldPosY;
            Debug.Log("New PhysFieldCoordY: " + rcf.llhCoord.y);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldCoordY.");
        }
    }

    void SetPhysFieldCoordZ(string input)
    {
        if (float.TryParse(input, out float newPhysFieldPosZ))
        {
            rcf.llhCoord.z = newPhysFieldPosZ;
            Debug.Log("New PhysFieldCoordZ: " + rcf.llhCoord.z);
        }
        else
        {
            Debug.LogError("Invalid input for PhysFieldCoordZ.");
        }
    }


    void ConfirmPhysFieldPos()
    {
        if (! simulateBackGroundFlow)
        {
            solver.InitVelPresFields();
        }
        else
        {
            solver.InitVelPresFieldsFromBackGroundFlow(bgcf, velTexBackGround, presTexBackGround);
        }
        visualizer.UpdateQuadPosByConfig();

        // ----- Re-init model and solver -----
        if (model != null && cf.solidType == SolidType.Model)
        {
            int[] flags = voxelizer.VoxelizeMesh();
            solver.InitFlags(flags);
        }
        visualizer.UpdateVis();

        if (cf.showVfx && targetVfx != null)
        {
            targetVfx.SetVector3(fluidFieldPosVfxName, (Vector3)cf.physFieldPos);
        }

        // ----- Rotate the flow direction arrow -----
        Quaternion rotation = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);
        arrowPosition = (Vector3)cf.physFieldPos + rotation * new Vector3(-cf.physDomainSize.x / 2, 0, 0);
        flowDirArrow.SetVector3(arrowPosVfxName, arrowPosition);

        Debug.Log("PhysFieldPos updated");

        simuStatus = SimuStatus.Paused;

        // ----- Update VFX -----
        if (cf.showVfx)
            targetVfx.SetVector3(fluidFieldPosVfxName, (Vector3)cf.physFieldPos);
    }

    void ConfirmPhysFieldCoord()
    {
        cesiumGeoreference.SetOriginLongitudeLatitudeHeight(rcf.llhCoord.x, rcf.llhCoord.y, rcf.llhCoord.z);
        coordMsg.text = "New origin coord: " + rcf.llhCoord.x.ToString() + ", " + rcf.llhCoord.y.ToString() + ", " + rcf.llhCoord.z.ToString();
    }

    void ConfirmPhysFieldOrientation()
    {
        // ----- Re-init fluid field and visualization quad -----
        solver.InitVelPresFields();
        visualizer.UpdateQuadOrientationByConfig();

        if (simulateBackGroundFlow && bgcf != null)
        {
            solverBackGround.InitVelPresFields();
            visualizerBackGround.UpdateQuadOrientationByConfig();
        }

        // ----- Re-voxelization -----
        if (model != null && cf.solidType == SolidType.Model)
        {
            int[] flags = voxelizer.VoxelizeMesh();
            solver.InitFlags(flags);

            if (simulateBackGroundFlow && bgcf != null)
            {
                int[] flagsBackGround = voxelizerBackGround.VoxelizeMesh();
                solverBackGround.InitFlags(flagsBackGround);
            }
        }

        // ----- Visualize the re-voxelized field -----
        visualizer.UpdateVis();
        if (simulateBackGroundFlow)
            visualizerBackGround.UpdateVis();

        // ----- VFX: notify orientation change -----
        flowDirArrow.SetFloat(fluidFieldRotVfxName, rcf.flowFieldOrientation);
        if (cf.showVfx && targetVfx != null)
            targetVfx.SetFloat(fluidFieldRotVfxName, rcf.flowFieldOrientation);

        // ----- VFX: rotate flow direction arrow -----
        Quaternion rotation = Quaternion.Euler(0f, rcf.flowFieldOrientation, 0f);
        arrowPosition = (Vector3)cf.physFieldPos + rotation * new Vector3(-cf.physDomainSize.x / 2, 0, 0);
        flowDirArrow.SetVector3(arrowPosVfxName, arrowPosition);

        Debug.Log("PhysField orientation updated");

        simuStatus = SimuStatus.Stopped;
    }

    void VfxStartStop()
    {
        if (vfxStarted)
        {
            targetVfx.SendEvent("StopSpawn");
            vfxStarted = false;
            Debug.Log("VFX stopped");
        }
        else
        {
            targetVfx.SendEvent("StartSpawn");
            vfxStarted = true;
            Debug.Log("VFX started");
        }
    }

    async Task LoadKnmi()
    {
        // ----- Load knmi data -----
        (float windDir, float windSpeed) = await knmiLoader.GetWindDirMagIDW();

        knmiMsg.text = "KNMI wind direction: " + windDir.ToString("F2") + "°, wind speed: " + windSpeed.ToString("F2") + " m/s";

        // ----- Update wind speed -----
        rcf.windSpeed = windSpeed;

        // ----- Update wind direction -----
        float normalizedWindRotAng = ((windDir + 90) % 360) / 360;
        SliderUpdateFlowFieldOrientation(normalizedWindRotAng);

        // ----- Reinitialize velocity boundary condition -----
        solver.SetFixedValueVelBndCond();
        if (simulateBackGroundFlow)
        {
            solverBackGround.SetFixedValueVelBndCond();
        }

        ConfirmPhysFieldOrientation();
    }

    void SetPhysDomainSizeX(string input)
    {
        if (float.TryParse(input, out float newPhysDomainSizeX))
        {
            cf.physDomainSize.x = newPhysDomainSizeX;
            Debug.Log("New PhysDomainSizeX: " + cf.physDomainSize.x);
        }
        else
        {
            Debug.LogError("Invalid input for PhysDomainSizeX.");
        }
    }

    void SetPhysDomainSizeY(string input)
    {
        if (float.TryParse(input, out float newPhysDomainSizeY))
        {
            cf.physDomainSize.y = newPhysDomainSizeY;
            Debug.Log("New PhysDomainSizeY: " + cf.physDomainSize.y);
        }
        else
        {
            Debug.LogError("Invalid input for PhysDomainSizeY.");
        }
    }

    void SetPhysDomainSizeZ(string input)
    {
        if (float.TryParse(input, out float newPhysDomainSizeZ))
        {
            cf.physDomainSize.z = newPhysDomainSizeZ;
            Debug.Log("New PhysDomainSizeZ: " + cf.physDomainSize.z);
        }
        else
        {
            Debug.LogError("Invalid input for PhysDomainSizeZ.");
        }
    }

    void ConfirmPhysDomainSize()
    {
        // ----- Recalculate simulation parameters -----
        cf.Init();
        solver.ChangePhysDomainSize();
        visualizer.UpdateQuadPosByConfig();

        // ----- Re-init model and solver -----
        if (model != null && cf.solidType == SolidType.Model)
        {
            int[] flags = voxelizer.VoxelizeMesh();
            solver.InitFlags(flags);
        }

        Debug.Log("PhysFieldPos updated");
    }
    #endregion

    public void LoadModel1()
    {
        if (model1 == null)
        {
            Debug.LogError("Model1 not specified!");
            return;
        }

        GameObject instance = Instantiate(model1);
        Vector3 originalPos = instance.transform.position;
        instance.transform.position = new Vector3(cf.physFieldPos.x, originalPos.y, cf.physFieldPos.z);
        instance.SetActive(true);

        Debug.Log("Model1 loaded!");
    }

    public void LoadModel2()
    {
        if (model2 == null)
        {
            Debug.LogError("Model1 not specified!");
            return;
        }

        GameObject instance = Instantiate(model2);
        Vector3 originalPos = instance.transform.position;
        instance.transform.position = new Vector3(cf.physFieldPos.x, originalPos.y, cf.physFieldPos.z);
        instance.SetActive(true);

        Debug.Log("Model2 loaded!");
    }

    #region Cleanup
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        Debug.Log("Application stopped in editor mode");

#else
            Application.Quit();
#endif
    }

    void OnDestroy()
    {
        if (cf.mode == Mode.SimulateAndVisualize)
        {
            if ((cf.saveVelField || cf.savePresField) && cf.savePath != null)
            {
                exporter.CloseNcFile();
            }
        }
        else if (cf.mode == Mode.Visualize)
        {
            playBackManager.CloseNcFile();
        }
        
    }
    #endregion
}
