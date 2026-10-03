// Host regression for two libnx Mono runtime bugs, compiled against the real sources
// (mono-mmap-libnx.c and the HOST_LIBNX part of mono-tls.c) with stub headers.
//   mmap-align:         mono_valloc_aligned must return pointers aligned in absolute
//                       address, even when the fake heap starts off that alignment
//                       (SGen nursery-size=16m aborted at startup, build 66).
//   tls-detached-exit:  a key reset to NULL before thread exit must not call its
//                       destructor (POSIX); unregister_thread asserted info != NULL (65).
//   tls-attached-exit:  a destructor that re-sets then clears its key (thread_info_key_dtor)
//                       must not be reported as re-setting the value.
#include "stubs.h"
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>

void mono_nx_fakemmap_init(intptr_t memory_start, intptr_t memory_end);
void *mono_valloc_aligned(size_t size, size_t alignment, int flags, MonoMemAccountType type);
int mono_vfree(void *addr, size_t length, MonoMemAccountType type);
int mono_native_tls_alloc(MonoNativeTlsKey *key, void *destructor);
int mono_native_tls_set_value(MonoNativeTlsKey key, gpointer value);
void *mono_native_tls_get_value(MonoNativeTlsKey key);

#define MB (1024u * 1024u)

static int check(int ok, const char *what)
{
    printf("%s: %s\n", ok ? "PASS" : "FAIL", what);
    return ok ? 0 : 1;
}

static int case_mmap_align(void)
{
    // Reserve 256 MB, then start the fake heap 8 MB past a 32 MB boundary: 4 MB- and
    // 8 MB-aligned like the real heap.c split, but not 16 MB-aligned.
    size_t reserve = 256 * MB;
    uint8_t *raw = mmap(NULL, reserve, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (raw == MAP_FAILED) { perror("mmap"); return 1; }
    uintptr_t base = ((uintptr_t)raw + 32 * MB - 1) & ~(uintptr_t)(32 * MB - 1);
    uintptr_t start = base + 8 * MB;
    uintptr_t end = (uintptr_t)raw + reserve;
    mono_nx_fakemmap_init((intptr_t)start, (intptr_t)end);

    int failures = 0;
    size_t aligns[] = { 64 * 1024, 4 * MB, 8 * MB, 16 * MB, 32 * MB };
    for (unsigned i = 0; i < sizeof aligns / sizeof aligns[0]; i++) {
        void *p = mono_valloc_aligned(aligns[i], aligns[i], MONO_MMAP_READ | MONO_MMAP_WRITE, MONO_MEM_ACCOUNT_SGEN_NURSERY);
        char what[96];
        snprintf(what, sizeof what, "aligned allocation of %zu MB-aligned %zu bytes", aligns[i] / MB, aligns[i]);
        failures += check(p && ((uintptr_t)p & (aligns[i] - 1)) == 0 &&
            (uintptr_t)p >= start && (uintptr_t)p + aligns[i] <= end, what);
    }
    // A 16 MB block after an unaligned small allocation, then free and reuse.
    void *small = mono_valloc_aligned(64 * 1024, 64 * 1024, MONO_MMAP_READ, MONO_MEM_ACCOUNT_OTHER);
    void *big = mono_valloc_aligned(16 * MB, 16 * MB, MONO_MMAP_READ, MONO_MEM_ACCOUNT_OTHER);
    failures += check(small && big && ((uintptr_t)big & (16 * MB - 1)) == 0, "16 MB alignment after other allocations");
    mono_vfree(big, 16 * MB, MONO_MEM_ACCOUNT_OTHER);
    void *again = mono_valloc_aligned(16 * MB, 16 * MB, MONO_MMAP_READ, MONO_MEM_ACCOUNT_OTHER);
    failures += check(again == big, "freed aligned block is reused");
    munmap(raw, reserve);
    return failures;
}

static MonoNativeTlsKey info_key;
static int dtor_calls;
static void *dtor_last_arg;

// Same shape as mono-threads.c thread_info_key_dtor: re-set, work, clear.
static void info_key_dtor(void *arg)
{
    dtor_calls++;
    dtor_last_arg = arg;
    if (!arg) {
        fprintf(stderr, "destructor called with NULL (unregister_thread would assert)\n");
        abort();
    }
    mono_native_tls_set_value(info_key, arg);
    mono_native_tls_set_value(info_key, NULL);
}

static int case_tls(int detached)
{
    stub_thread_reset();
    mono_native_tls_alloc(&info_key, (void *)info_key_dtor);
    int value = 42;
    mono_native_tls_set_value(info_key, &value);
    if (detached)
        mono_native_tls_set_value(info_key, NULL);   // mono detached the thread itself
    dtor_calls = 0;
    dtor_last_arg = NULL;
    stub_thread_exit();                               // libnx runs the TLS destructor
    if (detached)
        return check(dtor_calls == 0, "detached thread exit does not call the destructor");
    return check(dtor_calls == 1 && dtor_last_arg == &value,
        "attached thread exit calls the destructor once with its value");
}

int main(int argc, char **argv)
{
    const char *which = argc > 1 ? argv[1] : "all";
    int failures = 0;
    if (!strcmp(which, "all") || !strcmp(which, "mmap-align"))
        failures += case_mmap_align();
    if (!strcmp(which, "all") || !strcmp(which, "tls-detached-exit"))
        failures += case_tls(1);
    if (!strcmp(which, "all") || !strcmp(which, "tls-attached-exit"))
        failures += case_tls(0);
    return failures ? 1 : 0;
}
