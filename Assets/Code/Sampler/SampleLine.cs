using System.Linq;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

public class SampleLine
{
    FluidSimConfig cf;
    RuntimeConfig rcf;

    float[] facePosXArray;
    float[] facePosYArray;
    float[] facePosZArray;

    float[] cellPosXArray;
    float[] cellPosYArray;
    float[] cellPosZArray;

    int numSampleLines = 0;
    int maxNumSamples = 0;

    RenderTexture sampleLinesTex;
    ComputeBuffer sampleIdsBuffer;
    ComputeBuffer sampleWeightsBuffer;

    ComputeShader lineSampleShader;
    int sampleVelXKernel;

    int3[] sampleIdsArray;
    float3[] sampleWeightsArray;

    public SampleLine(
        FluidSimConfig cfIn, RuntimeConfig rcfIn, 
        float[] facePosXArrayIn=null, float[] facePosYArrayIn = null, float[] facePosZArrayIn = null)
    {
        cf = cfIn;
        rcf = rcfIn;

        facePosXArray = facePosXArrayIn;
        facePosYArray = facePosYArrayIn;
        facePosZArray = facePosZArrayIn;
        InitCellPosArray();

        numSampleLines = cf.sampleLines.Count;
        maxNumSamples = cf.sampleLines.Max(line => line.numSamples);

        sampleLinesTex = CreateRenderTexture2D(new int2(numSampleLines, maxNumSamples), RenderTextureFormat.RFloat);

        int totalSamples = numSampleLines * maxNumSamples;
        sampleIdsBuffer = new ComputeBuffer(totalSamples, sizeof(int) * 3);
        sampleWeightsBuffer = new ComputeBuffer(totalSamples, sizeof(float) * 3);

        sampleIdsArray = new int3[totalSamples];
        sampleWeightsArray = new float3[totalSamples];

        InitSampleCoeffs();
        UploadSampleCoeffsToBuffer();

        InitLineSampleShader();
    }

    void InitLineSampleShader()
    {
        ComputeShader lineSampleShaderAsset = Resources.Load<ComputeShader>("Shaders/SampleShaders/LineSample");
        lineSampleShader = Object.Instantiate(lineSampleShaderAsset);
        sampleVelXKernel = lineSampleShader.FindKernel("CSSampleVelX");

        lineSampleShader.SetInts("gridRes", rcf.numCells.x, rcf.numCells.y, rcf.numCells.z);
        lineSampleShader.SetInt("numSampleLines", numSampleLines);
        lineSampleShader.SetInt("maxNumSamples", maxNumSamples);

        lineSampleShader.SetBuffer(sampleVelXKernel, "sampleIdsBuffer", sampleIdsBuffer);
        lineSampleShader.SetBuffer(sampleVelXKernel, "sampleWeightsBuffer", sampleWeightsBuffer);
        lineSampleShader.SetTexture(sampleVelXKernel, "sampleLinesTex", sampleLinesTex);
    }

    public void SampleLines(RenderTexture velFieldTex)
    {
        lineSampleShader.SetTexture(sampleVelXKernel, "velField", velFieldTex);

        int groupX = Mathf.CeilToInt(numSampleLines / 8.0f);
        int groupY = Mathf.CeilToInt(maxNumSamples / 8.0f);
        lineSampleShader.Dispatch(sampleVelXKernel, groupX, groupY, 1);
    }

    public RenderTexture GetSampleLinesTex()
    {
        return sampleLinesTex;
    }

