using UnityEngine;

public interface IFvmSolver
{
    bool Step();

    void InitFlags(int[] flags);

    void InitVelPresFields();

    void InitVelPresFieldsFromBackGroundFlow(FluidSimConfig cfBackGround, RenderTexture velTexBackGround, RenderTexture presTexBackGround);

    void SetFixedValueVelBndCond();

    void ChangePhysFieldPos();

    void ChangePhysDomainSize();

    object GetVelField();

    object GetPresField();

    object GetFlagField();
}
