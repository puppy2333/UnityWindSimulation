using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class SampleLineData
{
    public float currPhyTime;

    public float[] sampleData;

    public SampleLineData(float currPhyTimeIn, float[] sampleDataIn)
    {
        currPhyTime = currPhyTimeIn;
        sampleData = sampleDataIn;
    }
}


public class NcSampleLineExporter
{
    #region ReferenceVariables
    FluidSimConfig cf;
    RuntimeConfig rcf;

    IFvmSolver solver;

    RenderTexture sampleLinesGpu;
    int numSampleLines;
    int maxNumSamples;
    
    int numStepsSaved = 0;
    #endregion

    #region NetCDFVariables
    int fileId;

    // MetaData
    float[] dtList;

    // Dimension IDs
    int dimTimeId;
    int dimLineId;
    int dimSampleId;

    int[] dimTimeIdArray = new int[1];
    int[] dimLineIdArray = new int[1];
    int[] dimSampleIdArray = new int[1];

    int[] dimSampleDataIdArray = new int[3];

    // Variable IDs
    int varTimeId;
    int varLineId;
    int varSampleId;

    int varSampleDataId;

    // Variable values
    float[] timeValues;
    float[] lineValues;
    float[] sampleValues;

    // For appending data
    IntPtr[] timeWriteStart = new IntPtr[1];
    IntPtr[] timeWriteCount = new IntPtr[1];

    IntPtr[] sampleDataWriteStart = new IntPtr[3];
    IntPtr[] sampleDataWriteCount = new IntPtr[3];
    #endregion

    #region ErrorHandling
    int status;
    string errorMsg;
    #endregion

    #region MultiThreadingVariables
    ConcurrentQueue<SampleLineData> sampleLineQueue = new ConcurrentQueue<SampleLineData>();

    private Thread ncWriterThread;
    #endregion

    #region Initialization
    public NcSampleLineExporter(
        FluidSimConfig configIn, RuntimeConfig rcfIn,
        RenderTexture sampleLines,
        float[] facePosXArrayIn = null, float[] facePosYArrayIn = null, float[] facePosZArrayIn = null)
    {
        cf = configIn;
        rcf = rcfIn;

        if (cf.mode == Mode.SimulateAndVisualize)
        {
            sampleLinesGpu = sampleLines;
            numSampleLines = cf.sampleLines.Count;
            maxNumSamples = cf.sampleLines.Max(line => line.numSamples);

            MallocNcVariables();

            SetUpNcFile();

            LaunchNcWriteThread();
        }
    }

    void FillSampleCoords()
    {
        for (int line = 0; line < numSampleLines; line++)
        {
            lineValues[line] = line;
        }
        for (int sample = 0; sample < maxNumSamples; sample++)
        {
            sampleValues[sample] = sample;
        }
    }

    void MallocNcVariables()
    {
        dtList = new float[1];

        timeValues = new float[1];
        lineValues = new float[numSampleLines];
        sampleValues = new float[maxNumSamples];
        FillSampleCoords();

        timeWriteStart[0] = (IntPtr)0;
        timeWriteCount[0] = (IntPtr)1;

        sampleDataWriteStart[0] = (IntPtr)0;
        sampleDataWriteStart[1] = (IntPtr)0;
        sampleDataWriteStart[2] = (IntPtr)0;

        sampleDataWriteCount[0] = (IntPtr)1;
        sampleDataWriteCount[1] = (IntPtr)maxNumSamples;
        sampleDataWriteCount[2] = (IntPtr)numSampleLines;
    }

    void SetUpNcFile()
    {
        // Create NetCDF file.
        status = CsNetCDF.NetCDF.nc_create(cf.savePath2, CsNetCDF.NetCDF.CreateMode.NC_CLOBBER, out fileId);
        CheckError("Creat NetCDF file error: ");

        // Define dimensions.
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "time", (IntPtr)CsNetCDF.NetCDF.NC_UNLIMITED, out dimTimeId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "line", (IntPtr)numSampleLines, out dimLineId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "sample", (IntPtr)maxNumSamples, out dimSampleId);
        CheckError("Define dimension error: ");

        dimTimeIdArray[0] = dimTimeId;
        dimLineIdArray[0] = dimLineId;
        dimSampleIdArray[0] = dimSampleId;

