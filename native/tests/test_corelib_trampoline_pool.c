/* Host-only boundary regression; executes the extracted allocator, not ARM64 code. */
#include <assert.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "aot_layout.inc"

typedef struct {
    MonoAotFileInfo info;
    guint32 trampoline_index[MONO_AOT_TRAMP_NUM];
    guint8 *trampolines[MONO_AOT_TRAMP_NUM];
    gpointer *got;
} MonoAotModule;
typedef struct { const char *name; } MonoImage;
static MonoImage corlib = { "System.Private.CoreLib.dll" };
static struct { MonoImage *corlib; } mono_defaults = { &corlib };
static MonoAotModule module;
static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
#define MONO_ASSEMBLY_CORLIB_NAME "System.Private.CoreLib"
static MonoAotModule *get_mscorlib_aot_module(void) { return &module; }
static void mono_aot_lock(void) { assert(pthread_mutex_lock(&mutex) == 0); }
static void mono_aot_unlock(void) { assert(pthread_mutex_unlock(&mutex) == 0); }
static _Noreturn void g_error(const char *format, ...)
{
    va_list args;
    va_start(args, format);
    vfprintf(stderr, format, args);
    va_end(args);
    abort();
}

#include "allocator.inc"

int main(int argc, char **argv)
{
    assert(argc == 7);
    unsigned capacity = (unsigned)strtoul(argv[1], NULL, 10);
    unsigned stride = (unsigned)strtoul(argv[2], NULL, 10);
    unsigned base = (unsigned)strtoul(argv[3], NULL, 10);
    unsigned got_slots = (unsigned)strtoul(argv[4], NULL, 10);
    unsigned requests = (unsigned)strtoul(argv[5], NULL, 10);
    assert(capacity && stride && got_slots >= base + 2 * capacity);
    size_t code_bytes = (size_t)capacity * stride;
    guint8 *code = malloc(code_bytes);
    assert(code);
    FILE *file = fopen(argv[6], "rb");
    assert(file && fread(code, 1, code_bytes, file) == code_bytes);
    assert(fgetc(file) == EOF && fclose(file) == 0);
    module.got = calloc(got_slots, sizeof(gpointer));
    assert(module.got);
    module.info.num_trampolines[MONO_AOT_TRAMP_SPECIFIC] = capacity;
    module.info.trampoline_size[MONO_AOT_TRAMP_SPECIFIC] = stride;
    module.info.trampoline_got_offset_base[MONO_AOT_TRAMP_SPECIFIC] = base;
    module.trampolines[MONO_AOT_TRAMP_SPECIFIC] = code;
    for (unsigned i = 0; i < requests; ++i) {
        MonoAotModule *owner = NULL;
        guint32 slot = 0, actual_stride = 0;
        if (i == capacity) {
            printf("served=%u; requesting=%u\n", i, i + 1);
            fflush(stdout);
        }
        guint8 *result = get_numerous_trampoline(MONO_AOT_TRAMP_SPECIFIC, 2, &owner, &slot, &actual_stride);
        assert(i < capacity && owner == &module && actual_stride == stride);
        assert(result == code + (size_t)i * stride && result + stride <= code + code_bytes);
        assert(slot == base + i * 2 && slot + 1 < got_slots);
        assert(module.got[slot] == NULL && module.got[slot + 1] == NULL);
        module.got[slot] = (gpointer)(uintptr_t)(i + 1);
        module.got[slot + 1] = (gpointer)(uintptr_t)(capacity + i + 1);
    }
    for (unsigned i = 0; i < requests; ++i) {
        assert(module.got[base + 2 * i] == (gpointer)(uintptr_t)(i + 1));
        assert(module.got[base + 2 * i + 1] == (gpointer)(uintptr_t)(capacity + i + 1));
    }
    assert((unsigned)module.trampoline_index[MONO_AOT_TRAMP_SPECIFIC] == requests);
    printf("PASS: served=%u capacity=%u; unique code addresses and GOT pairs; no ARM64 execution\n", requests, capacity);
    free(module.got);
    free(code);
    return 0;
}
