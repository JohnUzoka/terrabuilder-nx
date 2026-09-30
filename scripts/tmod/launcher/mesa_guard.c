// Diagnostic guard for Mesa nouveau's slab caches (struct nouveau_mman).
//
// tmod21/22 crash in nouveau_mm_allocate: sprite index data overwrites a bucket list head
// at the same offset (+0x288) inside the GART cache. Linked with
// -Wl,--wrap=nouveau_mm_{create,allocate,free,free_work,destroy}, this file:
// - moves every cache to its own page in a reserved region (heap_split.c carves it from the
//   top of the libnx heap, so newlib's layout is unchanged) and keeps those pages read-only
//   except while Mesa's mm functions run, so a stray CPU write faults at the culprit;
// - keeps the original malloc'd block allocated and filled with a canary, so a write aimed at
//   the old location is logged (hexdump, recent mm events, overlapping buffer objects);
// - checks the relocated caches' list heads on every call (a GPU write bypasses page
//   permissions but still breaks them);
// - installs a libnx exception handler that logs registers and a frame-pointer backtrace,
//   flushes the buffered log file, then returns the exception to the kernel so Atmosphere
//   still writes its crash report.
// Compiled for the host with -DMESA_GUARD_HOST for the proof harness (mprotect, printf).

#include <stdalign.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef MESA_GUARD_HOST
#include <pthread.h>
#include <sys/mman.h>
typedef pthread_mutex_t guard_mutex_t;
static guard_mutex_t guard_lock;
static void guard_lock_init(void)
{
    pthread_mutexattr_t attr;
    pthread_mutexattr_init(&attr);
    pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(&guard_lock, &attr);
}
#define GUARD_LOCK() pthread_mutex_lock(&guard_lock)
#define GUARD_UNLOCK() pthread_mutex_unlock(&guard_lock)
#define guard_log(...) (printf(__VA_ARGS__), putchar('\n'))
extern uintptr_t mono_nx_guard_region, mono_nx_guard_region_size;
extern uintptr_t mono_nx_libnx_heap_start, mono_nx_libnx_heap_end;
static int guard_protect(bool writable)
{
    return mprotect((void *)mono_nx_guard_region, mono_nx_guard_region_size,
        writable ? PROT_READ | PROT_WRITE : PROT_READ);
}
#else
#include <switch.h>
#include <io_util.h>
static RMutex guard_lock;
static void guard_lock_init(void) { rmutexInit(&guard_lock); }
#define GUARD_LOCK() rmutexLock(&guard_lock)
#define GUARD_UNLOCK() rmutexUnlock(&guard_lock)
#define guard_log(...) io_debugf(__VA_ARGS__)
// Exported by heap_split.c when built with MONO_NX_GUARD_PAGES.
extern uintptr_t mono_nx_guard_region, mono_nx_guard_region_size;
extern uintptr_t mono_nx_libnx_heap_start, mono_nx_libnx_heap_end;
static int guard_protect(bool writable)
{
    return (int)svcSetMemoryPermission((void *)mono_nx_guard_region, mono_nx_guard_region_size,
        writable ? Perm_Rw : Perm_R);
}
#endif

// Mesa 20.1 src/gallium/drivers/nouveau/nouveau_mm.c (switch-20.1.0-rc3), checked against the
// linked nouveau_mm_create: malloc(0x380), buckets from +8, 56 bytes each.
struct list_head { struct list_head *prev, *next; };
struct mm_bucket { struct list_head free, used, full; int num_free; };
#define MM_NUM_BUCKETS 15
struct nouveau_mman_view { void *dev; struct mm_bucket bucket[MM_NUM_BUCKETS]; };
#define MMAN_SIZE 0x380
struct nouveau_bo_view { void *device; uint32_t handle; uint64_t size; uint32_t flags; uint64_t offset; void *map; };
struct mm_slab_view { struct list_head head; struct nouveau_bo_view *bo; void *cache; int order, count, free; };
struct mm_allocation_view { void *next; void *priv; uint32_t offset; };
_Static_assert(sizeof(struct nouveau_mman_view) == 8 + 15 * 56, "mman layout");

struct nouveau_mman;
struct nouveau_bo;
struct nouveau_device;
union nouveau_bo_config;
struct nouveau_mm_allocation;
struct nouveau_mman *__real_nouveau_mm_create(struct nouveau_device *, uint32_t, union nouveau_bo_config *);
struct nouveau_mm_allocation *__real_nouveau_mm_allocate(struct nouveau_mman *, uint32_t, struct nouveau_bo **, uint32_t *);
void __real_nouveau_mm_free(struct nouveau_mm_allocation *);
void __real_nouveau_mm_free_work(void *);
void __real_nouveau_mm_destroy(struct nouveau_mman *);

