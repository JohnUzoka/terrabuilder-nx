/* Audio scheduling for the devkitPro SDL2 Switch audio driver.
 *
 * Linked with -Wl,--wrap=SDL_SetThreadPriority,--wrap=SDL_OpenAudioDevice.
 *
 * Threads: SDL and FAudio create their threads with libnx's default core and priority,
 * which is the game thread's (core 0, 0x2C). Horizon does not time-slice threads of equal
 * priority on one core, so the mixer only runs when the game thread blocks and audio
 * underruns (slow, choppy sound). SDL's mixer thread asks for TIME_CRITICAL and FACT's
 * API thread for HIGH; both move to NX_AUDIO_CORE (default 2) above the game priority.
 *
 * Buffering: the Switch driver double-buffers SDL's period. FAudio asks for 10 ms (480
 * frames), which leaves about 5 ms for each mix. NX_AUDIO_SAMPLES (default 1024, about
 * 21 ms) is applied to every output device opened without allowed changes.
 *
 * Service load: the driver's PlayDevice loops audrvUpdate (an IPC request to the system
 * audio service) and audrenWaitFrame (5 ms) for as long as a buffer plays. With
 * --wrap=audrenWaitFrame, NX_AUDREN_WAIT_FRAMES (default 1) waits that many renderer
 * frames per loop, so fewer requests reach the system core.
 */
#include "io_util.h"

#include <switch.h>
#include <SDL2/SDL.h>
#include <stdlib.h>

#define NX_AUDIO_PRIORITY_MIXER 0x2A
#define NX_AUDIO_PRIORITY_API 0x2B

extern void __real_audrenWaitFrame(void);
extern int __real_SDL_SetThreadPriority(SDL_ThreadPriority priority);
extern SDL_AudioDeviceID __real_SDL_OpenAudioDevice(const char *device, int iscapture,
    const SDL_AudioSpec *desired, SDL_AudioSpec *obtained, int allowed_changes);

static int env_int(const char *name, int fallback, int low, int high)
{
    const char *text = getenv(name);
    if (!text || !*text)
        return fallback;
    char *end;
    long value = strtol(text, &end, 0);
    return (*end || value < low || value > high) ? fallback : (int)value;
}

int __wrap_SDL_SetThreadPriority(SDL_ThreadPriority priority)
{
    if (priority != SDL_THREAD_PRIORITY_TIME_CRITICAL && priority != SDL_THREAD_PRIORITY_HIGH)
        return __real_SDL_SetThreadPriority(priority);
    int core = env_int("NX_AUDIO_CORE", 2, 0, 2);
    int level = priority == SDL_THREAD_PRIORITY_TIME_CRITICAL ? NX_AUDIO_PRIORITY_MIXER : NX_AUDIO_PRIORITY_API;
    Handle self = threadGetCurHandle();
    Result core_rc = svcSetThreadCoreMask(self, core, 1u << core);
    Result priority_rc = svcSetThreadPriority(self, level);
    io_debugf("NX_AUDIO thread %s -> core %d priority 0x%X (rc=0x%x/0x%x)",
        priority == SDL_THREAD_PRIORITY_TIME_CRITICAL ? "mixer" : "api", core, level,
        (unsigned)core_rc, (unsigned)priority_rc);
    return R_SUCCEEDED(core_rc) && R_SUCCEEDED(priority_rc) ? 0 : -1;
}

SDL_AudioDeviceID __wrap_SDL_OpenAudioDevice(const char *device, int iscapture,
    const SDL_AudioSpec *desired, SDL_AudioSpec *obtained, int allowed_changes)
{
    if (iscapture || !desired || allowed_changes)
        return __real_SDL_OpenAudioDevice(device, iscapture, desired, obtained, allowed_changes);
    SDL_AudioSpec spec = *desired;
    spec.samples = (Uint16)env_int("NX_AUDIO_SAMPLES", 1024, 256, 8192);
    SDL_AudioDeviceID id = __real_SDL_OpenAudioDevice(device, iscapture, &spec, obtained, allowed_changes);
    io_debugf("NX_AUDIO open %d Hz, %d ch, %u frames (requested %u) -> device %u",
        spec.freq, spec.channels, (unsigned)spec.samples, (unsigned)desired->samples, (unsigned)id);
    return id;
}

void __wrap_audrenWaitFrame(void)
{
    static int frames;
    static uint64_t calls;
    if (!frames) {
        frames = env_int("NX_AUDREN_WAIT_FRAMES", 1, 1, 8);
        io_debugf("NX_AUDIO renderer wait %d frame(s) per update", frames);
    }
    for (int i = 0; i < frames; ++i)
        __real_audrenWaitFrame();
    if (++calls % 2000 == 0)
        io_debugf("NX_AUDIO renderer waits=%llu", (unsigned long long)calls);
}
