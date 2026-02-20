using System;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

public class NcPlayBackManager
{
    #region SimulationVariables
    FluidSimConfig cf;

    float dx;

    public IntPtr velResXIntPtr;
    public IntPtr velResYIntPtr;
    public IntPtr velResZIntPtr;
    public int3 velRes;

    public IntPtr presResXIntPtr;
    public IntPtr presResYIntPtr;
    public IntPtr presResZIntPtr;
    public int3 presRes;
    #endregion

    #region NetCDFVariables
    int fileId;

    int dimTimeId;
    int dimVelXId;
    int dimVelYId;
    int dimVelZId;
    int dimPresXId;
    int dimPresYId;
    int dimPresZId;

    int varTimeId;
    int varVelXId;
    int varVelFieldId;
    int varPresFieldId;

    int status;
    string errorMsg;

    public IntPtr numFramesIntPtr;
    public int numFrames;
    public float[] timePlayBack;

    public float[] velXPlayBack;

    public float[] velFieldPlayBack;
    public float[] presFieldPlayBack;

    IntPtr[] velFieldStart = new IntPtr[5];
    IntPtr[] velFieldCount = new IntPtr[5];
    IntPtr[] presFieldStart = new IntPtr[4];
    IntPtr[] presFieldCount = new IntPtr[4];
    #endregion

    #region TextureVariables
    ComputeShader playBackShader;

    int playBackKernel;

    public RenderTexture velFieldTex;
    public RenderTexture presFieldTex;

    ComputeBuffer velFieldBuffer;
    ComputeBuffer presFieldBuffer;
    #endregion

    public NcPlayBackManager(FluidSimConfig configIn)
    {
        cf = configIn;

        ReadNcFile();

        velFieldStart[0] = (IntPtr)0;
        velFieldStart[1] = (IntPtr)0;
        velFieldStart[2] = (IntPtr)0;
        velFieldStart[3] = (IntPtr)0;
        velFieldStart[4] = (IntPtr)0;

        velFieldCount[0] = (IntPtr)1;
        //velFieldCount[1] = (IntPtr)velRes.x;
        //velFieldCount[2] = (IntPtr)velRes.y;
        //velFieldCount[3] = (IntPtr)velRes.z;
        velFieldCount[1] = (IntPtr)velRes.z;
        velFieldCount[2] = (IntPtr)velRes.y;
        velFieldCount[3] = (IntPtr)velRes.x;
        velFieldCount[4] = (IntPtr)4;

        presFieldStart[0] = (IntPtr)0;
        presFieldStart[1] = (IntPtr)0;
        presFieldStart[2] = (IntPtr)0;
        presFieldStart[3] = (IntPtr)0;

        presFieldCount[0] = (IntPtr)1;
        //presFieldCount[1] = (IntPtr)presRes.x;
        //presFieldCount[2] = (IntPtr)presRes.y;
        //presFieldCount[3] = (IntPtr)presRes.z;
        presFieldCount[1] = (IntPtr)presRes.z;
        presFieldCount[2] = (IntPtr)presRes.y;
        presFieldCount[3] = (IntPtr)presRes.x;

        InitShaders();

        MallocTextures();

        UpdateTexture(0);
    }

    public void InitShaders()
    {
        // Find shader file.
        playBackShader = Resources.Load<ComputeShader>("Shaders/PlayBackShaders/PlayBackShader");

        // Set shader parameters.
        playBackShader.SetInts("velRes", velRes.x, velRes.y, velRes.z);
        playBackShader.SetInts("presRes", presRes.x, presRes.y, presRes.z);

        // Register kernels.
        playBackKernel = playBackShader.FindKernel("CSPlayBack");
    }

