using System;
using System.Collections.Concurrent;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class FieldData
{
    public float currPhyTime;

    public float[] velField;
    public float[] presField;

    public bool velReady = false;
    public bool presReady = false;

    public FieldData(float currPhyTimeIn, float[] velFieldIn, float[] presFieldIn)
    {
        currPhyTime = currPhyTimeIn;
        velField = velFieldIn;
        presField = presFieldIn;
    }
}

public class NcFlowFieldExporter
{
    #region ReferenceVariables
    FluidSimConfig cf;

    IFvmSolver solver;

    RenderTexture velFieldGpu;
    RenderTexture presFieldGpu;
    #endregion

    #region NetCDFVariables
    int fileId;

    // MetaData
    float[] dtList;

    // Dimension IDs
    int dimTimeId;
    int dimVelXId;
    int dimVelYId;
    int dimVelZId;
    int dimVelVecId;
    int dimPresXId;
    int dimPresYId;
    int dimPresZId;

    int[] dimTimeIdArray = new int[1];
    int[] dimVelXIdArray = new int[1];
    int[] dimVelYIdArray = new int[1];
    int[] dimVelZIdArray = new int[1];
    int[] dimVelVecIdArray = new int[1];
    int[] dimPresXIdArray = new int[1];
    int[] dimPresYIdArray = new int[1];
    int[] dimPresZIdArray = new int[1];

    int[] dimVelFieldIdArray = new int[5];
    int[] dimPresFieldIdArray = new int[4];

    // Variable IDs
    int varTimeId;
    int varVelXId;
    int varVelYId;
    int varVelZId;
    int varVelVecId;
    int varPresXId;
    int varPresYId;
    int varPresZId;

    int varVelFieldId;
    int varPresFieldId;

    // Variable values
    float[] timeValues;
    float[] velXValues;
    float[] velYValues;
    float[] velZValues;
    int[] velVecValues;
    float[] presXValues;
    float[] presYValues;
    float[] presZValues;

    // For appending data
    IntPtr[] timeWriteStart = new IntPtr[1];
    IntPtr[] timeWriteCount = new IntPtr[1];

    IntPtr[] velFieldWriteStart = new IntPtr[5];
    IntPtr[] velFieldWriteCount = new IntPtr[5];

    IntPtr[] presFieldWriteStart = new IntPtr[4];
    IntPtr[] presFieldWriteCount = new IntPtr[4];
    #endregion

    #region ErrorHandling
    int status;
    string errorMsg;
    #endregion

    #region MultiThreadingVariables
    ConcurrentQueue<FieldData> fieldQueue = new ConcurrentQueue<FieldData>();

    private Thread ncWriterThread;
    #endregion

    #region Initialization
    public NcFlowFieldExporter(FluidSimConfig configIn, RenderTexture velField, RenderTexture presField)
    {
        cf = configIn;

        if (cf.mode == Mode.SimulateAndVisualize)
        {
            velFieldGpu = velField;
            presFieldGpu = presField;

            MallocNcVariables();

            SetUpNcFile();

            LaunchNcWriteThread();
        }
    }

    void MallocNcVariables()
    {
        dtList = new float[1];

        timeValues = new float[1];
        velXValues = new float[cf.velRes.x];
        velYValues = new float[cf.velRes.y];
        velZValues = new float[cf.velRes.z];
        velVecValues = new int[4];
        presXValues = new float[cf.presRes.x];
        presYValues = new float[cf.presRes.y];
        presZValues = new float[cf.presRes.z];

        for (int x = 0; x < cf.velRes.x; x++)
        {
            velXValues[x] = (x + 0.5f) * cf.dx;
        }
        for (int y = 0; y < cf.velRes.y; y++)
        {
            velYValues[y] = (y + 0.5f) * cf.dx;
        }
        for (int z = 0; z < cf.velRes.z; z++)
        {
            velZValues[z] = (z + 0.5f) * cf.dx;
        }
        for (int x = 0; x < cf.presRes.x; x++)
        {
            presXValues[x] = (x + 0.5f) * cf.dx;
        }
        for (int y = 0; y < cf.presRes.y; y++)
        {
            presYValues[y] = (y + 0.5f) * cf.dx;
        }
        for (int z = 0; z < cf.presRes.z; z++)
        {
            presZValues[z] = (z + 0.5f) * cf.dx;
        }

        velVecValues[0] = 0;
        velVecValues[1] = 1;
        velVecValues[2] = 2;
        velVecValues[3] = 3;

        timeWriteStart[0] = (IntPtr)0;
        timeWriteCount[0] = (IntPtr)1;

        velFieldWriteStart[0] = (IntPtr)0;
        velFieldWriteStart[1] = (IntPtr)0;
        velFieldWriteStart[2] = (IntPtr)0;
        velFieldWriteStart[3] = (IntPtr)0;
        velFieldWriteStart[4] = (IntPtr)0;

        velFieldWriteCount[0] = (IntPtr)1;
        velFieldWriteCount[1] = (IntPtr)cf.velRes.z;
        velFieldWriteCount[2] = (IntPtr)cf.velRes.y;
        velFieldWriteCount[3] = (IntPtr)cf.velRes.x;
        velFieldWriteCount[4] = (IntPtr)4;

        presFieldWriteStart[0] = (IntPtr)0;
        presFieldWriteStart[1] = (IntPtr)0;
        presFieldWriteStart[2] = (IntPtr)0;
        presFieldWriteStart[3] = (IntPtr)0;

        presFieldWriteCount[0] = (IntPtr)1;
        presFieldWriteCount[1] = (IntPtr)cf.presRes.z;
        presFieldWriteCount[2] = (IntPtr)cf.presRes.y;
        presFieldWriteCount[3] = (IntPtr)cf.presRes.x;
    }

