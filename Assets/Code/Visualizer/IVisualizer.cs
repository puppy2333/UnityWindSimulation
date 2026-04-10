using UnityEngine;

public interface IVisualizer
{

    void LoadConfig(FluidSimConfig config, RuntimeConfig rcfIn);
    void UpdateVis();

    void UpdateQuadPosBySlider(float handlePos);

    void UpdateQuadPosByConfig();

    void UpdateQuadOrientationByConfig();

    void UpdateQuadSizeByConfig();

    void LoadColorMapFromCsv(string csvText);

    RenderTexture GetVelDirSliceTex();

    RenderTexture GetVelMagSliceTex();

    void SetFacePosArrays(float[] facePosXArrayIn, float[] facePosYArrayIn, float[] facePosZArrayIn);

    void SetTextures(RenderTexture velSimTexIn, RenderTexture presSimTexIn, RenderTexture flagSimTexIn);

    void AddNonUnifLayers(FluidSimConfig cfIn, RuntimeConfig rcfIn);
}