    public void ReadNcFile()
    {
        // Open NetCDF file.
        Debug.Log("Opening NetCDF file: " + cf.readPath);
        status = CsNetCDF.NetCDF.nc_open(cf.readPath, CsNetCDF.NetCDF.OpenMode.NC_NOWRITE, out fileId);
        CheckError("Creat NetCDF file error: ");

        // Read meta data.
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "time", out dimTimeId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "velX", out dimVelXId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "velY", out dimVelYId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "velZ", out dimVelZId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "presX", out dimPresXId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "presY", out dimPresYId);
        status = CsNetCDF.NetCDF.nc_inq_dimid(fileId, "presZ", out dimPresZId);
        CheckError("Read meta data error: ");

        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimTimeId, out numFramesIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimVelXId, out velResXIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimVelYId, out velResYIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimVelZId, out velResZIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimPresXId, out presResXIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimPresYId, out presResYIntPtr);
        status = CsNetCDF.NetCDF.nc_inq_dimlen(fileId, dimPresZId, out presResZIntPtr);
        CheckError("Read dimension length error: ");

        numFrames = (int)numFramesIntPtr;
        velRes = new int3((int)velResXIntPtr, (int)velResYIntPtr, (int)velResZIntPtr);
        presRes = new int3((int)presResXIntPtr, (int)presResYIntPtr, (int)presResZIntPtr);

        // Read grid layout type.
        status = CsNetCDF.NetCDF.nc_inq_attlen(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "gridLayoutType", out IntPtr gridLayoutTypeLength);
        Debug.Log("Read length of grid layout type string: " + gridLayoutTypeLength);

        StringBuilder gridLayoutType = new((int)gridLayoutTypeLength);
        status = CsNetCDF.NetCDF.nc_get_att_text(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "gridLayoutType", gridLayoutType);

        if (gridLayoutType.ToString() == GridType.Collocated.ToString())
        {
            cf.gridType = GridType.Collocated;
        }
        else if (gridLayoutType.ToString() == GridType.Staggered.ToString())
        {
            cf.gridType = GridType.Staggered;
        }
        else
        {
            Debug.LogError("Unknown grid layout type in the NetCDF file: " + gridLayoutType.ToString());
        }

        // Allocate memory for playback data.
        timePlayBack = new float[numFrames];
        velXPlayBack = new float[velRes.x];
        velFieldPlayBack = new float[velRes.x * velRes.y * velRes.z * 4];
        presFieldPlayBack = new float[presRes.x * presRes.y * presRes.z];

        // Read time series.
        status = CsNetCDF.NetCDF.nc_inq_varid(fileId, "time", out varTimeId);
        status = CsNetCDF.NetCDF.nc_get_var_float(fileId, varTimeId, timePlayBack);
        CheckError("Read time data error: ");

        // Read grid data.
        status = CsNetCDF.NetCDF.nc_inq_varid(fileId, "velX", out varVelXId);
        status = CsNetCDF.NetCDF.nc_get_var_float(fileId, varVelXId, velXPlayBack);
        CheckError("Read velX data error: ");
        dx = velXPlayBack[1] - velXPlayBack[0];

        // Read velocity and pressure field data.
        status = CsNetCDF.NetCDF.nc_inq_varid(fileId, "velField", out varVelFieldId);
        status = CsNetCDF.NetCDF.nc_inq_varid(fileId, "presField", out varPresFieldId);
        status = CsNetCDF.NetCDF.nc_get_vara_float(fileId, varVelFieldId, velFieldStart, velFieldCount, velFieldPlayBack);
        status = CsNetCDF.NetCDF.nc_get_vara_float(fileId, varPresFieldId, presFieldStart, presFieldCount, presFieldPlayBack);
        CheckError("Read variable data error: ");

        Debug.Log("Successfully read NetCDF file: " + cf.readPath);
        Debug.Log("Number of frames: " + numFrames);
        Debug.Log("Vel resolution: " + velRes.x + " x " + velRes.y + " x " + velRes.z);
        Debug.Log("Pres resolution: " + presRes.x + " x " + presRes.y + " x " + presRes.z);
        Debug.Log("Grid spacing dx: " + dx);
    }

    public void MallocTextures()
    {
        velFieldBuffer = new ComputeBuffer(velRes.x * velRes.y * velRes.z * 4, sizeof(float));
        presFieldBuffer = new ComputeBuffer(presRes.x * presRes.y * presRes.z, sizeof(float));

        velFieldTex = CreateRenderTexture(new int3(velRes.x, velRes.y, velRes.z), RenderTextureFormat.ARGBFloat);
        presFieldTex = CreateRenderTexture(new int3(presRes.x, presRes.y, presRes.z), RenderTextureFormat.RFloat);
    }

    public void UpdateTexture(float sliderPos)
    {
        int currentFrame = Mathf.RoundToInt(sliderPos * (numFrames - 1));

        velFieldStart[0] = (IntPtr)currentFrame;
        presFieldStart[0] = (IntPtr)currentFrame;
        status = CsNetCDF.NetCDF.nc_get_vara_float(fileId, varVelFieldId, velFieldStart, velFieldCount, velFieldPlayBack);
        status = CsNetCDF.NetCDF.nc_get_vara_float(fileId, varPresFieldId, presFieldStart, presFieldCount, presFieldPlayBack);

        velFieldBuffer.SetData(velFieldPlayBack);
        presFieldBuffer.SetData(presFieldPlayBack);

        playBackShader.SetBuffer(playBackKernel, "velFieldBuffer", velFieldBuffer);
        playBackShader.SetBuffer(playBackKernel, "presFieldBuffer", presFieldBuffer);
        playBackShader.SetTexture(playBackKernel, "velFieldTex", velFieldTex);
        playBackShader.SetTexture(playBackKernel, "presFieldTex", presFieldTex);
        playBackShader.Dispatch(playBackKernel, (velRes.x + 7) / 8, (velRes.y + 7) / 8, (velRes.z + 7) / 8);
    }

    public void CloseNcFile()
    {
        status = CsNetCDF.NetCDF.nc_close(fileId);
    }

    void CheckError(string errorPos)
    {
        if (status != 0)
        {
            errorMsg = CsNetCDF.NetCDF.nc_strerror(status);
            Debug.LogError(errorPos + errorMsg);
        }
    }

    RenderTexture CreateRenderTexture(int3 dim, RenderTextureFormat format)
    {
        RenderTexture tex = new(dim.x, dim.y, 0, format)
        {
            volumeDepth = dim.z,
            enableRandomWrite = true,
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            filterMode = FilterMode.Point
        };
        tex.Create();
        return tex;
    }
}
