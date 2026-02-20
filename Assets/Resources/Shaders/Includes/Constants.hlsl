#ifndef CONSTANTS_HLSL
#define CONSTANTS_HLSL

static const int CELL_FLUID = 0;
static const int CELL_SOLID = 1;
static const int CELL_BOUNDARY = 2;

static const int VEL_BND_FIXED_VALUE = 0;
static const int VEL_BND_ZERO_GRAD = 1;
static const int VEL_BND_SYMMETRY = 2;

static const int PRES_BND_FIXED_VALUE = 0;
static const int PRES_BND_ZERO_GRAD = 1;
static const int PRES_BND_SYMMETRY = 2;

static const int3 groupSize = int3(8, 8, 8);

#endif