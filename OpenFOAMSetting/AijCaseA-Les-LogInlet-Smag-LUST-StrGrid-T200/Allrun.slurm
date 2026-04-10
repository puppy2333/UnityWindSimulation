#!/bin/bash
#SBATCH --job-name="AijBuilding0Les-LogInlet-Smagorinsky-LUST"
#SBATCH --output="slurm_%j.out"
#SBATCH --account=research-abe-aet
#SBATCH --partition=compute
#SBATCH --time=3:00:00
#SBATCH --ntasks=32
#SBATCH --cpus-per-task=1
#SBATCH --mem-per-cpu=3G

module load 2025 openmpi openfoam/2312

CASE_DIR=$SLURM_SUBMIT_DIR
SCRATCH_DIR=/scratch/$USER/$SLURM_JOB_NAME_$SLURM_JOB_ID

echo "Copying files to scratch $SCRATCH_DIR"
mkdir -p $SCRATCH_DIR
rsync -a $CASE_DIR/ $SCRATCH_DIR/
cd $SCRATCH_DIR

. ${WM_PROJECT_DIR:?}/bin/tools/RunFunctions


echo "Cleaning up previous logs..."
rm -rf processor* log.* 0 constant/polyMesh

echo "1. Running blockMesh..."
runApplication blockMesh

echo "2. Restoring 0 directory..."
cp -r 0.orig 0

echo "3. Running checkMesh..."
runApplication checkMesh

echo "4. Running setExprFields..."
runApplication setExprFields

echo "5. Running decomposePar..."
runApplication decomposePar

echo "6. Running pimpleFoam in parallel..."
srun pimpleFoam -parallel > log.pimpleFoam 2>&1