    void SetUpNcFile()
    {
        // Create NetCDF file.
        status = CsNetCDF.NetCDF.nc_create(cf.savePath, CsNetCDF.NetCDF.CreateMode.NC_CLOBBER, out fileId);
        CheckError("Creat NetCDF file error: ");

        // Define dimensions.
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "time", (IntPtr)CsNetCDF.NetCDF.NC_UNLIMITED, out dimTimeId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "velX", (IntPtr)cf.velRes.x, out dimVelXId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "velY", (IntPtr)cf.velRes.y, out dimVelYId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "velZ", (IntPtr)cf.velRes.z, out dimVelZId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "velVec", (IntPtr)4, out dimVelVecId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "presX", (IntPtr)cf.presRes.x, out dimPresXId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "presY", (IntPtr)cf.presRes.y, out dimPresYId);
        status = CsNetCDF.NetCDF.nc_def_dim(fileId, "presZ", (IntPtr)cf.presRes.z, out dimPresZId);
        CheckError("Define dimension error: ");

        dimTimeIdArray[0] = dimTimeId;
        dimVelXIdArray[0] = dimVelXId;
        dimVelYIdArray[0] = dimVelYId;
        dimVelZIdArray[0] = dimVelZId;
        dimVelVecIdArray[0] = dimVelVecId;
        dimPresXIdArray[0] = dimPresXId;
        dimPresYIdArray[0] = dimPresYId;
        dimPresZIdArray[0] = dimPresZId;

        // Define meta data.
        string gridLayoutType = cf.gridType.ToString();
        //Debug.Log("Length: " + gridLayoutType.Length);
        status = CsNetCDF.NetCDF.nc_put_att_text(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "gridLayoutType", (IntPtr)gridLayoutType.Length, gridLayoutType);
        CheckError("Write gridLayoutType error: ");

        dtList[0] = cf.dt;
        status = CsNetCDF.NetCDF.nc_put_att_float(fileId, CsNetCDF.NetCDF.NC_GLOBAL, "dt", CsNetCDF.NetCDF.nc_type.NC_FLOAT, (IntPtr)1, dtList);
        CheckError("Write dt error: ");