#define MAX_CACHES 4
#define PAGE 0x1000
#define CANARY 0xA5
struct guarded { uint8_t *page; uint8_t *old; uint32_t domain; unsigned reports; };
static struct guarded caches[MAX_CACHES];
static int cache_count;
static int depth;
static bool protect_ok;
static bool initialized;
static unsigned long calls;

enum { EV_ALLOC = 1, EV_FREE, EV_FREE_WORK };
struct event { uint8_t op; uint8_t cache; uint16_t order; uint32_t size; uint32_t offset; uint64_t map; uint64_t bo_size; uint64_t gpu; };
#define RING 64
static struct event ring[RING];
static unsigned ring_next;

static int cache_index(const void *cache)
{
    for (int i = 0; i < cache_count; ++i)
        if (caches[i].page == cache)
            return i;
    return -1;
}

static void record(uint8_t op, int cache, int order, uint32_t size, uint32_t offset, const struct nouveau_bo_view *bo)
{
    struct event *e = &ring[ring_next++ % RING];
    e->op = op; e->cache = (uint8_t)cache; e->order = (uint16_t)order; e->size = size; e->offset = offset;
    e->map = bo ? (uint64_t)(uintptr_t)bo->map : 0;
    e->bo_size = bo ? bo->size : 0;
    e->gpu = bo ? bo->offset : 0;
}

static void dump_ring(void)
{
    static const char *names[] = { "?", "alloc", "free", "free_work" };
    unsigned n = ring_next < RING ? ring_next : RING;
    guard_log("NX_MESA_GUARD last %u mm events (oldest first):", n);
    for (unsigned i = ring_next - n; i != ring_next; ++i) {
        const struct event *e = &ring[i % RING];
        guard_log("NX_MESA_GUARD   #%u %s cache=%d order=%u size=0x%x offset=0x%x map=0x%llx bo_size=0x%llx gpu=0x%llx",
            i, names[e->op < 4 ? e->op : 0], e->cache, e->order, e->size, e->offset,
            (unsigned long long)e->map, (unsigned long long)e->bo_size, (unsigned long long)e->gpu);
    }
}

static void hexdump(const char *what, const uint8_t *base, size_t from, size_t to)
{
    for (size_t row = from & ~(size_t)31; row < to; row += 32) {
        char line[32 * 3 + 1];
        for (int i = 0; i < 32; ++i)
            snprintf(line + i * 3, 4, "%02x ", base[row + i]);
        guard_log("NX_MESA_GUARD %s +0x%03zx: %s", what, row, line);
    }
}

static bool heap_pointer(const void *p)
{
    uintptr_t a = (uintptr_t)p;
    if (a & 7)
        return false;
    if (a >= mono_nx_libnx_heap_start && a < mono_nx_libnx_heap_end)
        return true;
    return a >= mono_nx_guard_region && a < mono_nx_guard_region + mono_nx_guard_region_size;
}

// Log live slab BOs whose CPU mapping or GPU range covers addr.
static void find_overlapping_bos(uintptr_t addr)
{
    for (int c = 0; c < cache_count; ++c) {
        struct nouveau_mman_view *m = (struct nouveau_mman_view *)caches[c].page;
        for (int b = 0; b < MM_NUM_BUCKETS; ++b) {
            struct list_head *heads[3] = { &m->bucket[b].free, &m->bucket[b].used, &m->bucket[b].full };
            for (int h = 0; h < 3; ++h) {
                int guard = 0;
                for (struct list_head *n = heads[h]->next; n != heads[h] && heap_pointer(n) && guard < 100000; n = n->next, ++guard) {
                    struct mm_slab_view *s = (struct mm_slab_view *)n;
                    if (!heap_pointer(s->bo))
                        continue;
                    uintptr_t map = (uintptr_t)s->bo->map;
                    if (map && addr >= map && addr < map + s->bo->size)
                        guard_log("NX_MESA_GUARD   address 0x%llx is INSIDE slab bo map 0x%llx+0x%llx (cache %d order %d)",
                            (unsigned long long)addr, (unsigned long long)map, (unsigned long long)s->bo->size, c, s->order);
                }
            }
        }
    }
}

static void flush_log(void) { fflush(NULL); }

static void check_canaries(const char *where)
{
    for (int c = 0; c < cache_count; ++c) {
        struct guarded *g = &caches[c];
        size_t first = MMAN_SIZE, last = 0;
        for (size_t i = 0; i < MMAN_SIZE; ++i)
            if (g->old[i] != CANARY) {
                if (first == MMAN_SIZE)
                    first = i;
                last = i;
            }
        if (first == MMAN_SIZE)
            continue;
        if (g->reports++ < 4) {
            guard_log("NX_MESA_GUARD STRAY WRITE into old cache %d block %p (domain 0x%x): bytes +0x%zx..+0x%zx changed, seen at %s after %lu mm calls",
                c, (void *)g->old, g->domain, first, last, where, calls);
            hexdump("old", g->old, first, last + 1);
            find_overlapping_bos((uintptr_t)g->old + first);
            dump_ring();
            flush_log();
        }
        memset(g->old, CANARY, MMAN_SIZE);
    }
}

