/* Compile through test_mono_switch_table.py: the resolver cases and write helpers
 * are extracted verbatim from runtime source, not maintained copies.
 */
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define TRUE 1
#define FALSE 0
#define HOST_LIBNX 1
#define G_STRLOC __FILE__
#define GPOINTER_TO_INT(value) ((int)(intptr_t)(value))
#define g_assert assert
#define g_assert_not_reached() assert(!"MONO_ARCH_NO_CODEMAN")

typedef void *gpointer;
typedef const void *gconstpointer;
typedef unsigned char guint8;
typedef int gboolean;
typedef struct { int id; } MonoMemoryManager;
typedef struct { int id; } MonoCodeManager;
typedef struct { MonoCodeManager *code_mp; } DynamicCode;
typedef struct {
    gboolean dynamic;
    MonoMemoryManager *mem_manager;
    DynamicCode *dynamic_code;
} MonoMethod;
typedef struct { int table_size; gpointer *table; } JumpTable;
typedef struct { int type; union { JumpTable *table; } data; } MonoJumpInfo;
enum { MONO_PATCH_INFO_SWITCH };
enum { MONO_AOT_MODE_NORMAL, MONO_AOT_MODE_FULL, MONO_AOT_MODE_INTERP };
static int mono_aot_mode;
static gboolean mono_aot_only;

#ifndef MONO_ARCH_NO_CODEMAN
/* Separate stores make a skipped RX->RW translation observable. Flush publishes
 * RW contents to RX, and verifies that no store was made directly to RX.
 * These are deterministic allocation/alias models, not the libnx JIT API.
 */
#define CAPACITY 8
#define GUARD 1
static gpointer heap[CAPACITY + 2], rx[CAPACITY + 2], rw[CAPACITY + 2];
static unsigned char code_bytes[128], sentinel;
static MonoMemoryManager outer_manager = {1}, method_manager = {2};
static MonoCodeManager dynamic_manager = {3};
static DynamicCode dynamic_code = {&dynamic_manager};
static MonoMemoryManager *selected_manager;
static MonoCodeManager *selected_codeman;
static size_t allocated_size;
static int heap_allocs, code_allocs, dynamic_allocs, method_lookups;
static int dynamic_lookups, write_lookups, exec_lookups, flushes;
static int enabled;

static gpointer *heap_start(void) { return heap + GUARD; }
static gpointer *rx_start(void) { return rx + GUARD; }
static gpointer *rw_start(void) { return rw + GUARD; }

static void reset_state(void)
{
    for (size_t i = 0; i < CAPACITY + 2; ++i)
        heap[i] = rx[i] = rw[i] = &sentinel;
    selected_manager = NULL;
    selected_codeman = NULL;
    allocated_size = 0;
    heap_allocs = code_allocs = dynamic_allocs = method_lookups = 0;
    dynamic_lookups = write_lookups = exec_lookups = flushes = enabled = 0;
}

static MonoMemoryManager *m_method_get_mem_manager(MonoMethod *method)
{
    assert(method && !method->dynamic);
    ++method_lookups;
    return method->mem_manager;
}

static DynamicCode *mono_dynamic_code_hash_lookup(MonoMethod *method)
{
    assert(method && method->dynamic);
    ++dynamic_lookups;
    return method->dynamic_code;
}

static void record_size(size_t size)
{
    assert(size <= CAPACITY * sizeof(gpointer));
    allocated_size = size;
}

static gpointer mono_mem_manager_alloc(MonoMemoryManager *manager, size_t size)
{
    record_size(size);
    selected_manager = manager;
    ++heap_allocs;
    return heap_start();
}

static gpointer mono_mem_manager_code_reserve(MonoMemoryManager *manager, size_t size)
{
    record_size(size);
    selected_manager = manager;
    ++code_allocs;
    return rx_start();
}

