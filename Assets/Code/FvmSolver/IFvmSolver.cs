using UnityEngine;

public interface IFvmSolver
{
    bool Step();

    void InitFlags(int[] flags);

    void InitNonUnifGridFlags(int[] flagsNonUnif);

    void InitVelPresFields();

    void InitVelPresFieldsFromBackGroundFlow(FluidSimConfig cfBG, RuntimeConfig rcfBG, RenderTexture velTexBG, RenderTexture presTexBG);

    void SetFixedValueVelBndCond();

    void ChangePhysFieldPos();

    void ChangePhysDomainSize();

    object GetVelField();

    object GetPresField();

    object GetFlagField();

    void InitNonUnifGridAuto(FluidSimConfig cfIn, RuntimeConfig rcfIn);

    float[] GetFacePosXArray();
    float[] GetFacePosYArray();
    float[] GetFacePosZArray();
}