        // Define meta data.
        string gridLayoutType = cf.grid.GetType().ToString();
        status = CsNetCDF.NetCDF.nc_put_att_text(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "gridLayoutType", (IntPtr)gridLayoutType.Length, gridLayoutType);
        CheckError("Write gridLayoutType error: ");

        dtList[0] = cf.dt;
        status = CsNetCDF.NetCDF.nc_put_att_float(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "dt", CsNetCDF.NetCDF.nc_type.NC_FLOAT, (IntPtr)1, dtList);
        CheckError("Write dt error: ");

        // Define coordinate variables.
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "time", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimTimeIdArray, out varTimeId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "line", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimLineIdArray, out varLineId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "sample", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimSampleIdArray, out varSampleId);
        CheckError("Define coordinate variables error: ");

        dimSampleDataIdArray[0] = dimTimeId;
        //dimSampleDataIdArray[1] = dimLineId;
        //dimSampleDataIdArray[2] = dimSampleId;
        dimSampleDataIdArray[1] = dimSampleId;
        dimSampleDataIdArray[2] = dimLineId;

        // Define sample data field.
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "sampleData", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 3, dimSampleDataIdArray, out varSampleDataId);
        CheckError("Define field variables: ");

        status = CsNetCDF.NetCDF.nc_enddef(fileId);
        CheckError("End define error: ");

        // Write coordinate variables.
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varLineId, lineValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varSampleId, sampleValues);
        CheckError("Write dimension error: ");
    }

    void LaunchNcWriteThread()
    {
        ncWriterThread = new Thread(NcWriterLoop);
        ncWriterThread.IsBackground = true;
        ncWriterThread.Start();
    }

    public void CloseNcFile()
    {
        while (sampleLineQueue.Count > 0)
        {
            UnityEngine.Debug.Log("Waiting for sample line queue to be empty before closing the file. Current queue length: " + sampleLineQueue.Count);
            Thread.Sleep(500);
        }

        status = CsNetCDF.NetCDF.nc_close(fileId);
        CheckError("Close file error: ");
    }
    #endregion

    #region WriteDataFuncs
    public void EnqueueSampleLine()
    {
        SampleLineData sampleLineData = new SampleLineData(cf.currPhyTime, null);

        var sampleNativeArray = new NativeArray<float>(
            numSampleLines * maxNumSamples, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);

        AsyncGPUReadback.RequestIntoNativeArray(ref sampleNativeArray, sampleLinesGpu, 0, request =>
        {
            if (request.hasError)
            {
                Debug.LogError("GPU sample line readback error");
                sampleNativeArray.Dispose();
                return;
            }

            sampleLineData.sampleData = sampleNativeArray.ToArray();
            sampleNativeArray.Dispose();

            sampleLineQueue.Enqueue(sampleLineData);
        });

        Debug.Log("Enqueued sample line at time: " + cf.currPhyTime + "s, step: " + cf.currSimStep + ". Current queue length: " + sampleLineQueue.Count + " numStepsSaved: " + numStepsSaved);
    }

    private void NcWriterLoop()
    {
        while (true)
        {
            SampleLineData sampleLineData;
            if (sampleLineQueue.TryDequeue(out sampleLineData))
            {
                Debug.Log(string.Join(", ", sampleLineData.sampleData));

                // Write current time.
                timeValues[0] = sampleLineData.currPhyTime;
                timeWriteStart[0] = (IntPtr)numStepsSaved;
                status = CsNetCDF.NetCDF.nc_put_vara_float(fileId, varTimeId, timeWriteStart, timeWriteCount, timeValues);

                // Write sample data.
                sampleDataWriteStart[0] = (IntPtr)numStepsSaved;
                status = CsNetCDF.NetCDF.nc_put_vara_float(fileId, varSampleDataId, sampleDataWriteStart, sampleDataWriteCount, sampleLineData.sampleData);
                CheckError("Sample data write error: ");

                numStepsSaved++;
            }
            else
            {
                Thread.Sleep(1000);
            }
        }
    }
    #endregion

    #region UtilFuncs
    void CheckError(string errorPos)
    {
        if (status != 0)
        {
            errorMsg = CsNetCDF.NetCDF.nc_strerror(status);
            Debug.LogError(errorPos + errorMsg);
        }
    }
    #endregion

}


