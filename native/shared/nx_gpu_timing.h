#pragma once
#include <stdbool.h>

// GPU frame timing via GL timestamp queries, plus clock logging (MONO_NX_GPU_TIMING=1).
// Called on the GL thread around the real FNA3D_SwapBuffers; see nx_gpu_timing.c.
void nx_gpu_frame_end(void);
void nx_gpu_frame_begin(void);
void nx_gpu_report(bool final);