static void check_lists(const char *where)
{
    for (int c = 0; c < cache_count; ++c) {
        struct nouveau_mman_view *m = (struct nouveau_mman_view *)caches[c].page;
        for (int b = 0; b < MM_NUM_BUCKETS; ++b) {
            struct list_head *heads[3] = { &m->bucket[b].free, &m->bucket[b].used, &m->bucket[b].full };
            for (int h = 0; h < 3; ++h) {
                if (heap_pointer(heads[h]->next) && heap_pointer(heads[h]->prev))
                    continue;
                guard_log("NX_MESA_GUARD CORRUPT cache %d bucket %d list %d at %s: next=%p prev=%p (relocated page %p; CPU writes fault, so this is a GPU/DMA write or Mesa itself)",
                    c, b, h, where, (void *)heads[h]->next, (void *)heads[h]->prev, (void *)caches[c].page);
                hexdump("page", caches[c].page, 0, MMAN_SIZE);
                dump_ring();
                flush_log();
#ifdef MESA_GUARD_HOST
                abort();
#else
                diagAbortWithResult(MAKERESULT(Module_Libnx, LibnxError_ShouldNotHappen));
#endif
            }
        }
    }
}

static void enter(const char *where)
{
    GUARD_LOCK();
    ++calls;
    if (depth++ == 0) {
        check_canaries(where);
        check_lists(where);
        if (protect_ok)
            guard_protect(true);
    }
}

static void leave(void)
{
    if (--depth == 0 && protect_ok)
        guard_protect(false);
    GUARD_UNLOCK();
}

static void relist(struct list_head *h) { h->prev = h->next = h; }

#ifdef MESA_GUARD_HOST
int nouveau_mm_guard_test_old(int c, uint8_t **out) { *out = caches[c].old; return c < cache_count; }
#endif

struct nouveau_mman *__wrap_nouveau_mm_create(struct nouveau_device *dev, uint32_t domain, union nouveau_bo_config *config)
{
    if (!initialized) {
        guard_lock_init();
        initialized = true;
    }
    GUARD_LOCK();
    struct nouveau_mman *real = __real_nouveau_mm_create(dev, domain, config);
    if (!real || cache_count >= MAX_CACHES || !mono_nx_guard_region ||
        mono_nx_guard_region_size < (size_t)(cache_count + 1) * PAGE) {
        guard_log("NX_MESA_GUARD cache %p (domain 0x%x) NOT guarded (count=%d region=%p)",
            (void *)real, domain, cache_count, (void *)mono_nx_guard_region);
        GUARD_UNLOCK();
        return real;
    }
    if (cache_count == 0) {
        int rc = guard_protect(true);
        protect_ok = rc == 0;
        guard_log("NX_MESA_GUARD region %p+0x%zx page protection %s (rc=0x%x)", (void *)mono_nx_guard_region,
            (size_t)mono_nx_guard_region_size, protect_ok ? "enabled" : "UNAVAILABLE", rc);
    } else if (protect_ok) {
        guard_protect(true);
    }
    struct guarded *g = &caches[cache_count];
    g->page = (uint8_t *)(mono_nx_guard_region + (uintptr_t)cache_count * PAGE);
    g->old = (uint8_t *)real;
    g->domain = domain;
    memcpy(g->page, real, MMAN_SIZE);
    struct nouveau_mman_view *m = (struct nouveau_mman_view *)g->page;
    for (int b = 0; b < MM_NUM_BUCKETS; ++b) {
        relist(&m->bucket[b].free);
        relist(&m->bucket[b].used);
        relist(&m->bucket[b].full);
    }
    memset(g->old, CANARY, MMAN_SIZE);
    guard_log("NX_MESA_GUARD cache %d domain 0x%x: old block %p (canary) -> page %p", cache_count, domain,
        (void *)g->old, (void *)g->page);
    ++cache_count;
    if (protect_ok)
        guard_protect(false);
    GUARD_UNLOCK();
    return (struct nouveau_mman *)g->page;
}

