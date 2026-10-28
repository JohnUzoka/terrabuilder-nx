// Presented-frame and GPU buffer churn statistics for tModLoader builds.
//
// tModLoader runs its own game loop, so nx_input's NX_PHASE windows (reported from the
// vanilla Tick) never print. Linked with -Wl,--wrap=SDL_GL_SwapWindow, this file times every
// presented frame and, every 5 s, logs from the render thread:
//   NX_FPS elapsed window frames fps frame_ms avg/max, frames per time class, time inside
//   SDL_GL_SwapWindow (GPU/vsync wait), nouveau_bo_new churn in the same window
//   (count, MB, size classes, the most frequent size), and FNA3D draw calls per frame.
// Built with MONO_NX_FRAME_STATS_BO_WRAP it also wraps nouveau_bo_new itself (when mesa_guard.c,
// which has its own wrap, is not linked); otherwise mesa_guard.c calls mono_nx_frame_stats_bo.
// The draw counts need -Wl,--wrap=FNA3D_DrawIndexedPrimitives,--wrap=FNA3D_DrawInstancedPrimitives,
// --wrap=FNA3D_DrawPrimitives.

#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <switch.h>
#include <io_util.h>

typedef struct SDL_Window SDL_Window;
void __real_SDL_GL_SwapWindow(SDL_Window *window);

// FNA3D's prototypes with its enums as int, which is how AArch64 passes them.
typedef struct FNA3D_Device FNA3D_Device;
typedef struct FNA3D_Buffer FNA3D_Buffer;
void __real_FNA3D_DrawIndexedPrimitives(FNA3D_Device *device, int primitive_type, int32_t base_vertex,
    int32_t min_vertex_index, int32_t num_vertices, int32_t start_index, int32_t primitive_count,
    FNA3D_Buffer *indices, int index_element_size);
void __real_FNA3D_DrawInstancedPrimitives(FNA3D_Device *device, int primitive_type, int32_t base_vertex,
    int32_t min_vertex_index, int32_t num_vertices, int32_t start_index, int32_t primitive_count,
    int32_t instance_count, FNA3D_Buffer *indices, int index_element_size);
void __real_FNA3D_DrawPrimitives(FNA3D_Device *device, int primitive_type, int32_t vertex_start,
    int32_t primitive_count);

#define REPORT_TICKS (5 * 19200000ull)
#define MS(ticks) ((double)(ticks) / 19200.0)
#define TOP_SIZES 16

static Mutex stats_lock;
static uint64_t start_tick, window_start, last_frame_end;
static uint64_t frames, total_frames, frame_sum, frame_max, swap_sum, swap_max;
static uint64_t frames_60, frames_30, frames_20, frames_slow;
static uint64_t bo_count, bo_bytes, bo_small, bo_medium, bo_large, bo_huge, bo_failed, bo_total;
static uint64_t bo_vram, bo_gart;
static struct { uint64_t size; uint64_t count; } top[TOP_SIZES];
static uint64_t draw_count, draws_at_last_frame, window_draws, window_draws_max;

void __wrap_FNA3D_DrawIndexedPrimitives(FNA3D_Device *device, int primitive_type, int32_t base_vertex,
    int32_t min_vertex_index, int32_t num_vertices, int32_t start_index, int32_t primitive_count,
    FNA3D_Buffer *indices, int index_element_size)
{
    __atomic_fetch_add(&draw_count, 1, __ATOMIC_RELAXED);
    __real_FNA3D_DrawIndexedPrimitives(device, primitive_type, base_vertex, min_vertex_index, num_vertices,
        start_index, primitive_count, indices, index_element_size);
}

void __wrap_FNA3D_DrawInstancedPrimitives(FNA3D_Device *device, int primitive_type, int32_t base_vertex,
    int32_t min_vertex_index, int32_t num_vertices, int32_t start_index, int32_t primitive_count,
    int32_t instance_count, FNA3D_Buffer *indices, int index_element_size)
{
    __atomic_fetch_add(&draw_count, 1, __ATOMIC_RELAXED);
    __real_FNA3D_DrawInstancedPrimitives(device, primitive_type, base_vertex, min_vertex_index, num_vertices,
        start_index, primitive_count, instance_count, indices, index_element_size);
}

void __wrap_FNA3D_DrawPrimitives(FNA3D_Device *device, int primitive_type, int32_t vertex_start,
    int32_t primitive_count)
{
    __atomic_fetch_add(&draw_count, 1, __ATOMIC_RELAXED);
    __real_FNA3D_DrawPrimitives(device, primitive_type, vertex_start, primitive_count);
}