    void InitCellPosArray()
    {
        cellPosXArray = new float[facePosXArray.Length - 1];
        for (int i = 0; i < cellPosXArray.Length; i++)
        {
            cellPosXArray[i] = 0.5f * (facePosXArray[i] + facePosXArray[i + 1]);
        }

        cellPosYArray = new float[facePosYArray.Length - 1];
        for (int i = 0; i < cellPosYArray.Length; i++)
        {
            cellPosYArray[i] = 0.5f * (facePosYArray[i] + facePosYArray[i + 1]);
        }

        cellPosZArray = new float[facePosZArray.Length - 1];
        for (int i = 0; i < cellPosZArray.Length; i++)
        {
            cellPosZArray[i] = 0.5f * (facePosZArray[i] + facePosZArray[i + 1]);
        }
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

    void UploadSampleCoeffsToBuffer()
    {
        sampleIdsBuffer.SetData(sampleIdsArray);
        sampleWeightsBuffer.SetData(sampleWeightsArray);
    }

    (int, float) FindSampleIndexAndWeight(float samplePos, float[] cellPosArray, ref int searchStart)
    {
        int sampleIdx = 0;
        float sampleWeight = 0;

        int lastIdx = cellPosArray.Length - 1;

        if (samplePos <= cellPosArray[0])
        {
            sampleIdx = 0;
            sampleWeight = 1f;
        }
        else if (samplePos >= cellPosArray[lastIdx])
        {
            sampleIdx = lastIdx - 1;
            sampleWeight = 0f;
        }
        else
        {
            int j = searchStart;
            while (j < lastIdx - 1 && samplePos >= cellPosArray[j + 1])
                j++;

            sampleIdx = j;
            sampleWeight = (cellPosArray[j + 1] - samplePos) / (cellPosArray[j + 1] - cellPosArray[j]);
            searchStart = j;
        }

        return (sampleIdx, sampleWeight);
    }

    void InitYSampleCoeffs(SampleLineConfig sampleLine, int offset)
    {
        float samplePosX = sampleLine.fixedCoords.x;
        float samplePosZ = sampleLine.fixedCoords.y;

        int sampleIdxX = 0;
        int sampleIdxY = 0;
        int sampleIdxZ = 0;
        float sampleWeightX = 0f;
        float sampleWeightY = 0f;
        float sampleWeightZ = 0f;

        int xSearchStart = 0;
        (sampleIdxX, sampleWeightX) = FindSampleIndexAndWeight(samplePosX, cellPosXArray, ref xSearchStart);

        int zSearchStart = 0;
        (sampleIdxZ, sampleWeightZ) = FindSampleIndexAndWeight(samplePosZ, cellPosZArray, ref zSearchStart);

        // Find Y index and weight for each sample point along the line
        int ySearchStart = 0;
        for (int i = 0; i < sampleLine.numSamples; i++)
        {
            float samplePosY = sampleLine.sampleRange.x + i * (sampleLine.sampleRange.y - sampleLine.sampleRange.x) / (sampleLine.numSamples - 1);

            (sampleIdxY, sampleWeightY) = FindSampleIndexAndWeight(samplePosY, cellPosYArray, ref ySearchStart);

            int sampleFlatIdx = offset + i;
            sampleIdsArray[sampleFlatIdx] = new int3(sampleIdxX, sampleIdxY, sampleIdxZ);
            sampleWeightsArray[sampleFlatIdx] = new float3(sampleWeightX, sampleWeightY, sampleWeightZ);
        }
    }

    void InitZSampleCoeffs(SampleLineConfig sampleLine, int offset)
    {
        float samplePosX = sampleLine.fixedCoords.x;
        float samplePosY = sampleLine.fixedCoords.y;

        int sampleIdxX = 0;
        int sampleIdxY = 0;
        int sampleIdxZ = 0;
        float sampleWeightX = 0f;
        float sampleWeightY = 0f;
        float sampleWeightZ = 0f;

        int xSearchStart = 0;
        (sampleIdxX, sampleWeightX) = FindSampleIndexAndWeight(samplePosX, cellPosXArray, ref xSearchStart);

        int ySearchStart = 0;
        (sampleIdxY, sampleWeightY) = FindSampleIndexAndWeight(samplePosY, cellPosYArray, ref ySearchStart);

        int zSearchStart = 0;
        for (int i = 0; i < sampleLine.numSamples; i++)
        {
            float samplePosZ = sampleLine.sampleRange.x + i * (sampleLine.sampleRange.y - sampleLine.sampleRange.x) / (sampleLine.numSamples - 1);

            (sampleIdxZ, sampleWeightZ) = FindSampleIndexAndWeight(samplePosZ, cellPosZArray, ref zSearchStart);

            int sampleFlatIdx = offset + i;
            sampleIdsArray[sampleFlatIdx] = new int3(sampleIdxX, sampleIdxY, sampleIdxZ);
            sampleWeightsArray[sampleFlatIdx] = new float3(sampleWeightX, sampleWeightY, sampleWeightZ);
        }
    }



    void InitSampleCoeffs()
    {
        for (int i = 0; i < cf.sampleLines.Count; i++)
        {
            SampleLineConfig sampleLine = cf.sampleLines[i];
            int offset = i * maxNumSamples;

            if (sampleLine.sampleAxis == SampleAxis.Y)
                InitYSampleCoeffs(sampleLine, offset);
            else if (sampleLine.sampleAxis == SampleAxis.Z)
                InitZSampleCoeffs(sampleLine, offset);
        }

        DebugPrintSampleCoeffs();
    }

    void DebugPrintSampleCoeffs()
    {
        StringBuilder sb = new();
        sb.AppendLine("[SampleLine] Dump sample coeffs (idxX, idxY, idxZ | wx, wy, wz)");

        for (int line = 0; line < numSampleLines; line++)
        {
            sb.AppendLine($"Line {line}:");
            int baseOffset = line * maxNumSamples;
            for (int i = 0; i < maxNumSamples; i++)
            {
                int sampleFlatIdx = baseOffset + i;
                int3 ids = sampleIdsArray[sampleFlatIdx];
                float3 weights = sampleWeightsArray[sampleFlatIdx];
                sb.AppendLine($"  Sample {i}: ({ids.x}, {ids.y}, {ids.z}) | ({weights.x}, {weights.y}, {weights.z})");
            }
        }

        Debug.Log(sb.ToString());
    }
}