struct nouveau_mm_allocation *__wrap_nouveau_mm_allocate(struct nouveau_mman *cache, uint32_t size,
    struct nouveau_bo **bo, uint32_t *offset)
{
    int c = cache_index(cache);
    if (c < 0)
        return __real_nouveau_mm_allocate(cache, size, bo, offset);
    enter("allocate");
    struct nouveau_mm_allocation *a = __real_nouveau_mm_allocate(cache, size, bo, offset);
    const struct mm_allocation_view *v = (const struct mm_allocation_view *)a;
    int order = v && heap_pointer(v->priv) ? ((struct mm_slab_view *)v->priv)->order : -1;
    record(EV_ALLOC, c, order, size, offset ? *offset : 0, bo ? (const struct nouveau_bo_view *)*bo : NULL);
    leave();
    return a;
}

static void free_common(struct nouveau_mm_allocation *alloc, uint8_t op, const char *where)
{
    const struct mm_allocation_view *v = (const struct mm_allocation_view *)alloc;
    const struct mm_slab_view *s = v && heap_pointer(v->priv) ? (const struct mm_slab_view *)v->priv : NULL;
    int c = s ? cache_index(s->cache) : -1;
    enter(where);
    record(op, c, s ? s->order : -1, 0, v ? v->offset : 0, s && heap_pointer(s->bo) ? s->bo : NULL);
    if (op == EV_FREE)
        __real_nouveau_mm_free(alloc);
    else
        __real_nouveau_mm_free_work(alloc);
    leave();
}

void __wrap_nouveau_mm_free(struct nouveau_mm_allocation *alloc) { free_common(alloc, EV_FREE, "free"); }
void __wrap_nouveau_mm_free_work(void *data) { free_common(data, EV_FREE_WORK, "free_work"); }

void __wrap_nouveau_mm_destroy(struct nouveau_mman *cache)
{
    // The real destroy frees the cache block with free(); relocated caches are leaked at
    // screen teardown instead.
    if (cache_index(cache) < 0)
        __real_nouveau_mm_destroy(cache);
}

#ifndef MESA_GUARD_HOST
alignas(16) u8 __nx_exception_stack[0x10000];
u64 __nx_exception_stack_size = sizeof(__nx_exception_stack);
extern char _start[];

static bool readable(uintptr_t addr)
{
    MemoryInfo info;
    u32 page_info;
    if (R_FAILED(svcQueryMemory(&info, &page_info, addr)))
        return false;
    return (info.perm & Perm_R) && info.type != MemType_Unmapped && addr + 16 <= info.addr + info.size;
}

void __libnx_exception_handler(ThreadExceptionDump *ctx)
{
    static volatile int active;
    if (__atomic_exchange_n(&active, 1, __ATOMIC_SEQ_CST))
        svcReturnFromException(0xF801);
    uintptr_t base = (uintptr_t)_start;
    guard_log("NX_EXC desc=0x%x pc=tmodloader+0x%llx lr=tmodloader+0x%llx far=0x%llx esr=0x%x sp=0x%llx fp=0x%llx",
        ctx->error_desc, (unsigned long long)(ctx->pc.x - base), (unsigned long long)(ctx->lr.x - base),
        (unsigned long long)ctx->far.x, ctx->esr, (unsigned long long)ctx->sp.x, (unsigned long long)ctx->fp.x);
    for (int i = 0; i < 29; i += 4)
        guard_log("NX_EXC x%02d=0x%016llx x%02d=0x%016llx x%02d=0x%016llx x%02d=0x%016llx",
            i, (unsigned long long)ctx->cpu_gprs[i].x, i + 1, i + 1 < 29 ? (unsigned long long)ctx->cpu_gprs[i + 1].x : 0,
            i + 2, i + 2 < 29 ? (unsigned long long)ctx->cpu_gprs[i + 2].x : 0,
            i + 3, i + 3 < 29 ? (unsigned long long)ctx->cpu_gprs[i + 3].x : 0);
    uintptr_t fp = ctx->fp.x;
    for (int i = 0; i < 64 && fp && !(fp & 15) && readable(fp); ++i) {
        uintptr_t ret = ((uintptr_t *)fp)[1];
        guard_log("NX_EXC frame %02d ret=tmodloader+0x%llx", i, (unsigned long long)(ret - base));
        uintptr_t next = ((uintptr_t *)fp)[0];
        if (next <= fp)
            break;
        fp = next;
    }
    if (cache_count) {
        for (int c = 0; c < cache_count; ++c)
            if (ctx->far.x >= (uintptr_t)caches[c].page && ctx->far.x < (uintptr_t)caches[c].page + PAGE)
                guard_log("NX_EXC fault address is inside guarded cache %d page (+0x%llx): CPU stray write caught at pc",
                    c, (unsigned long long)(ctx->far.x - (uintptr_t)caches[c].page));
        check_canaries("exception");
        dump_ring();
    }
    guard_log("NX_EXC handing the exception back to the kernel for the crash report");
    flush_log();
    svcReturnFromException(0xF801);
}
#endif