void mono_nx_frame_stats_bo(int rc, uint64_t size, uint32_t flags)
{
    mutexLock(&stats_lock);
    if (rc != 0) {
        ++bo_failed;
    } else {
        ++bo_count;
        ++bo_total;
        bo_bytes += size;
        if (size <= 64 << 10) ++bo_small;
        else if (size <= 1 << 20) ++bo_medium;
        else if (size <= 4 << 20) ++bo_large;
        else ++bo_huge;
        // libdrm_nouveau: NOUVEAU_BO_VRAM 0x1, NOUVEAU_BO_GART 0x2.
        if (flags & 1) ++bo_vram;
        if (flags & 2) ++bo_gart;
        int free_slot = -1, least = 0;
        for (int i = 0; i < TOP_SIZES; ++i) {
            if (top[i].size == size && top[i].count) { ++top[i].count; free_slot = -2; break; }
            if (!top[i].count && free_slot == -1) free_slot = i;
            if (top[i].count < top[least].count) least = i;
        }
        if (free_slot != -2) {
            // Space-saving heavy hitter: evict the smallest counter when full.
            int slot = free_slot >= 0 ? free_slot : least;
            top[slot].count = free_slot >= 0 ? 1 : top[slot].count + 1;
            top[slot].size = size;
        }
    }
    mutexUnlock(&stats_lock);
}

#ifdef MONO_NX_FRAME_STATS_BO_WRAP
struct nouveau_device;
struct nouveau_bo;
union nouveau_bo_config;
int __real_nouveau_bo_new(struct nouveau_device *, uint32_t, uint32_t, uint64_t, union nouveau_bo_config *, struct nouveau_bo **);
int __wrap_nouveau_bo_new(struct nouveau_device *dev, uint32_t flags, uint32_t align, uint64_t size,
    union nouveau_bo_config *config, struct nouveau_bo **pbo)
{
    int rc = __real_nouveau_bo_new(dev, flags, align, size, config, pbo);
    mono_nx_frame_stats_bo(rc, size, flags);
    if (rc != 0)
        io_debugf("NX_BO FAILED rc=%d size=0x%" PRIx64 " align=0x%x flags=0x%x after created=%" PRIu64,
            rc, size, align, flags, bo_total);
    return rc;
}
#endif

static void report(uint64_t now)
{
    uint64_t top_size = 0, top_count = 0;
    for (int i = 0; i < TOP_SIZES; ++i)
        if (top[i].count > top_count) { top_count = top[i].count; top_size = top[i].size; }
    double window = MS(now - window_start) / 1000.0;
    io_debugf("NX_FPS elapsed=%.1fs window=%.2fs frames=%" PRIu64 "/%" PRIu64 " fps=%.1f "
        "frame_ms avg/max=%.1f/%.1f le17/le34/le50/gt50=%" PRIu64 "/%" PRIu64 "/%" PRIu64 "/%" PRIu64 " "
        "swap_ms sum/max=%.0f/%.1f bo=%" PRIu64 " bo_mb=%.1f le64k/le1m/le4m/gt4m=%" PRIu64 "/%" PRIu64 "/%" PRIu64 "/%" PRIu64 " "
        "vram/gart=%" PRIu64 "/%" PRIu64 " top_size=0x%" PRIx64 "x%" PRIu64 " bo_failed=%" PRIu64 " "
        "draws/frame avg/max=%.0f/%" PRIu64,
        MS(now - start_tick) / 1000.0, window, frames, total_frames, frames / window,
        frames ? MS(frame_sum) / frames : 0.0, MS(frame_max), frames_60, frames_30, frames_20, frames_slow,
        MS(swap_sum), MS(swap_max), bo_count, bo_bytes / 1048576.0, bo_small, bo_medium, bo_large, bo_huge,
        bo_vram, bo_gart, top_size, top_count, bo_failed,
        frames ? (double)window_draws / frames : 0.0, window_draws_max);
    window_start = now;
    frames = frame_sum = frame_max = swap_sum = swap_max = 0;
    window_draws = window_draws_max = 0;
    frames_60 = frames_30 = frames_20 = frames_slow = 0;
    bo_count = bo_bytes = bo_small = bo_medium = bo_large = bo_huge = bo_vram = bo_gart = 0;
    for (int i = 0; i < TOP_SIZES; ++i)
        top[i].size = top[i].count = 0;
}

void __wrap_SDL_GL_SwapWindow(SDL_Window *window)
{
    uint64_t begin = armGetSystemTick();
    __real_SDL_GL_SwapWindow(window);
    uint64_t end = armGetSystemTick();
    mutexLock(&stats_lock);
    if (!start_tick) {
        start_tick = window_start = begin;
        io_debugf("NX_FPS every 5s of presented frames (frame_ms = swap-to-swap; swap_ms = inside SDL_GL_SwapWindow; bo = nouveau_bo_new in window; draws = FNA3D draw calls)");
    }
    uint64_t swap = end - begin;
    swap_sum += swap;
    if (swap > swap_max) swap_max = swap;
    uint64_t draws_now = __atomic_load_n(&draw_count, __ATOMIC_RELAXED);
    uint64_t frame_draws = draws_now - draws_at_last_frame;
    draws_at_last_frame = draws_now;
    if (last_frame_end) {
        uint64_t frame = end - last_frame_end;
        double ms = MS(frame);
        frame_sum += frame;
        if (frame > frame_max) frame_max = frame;
        window_draws += frame_draws;
        if (frame_draws > window_draws_max) window_draws_max = frame_draws;
        if (ms <= 17.5) ++frames_60;
        else if (ms <= 34.0) ++frames_30;
        else if (ms <= 50.5) ++frames_20;
        else ++frames_slow;
        ++frames;
        ++total_frames;
    }
    last_frame_end = end;
    if (end - window_start >= REPORT_TICKS)
        report(end);
    mutexUnlock(&stats_lock);
}
