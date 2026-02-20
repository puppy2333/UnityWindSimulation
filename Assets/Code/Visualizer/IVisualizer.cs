using UnityEngine;

public interface IVisualizer
{
    void UpdateVis();

    void UpdateQuadPosBySlider(float handlePos);

    void UpdateQuadPosByConfig();

    void UpdateQuadOrientationByConfig();

    void UpdateQuadSizeByConfig();

    void LoadColorMapFromCsv(string csvText);

    RenderTexture GetVelDirSliceTex();

    RenderTexture GetVelMagSliceTex();
}