static gpointer mono_code_manager_reserve(MonoCodeManager *manager, size_t size)
{
    record_size(size);
    selected_codeman = manager;
    ++dynamic_allocs;
    return rx_start();
}

static guint8 *mono_codeman_find_write_address(void *address, const char *trace_line)
{
    (void)trace_line;
    ++write_lookups;
    if (address != rx_start()) {
        fputs("mono_codeman_find_write_address: jit area not found for heap/unknown RX\n", stderr);
        return NULL;
    }
    assert(!enabled);
    enabled = 1;
    return (guint8 *)rw_start();
}

static void nx_jit_flush_cache_by_address(void *address)
{
    ++flushes;
    if (address != rw_start())
        return;
    assert(enabled && write_lookups == 1 && exec_lookups == 0);
    for (size_t i = 0; i < CAPACITY + 2; ++i)
        assert(rx[i] == &sentinel);
    memcpy(rx_start(), rw_start(), allocated_size);
}

static guint8 *mono_codeman_find_exec_address(void *address, const char *trace_line)
{
    (void)trace_line;
    ++exec_lookups;
    if (address != rw_start())
        return NULL;
    assert(enabled && flushes == 1);
    enabled = 0;
    return (guint8 *)rx_start();
}

#include "codeman_helpers.inc"
#endif

static gconstpointer resolve_old(MonoMemoryManager *mem_manager, MonoMethod *method,
                                guint8 *code, MonoJumpInfo *patch_info)
{
    gconstpointer target = NULL;
#ifdef MONO_ARCH_NO_CODEMAN
    (void)mem_manager; (void)method; (void)code;
#endif
    switch (patch_info->type) {
#include "switch_old.inc"
    default: assert(0);
    }
    return target;
}

static gconstpointer resolve_new(MonoMemoryManager *mem_manager, MonoMethod *method,
                                guint8 *code, MonoJumpInfo *patch_info)
{
    gconstpointer target = NULL;
#ifdef MONO_ARCH_NO_CODEMAN
    (void)mem_manager; (void)method; (void)code;
#endif
    switch (patch_info->type) {
#include "switch_new.inc"
    default: assert(0);
    }
    return target;
}

#ifndef MONO_ARCH_NO_CODEMAN
static void run_case(int old, int mode, int aot_only, int method_kind, int entries)
{
    reset_state();
    mono_aot_mode = mode;
    mono_aot_only = aot_only;
    MonoMethod actual_method = {method_kind == 2, &method_manager, &dynamic_code};
    MonoMethod *method = method_kind ? &actual_method : NULL;
    const int offsets[CAPACITY] = {0, 1, 31, -16, 63, 1, -31, 7};
    gpointer offsets_as_pointers[CAPACITY];
    for (int i = 0; i < CAPACITY; ++i)
        offsets_as_pointers[i] = (gpointer)(intptr_t)offsets[i];
    JumpTable table = {entries, offsets_as_pointers};
    MonoJumpInfo patch = {MONO_PATCH_INFO_SWITCH, {.table = &table}};
    guint8 *code = code_bytes + 32;
    gconstpointer target = (old ? resolve_old : resolve_new)(&outer_manager, method, code, &patch);
    const int uses_code = method_kind == 2 || !aot_only;
    assert(allocated_size == sizeof(gpointer) * (size_t)entries);
    assert(heap_allocs == !uses_code);
    assert(dynamic_allocs == (method_kind == 2));
    assert(code_allocs == (uses_code && method_kind != 2));
    assert(method_lookups == (method_kind == 1));
    assert(dynamic_lookups == (method_kind == 2));
    assert(selected_manager == (method_kind == 2 ? NULL : method_kind == 1 ? &method_manager : &outer_manager));
    assert(selected_codeman == (method_kind == 2 ? &dynamic_manager : NULL));
    assert(target == (uses_code ? rx_start() : heap_start()));
    assert(write_lookups == uses_code && exec_lookups == uses_code && flushes == uses_code);
    assert(!enabled);
    for (int i = 0; i < CAPACITY + 2; ++i) {
        gpointer expected = i >= GUARD && i < GUARD + entries ? code + offsets[i - GUARD] : &sentinel;
        assert(heap[i] == (uses_code ? &sentinel : expected));
        assert(rx[i] == (uses_code ? expected : &sentinel));
        assert(rw[i] == (uses_code ? expected : &sentinel));
    }
}
#endif