        // Define coordinate variables.
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "time", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimTimeIdArray, out varTimeId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "velX", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimVelXIdArray, out varVelXId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "velY", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimVelYIdArray, out varVelYId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "velZ", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimVelZIdArray, out varVelZId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "velVec", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimVelVecIdArray, out varVelVecId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "presX", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimPresXIdArray, out varPresXId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "presY", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimPresYIdArray, out varPresYId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "presZ", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 1, dimPresZIdArray, out varPresZId);
        CheckError("Define coordinate variables error: ");

        dimVelFieldIdArray[0] = dimTimeId;
        dimVelFieldIdArray[1] = dimVelZId;
        dimVelFieldIdArray[2] = dimVelYId;
        dimVelFieldIdArray[3] = dimVelXId;
        dimVelFieldIdArray[4] = dimVelVecId;

        dimPresFieldIdArray[0] = dimTimeId;
        dimPresFieldIdArray[1] = dimPresZId;
        dimPresFieldIdArray[2] = dimPresYId;
        dimPresFieldIdArray[3] = dimPresXId;

        // Define velocity and pressure fields.
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "velField", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 5, dimVelFieldIdArray, out varVelFieldId);
        status = CsNetCDF.NetCDF.nc_def_var(fileId, "presField", CsNetCDF.NetCDF.nc_type.NC_FLOAT, 4, dimPresFieldIdArray, out varPresFieldId);
        CheckError("Define field variables: ");

        status = CsNetCDF.NetCDF.nc_enddef(fileId);
        CheckError("End define error: ");

        // Write coordinate variables.
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varVelXId, velXValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varVelYId, velYValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varVelZId, velZValues);
        status = CsNetCDF.NetCDF.nc_put_var_int(fileId, varVelVecId, velVecValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varPresXId, presXValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varPresYId, presYValues);
        status = CsNetCDF.NetCDF.nc_put_var_float(fileId, varPresZId, presZValues);
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
        while (fieldQueue.Count > 0)
        {
            UnityEngine.Debug.Log("Waiting for field queue to be empty before closing the file. Current queue length: " + fieldQueue.Count);
            Thread.Sleep(500);
        }

        status = CsNetCDF.NetCDF.nc_close(fileId);
        CheckError("Close file error: ");
    }
    #endregion

    #region WriteDataFuncs
    public void EnqueueField()
    {
        FieldData fieldData = new FieldData(cf.currPhyTime, null, null);

        if (cf.saveVelField)
        {
            var velNativeArray = new NativeArray<float>(cf.velRes.x * cf.velRes.y * cf.velRes.z * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            AsyncGPUReadback.RequestIntoNativeArray(ref velNativeArray, velFieldGpu, 0, request =>
            {
                if (request.hasError)
                {
                    Debug.LogError("GPU velvelocity field readback error");
                    return;
                }

                fieldData.velField = velNativeArray.ToArray(); // A data copy happens at "ToArray".
                fieldData.velReady = true;
                velNativeArray.Dispose();

                TryEnqueueFrame(fieldQueue, fieldData);
            });
        }
        if (cf.savePresField)
        {
            var presNativeArray = new NativeArray<float>(cf.presRes.x * cf.presRes.y * cf.presRes.z, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            AsyncGPUReadback.RequestIntoNativeArray(ref presNativeArray, presFieldGpu, 0, request =>
            {
                if (request.hasError)
                {
                    Debug.LogError("GPU pressure field readback error");
                    return;
                }

                fieldData.presField = presNativeArray.ToArray(); // A data copy happens at "ToArray".
                fieldData.presReady = true;
                presNativeArray.Dispose();

                TryEnqueueFrame(fieldQueue, fieldData);
            });
        }

        Debug.Log("Enqueued field at time: " + cf.currPhyTime + "s, step: " + cf.currSimStep + ". Current queue length: " + fieldQueue.Count + "numStepsSaved" + cf.numStepsSaved);
    }

    public void TryEnqueueFrame(ConcurrentQueue<FieldData> fieldQueue, FieldData fieldData)
    {
        if (cf.saveVelField && cf.savePresField)
        {
            if (fieldData.velReady && fieldData.presReady)
            {
                fieldQueue.Enqueue(fieldData);
            }
        }
        else
        {
            fieldQueue.Enqueue(fieldData);
        }
    }

    private void NcWriterLoop()
    {
        while (true)
        {
            FieldData fieldData;
            if (fieldQueue.TryDequeue(out fieldData))
            {
                // Write current time.
                timeValues[0] = fieldData.currPhyTime;
                timeWriteStart[0] = (IntPtr)cf.numStepsSaved;
                status = CsNetCDF.NetCDF.nc_put_vara_float(fileId, varTimeId, timeWriteStart, timeWriteCount, timeValues);

                // Write velocity field.
                if (cf.saveVelField)
                {
                    velFieldWriteStart[0] = (IntPtr)cf.numStepsSaved;
                    status = CsNetCDF.NetCDF.nc_put_vara_float(fileId, varVelFieldId, velFieldWriteStart, velFieldWriteCount, fieldData.velField);
                    CheckError("Velocity field write data error: ");
                }

                // Write pressure field.
                if (cf.savePresField)
                {
                    presFieldWriteStart[0] = (IntPtr)cf.numStepsSaved;
                    status = CsNetCDF.NetCDF.nc_put_vara_float(fileId, varPresFieldId, presFieldWriteStart, presFieldWriteCount, fieldData.presField);
                    CheckError("Pressure field write data error: ");
                }

                // Increase num of saved steps
                cf.numStepsSaved++;
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
