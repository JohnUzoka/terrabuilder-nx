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
 *
 * Output backend (NX_AUDIO_OUTPUT, wraps SDL_PauseAudioDevice/SDL_CloseAudioDevice too):
 * "audren" (default) keeps SDL's driver. "audout" feeds the system AudioOut service from
 * our own thread; "null" runs FAudio's mixer at the device rate and discards the output.
 * FAudio uses only Open/Pause/Close and reads freq, channels and samples back.
 */
#include "io_util.h"

#include <switch.h>
#include <SDL2/SDL.h>
#include <stdlib.h>
#include <string.h>
#include <malloc.h>

#define NX_AUDIO_PRIORITY_MIXER 0x2A
#define NX_AUDIO_PRIORITY_API 0x2B

extern void __real_audrenWaitFrame(void);
extern void __real_SDL_PauseAudioDevice(SDL_AudioDeviceID dev, int pause_on);
extern void __real_SDL_CloseAudioDevice(SDL_AudioDeviceID dev);
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

#define NX_OWN_DEVICE 0x4E58u
#define NX_OWN_BUFFERS 3

typedef struct NxOutput {
    int mode; /* 1 = audout, 2 = null */
    SDL_AudioCallback callback;
    void *userdata;
    int channels;
    int samples;
    float *mix;
    int16_t *pcm[NX_OWN_BUFFERS];
    AudioOutBuffer buffers[NX_OWN_BUFFERS];
    size_t pcm_bytes;
    Thread thread;
    volatile bool running;
    bool started;
} NxOutput;

static NxOutput own;

static void own_fill(int16_t *out)
{
    int count = own.samples * own.channels;
    own.callback(own.userdata, (Uint8 *)own.mix, count * (int)sizeof(float));
    for (int i = 0; i < count; ++i) {
        float v = own.mix[i] * 32767.0f;
        out[i] = (int16_t)(v > 32767.0f ? 32767 : v < -32768.0f ? -32768 : v);
    }
}

static void own_thread(void *unused)
{
    (void)unused;
    if (own.mode == 1) {
        for (int i = 0; i < NX_OWN_BUFFERS; ++i) {
            own_fill(own.pcm[i]);
            armDCacheFlush(own.pcm[i], own.pcm_bytes);
            audoutAppendAudioOutBuffer(&own.buffers[i]);
        }
        while (own.running) {
            AudioOutBuffer *released = NULL;
            u32 count = 0;
            if (R_FAILED(audoutWaitPlayFinish(&released, &count, 100000000ULL)) || !released)
                continue;
            own_fill(released->buffer);
            armDCacheFlush(released->buffer, own.pcm_bytes);
            audoutAppendAudioOutBuffer(released);
        }
    } else {
        uint64_t period = armNsToTicks(1000000000ULL * own.samples / 48000);
        uint64_t next = armGetSystemTick();
        while (own.running) {
            own_fill(own.pcm[0]);
            next += period;
            uint64_t now = armGetSystemTick();
            if (next > now)
                svcSleepThread(armTicksToNs(next - now));
            else
                next = now;
        }
    }
}

static SDL_AudioDeviceID own_open(int mode, const SDL_AudioSpec *desired, SDL_AudioSpec *obtained, int samples)
{
    if (own.mode)
        return 0;
    if (mode == 1) {
        Result rc = audoutInitialize();
        if (R_SUCCEEDED(rc))
            rc = audoutStartAudioOut();
        if (R_FAILED(rc)) {
            io_debugf("NX_AUDIO audout failed (rc=0x%x); using SDL", (unsigned)rc);
            return 0;
        }
    }
    own.mode = mode;
    own.callback = desired->callback;
    own.userdata = desired->userdata;
    own.channels = 2;
    own.samples = samples;
    own.mix = malloc(sizeof(float) * samples * 2);
    own.pcm_bytes = sizeof(int16_t) * samples * 2;
    size_t aligned = (own.pcm_bytes + 0xFFF) & ~(size_t)0xFFF;
    for (int i = 0; i < NX_OWN_BUFFERS; ++i) {
        own.pcm[i] = memalign(0x1000, aligned);
        memset(own.pcm[i], 0, aligned);
        own.buffers[i] = (AudioOutBuffer){ .buffer = own.pcm[i], .buffer_size = aligned,
            .data_size = own.pcm_bytes, .data_offset = 0 };
    }
    SDL_AudioSpec have = *desired;
    have.freq = 48000;
    have.channels = 2;
    have.format = AUDIO_F32SYS;
    have.samples = (Uint16)samples;
    have.size = (Uint32)(sizeof(float) * samples * 2);
    if (obtained)
        *obtained = have;
    return NX_OWN_DEVICE;
}

SDL_AudioDeviceID __wrap_SDL_OpenAudioDevice(const char *device, int iscapture,
    const SDL_AudioSpec *desired, SDL_AudioSpec *obtained, int allowed_changes)
{
    if (iscapture || !desired || allowed_changes)
        return __real_SDL_OpenAudioDevice(device, iscapture, desired, obtained, allowed_changes);
    SDL_AudioSpec spec = *desired;
    spec.samples = (Uint16)env_int("NX_AUDIO_SAMPLES", 1024, 256, 8192);
    const char *output = getenv("NX_AUDIO_OUTPUT");
    int mode = output && !strcmp(output, "audout") ? 1 : output && !strcmp(output, "null") ? 2 : 0;
    SDL_AudioDeviceID id = 0;
    if (mode && desired->freq == 48000 && desired->channels == 2 && desired->format == AUDIO_F32SYS)
        id = own_open(mode, desired, obtained, spec.samples);
    if (!id) {
        mode = 0;
        id = __real_SDL_OpenAudioDevice(device, iscapture, &spec, obtained, allowed_changes);
    }
    io_debugf("NX_AUDIO open %s %d Hz, %d ch, %u frames (requested %u) -> device %u",
        mode == 1 ? "audout" : mode == 2 ? "null" : "audren", spec.freq, spec.channels,
        (unsigned)spec.samples, (unsigned)desired->samples, (unsigned)id);
    return id;
}

void __wrap_SDL_PauseAudioDevice(SDL_AudioDeviceID dev, int pause_on)
{
    if (dev != NX_OWN_DEVICE) {
        __real_SDL_PauseAudioDevice(dev, pause_on);
        return;
    }
    if (pause_on || own.started)
        return;
    int core = env_int("NX_AUDIO_CORE", 2, 0, 2);
    own.running = true;
    Result rc = threadCreate(&own.thread, own_thread, NULL, NULL, 0x8000, NX_AUDIO_PRIORITY_MIXER, core);
    if (R_SUCCEEDED(rc))
        rc = threadStart(&own.thread);
    own.started = R_SUCCEEDED(rc);
    io_debugf("NX_AUDIO own mixer thread core %d priority 0x%X (rc=0x%x)", core,
        NX_AUDIO_PRIORITY_MIXER, (unsigned)rc);
}

void __wrap_SDL_CloseAudioDevice(SDL_AudioDeviceID dev)
{
    if (dev != NX_OWN_DEVICE) {
        __real_SDL_CloseAudioDevice(dev);
        return;
    }
    if (own.started) {
        own.running = false;
        threadWaitForExit(&own.thread);
        threadClose(&own.thread);
    }
    if (own.mode == 1) {
        audoutStopAudioOut();
        audoutExit();
    }
    own.started = false;
    own.mode = 0;
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
    if (++calls % 2000 == 0) {
#if defined(MONO_NX_AUDIO_DIAG)
        io_debugf("NX_AUDIO renderer waits=%llu", (unsigned long long)calls);
#endif
    }
}