int main(int argc, char **argv)
{
#ifdef MONO_ARCH_NO_CODEMAN
    (void)mono_aot_mode; (void)mono_aot_only;
    MonoJumpInfo patch = {MONO_PATCH_INFO_SWITCH, {.table = NULL}};
    assert(argc == 2);
    (void)(strcmp(argv[1], "old") == 0 ? resolve_old : resolve_new)(NULL, NULL, NULL, &patch);
    return 1;
#else
    if (argc == 2 && strcmp(argv[1], "old-aot-method") == 0) {
        run_case(1, MONO_AOT_MODE_INTERP, 1, 1, CAPACITY);
        return 1;
    }
    if (argc == 2 && strcmp(argv[1], "old-aot-no-method") == 0) {
        run_case(1, MONO_AOT_MODE_INTERP, 1, 0, CAPACITY);
        return 1;
    }
    if (argc == 3 && strcmp(argv[1], "unknown-write") == 0) {
        reset_state();
        mono_aot_mode = strcmp(argv[2], "interp") == 0 ? MONO_AOT_MODE_INTERP : MONO_AOT_MODE_NORMAL;
        (void)mono_codeman_enable_write_ex(heap_start(), G_STRLOC);
        return 1;
    }
    if (argc == 3 && strcmp(argv[1], "unknown-exec") == 0) {
        reset_state();
        mono_aot_mode = strcmp(argv[2], "interp") == 0 ? MONO_AOT_MODE_INTERP : MONO_AOT_MODE_NORMAL;
        (void)mono_codeman_disable_write_ex(heap_start(), G_STRLOC);
        return 1;
    }
    assert(argc == 1);
    int old_pass = 0, new_pass = 0;
    const int sizes[] = {0, 1, CAPACITY};
    for (int mode = MONO_AOT_MODE_NORMAL; mode <= MONO_AOT_MODE_INTERP; ++mode) {
        for (int only = 0; only <= 1; ++only) {
            for (int kind = 0; kind <= 2; ++kind) {
                /* FULL is safe here for AOT heap data. FULL+RX code allocations
                 * are not claimed safe: the unchanged helpers bypass aliases.
                 */
                if (mode == MONO_AOT_MODE_FULL && (!only || kind == 2))
                    continue;
                for (size_t n = 0; n < sizeof(sizes) / sizeof(sizes[0]); ++n) {
                    run_case(0, mode, only, kind, sizes[n]);
                    ++new_pass;
                    if (mode != MONO_AOT_MODE_FULL && only && kind != 2)
                        continue;
                    run_case(1, mode, only, kind, sizes[n]);
                    ++old_pass;
                }
            }
        }
    }
    /* The existing FULL-only escape hatch stays unchanged; INTERP/NORMAL are
     * separately required to reject these unknown pointers in child processes.
     */
    reset_state();
    mono_aot_mode = MONO_AOT_MODE_FULL;
    assert(mono_codeman_enable_write_ex(heap_start(), G_STRLOC) == (guint8 *)heap_start());
    assert(mono_codeman_disable_write_ex(heap_start(), G_STRLOC) == (guint8 *)heap_start());
    assert(!write_lookups && !exec_lookups && !flushes);
    printf("PASS: patched %d cases; unchanged old %d cases; FULL boundary preserved\n", new_pass, old_pass);
    puts("PASS: method/no-method allocator selection, dynamic precedence, heap/RX return pointers, entries and guards, RW stores, flush-before-RX translation");
    return 0;
#endif
}
