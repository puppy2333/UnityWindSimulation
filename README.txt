===============================================================================
UnityWindSimulation
===============================================================================

DESCRIPTION
-----------
A GPU-based urban wind simulator built using Unity Compute Shaders. This 
project leverages the Unity High Definition Render Pipeline (HDRP) for 
high-performance computation and visualization.

SYSTEM REQUIREMENTS & TESTING ENVIRONMENT
-----------------------------------------
- Operating System : Windows 11
- Unity Engine     : Version 6000.3.2f1 (HDRP)

Tested Hardware:
- CPU : Intel Core Ultra 9 185H
- GPU : NVIDIA GeForce RTX 4070

* Note: Because the core simulation relies heavily on Compute Shaders, a 
  discrete/dedicated GPU is strongly recommended for optimal performance.

INSTALLATION
------------
1. Ensure Unity version 6000.3.2f1 is installed on your system.
2. Clone the repository to your local machine using Git:
   git clone git@github.com:puppy2333/UnityWindSimulation.git
3. Open the cloned project folder in the Unity Editor. 
   (Note: The initial opening and asset import process might take some time.)

USAGE INSTRUCTIONS
------------------
1. Load Scene      : In the Unity Editor, navigate to and load the main scene 
                     named "OutdoorsScene.unity".
2. General Settings: Select the "FvmControllerGO" object in the Hierarchy 
                     window to modify general solver settings.
3. Sim Settings    : Modify the detailed simulation parameters in scripts 
                     located in the "Assets/Code/Config" folder.
4. Run Simulation  : Click the "Play" button at the top center of the Unity 
                     Editor to start the simulation.
5. UI & Controls   : Further operating instructions and interactive controls 
                     are displayed directly on the screen during gameplay.

RESULTS & VALIDATION
--------------------
1. Unity Settings   : The configuration file used to produce comparison 
                      results is located in "Assets/Code/Config/AijBuilding0".
2. OpenFOAM Settings: The corresponding OpenFOAM case setup, used as the 
                      baseline for validation and comparison, is provided in 
                      the dictionary "OpenFOAMSetting".

================================================================================