// heap.c from the mono-nx fork (native/shared/heap.c) with a configurable split.
// The fork splits 50/50; tModLoader with large mods exhausts the libnx half (Mesa
// nouveau then corrupts its slab cache), while SGen uses well under its half.
#ifndef MONO_NX_LIBNX_HEAP_PERMILLE
#define MONO_NX_LIBNX_HEAP_PERMILLE 500
#endif
#if MONO_NX_LIBNX_HEAP_PERMILLE < 250 || MONO_NX_LIBNX_HEAP_PERMILLE > 850
#error "MONO_NX_LIBNX_HEAP_PERMILLE out of range"
#endif
#include <switch.h>
#include <io_util.h>

// Store the values here for debugging since we can't print during heap init
static intptr_t mono_heap_start, mono_heap_end, libnx_heap_start, libnx_heap_end;

// MONO_NX_GUARD_PAGES > 0 reserves that many pages at the top of the libnx range, outside
// newlib's heap (whose layout from the bottom is unchanged), for mesa_guard.c.
#ifndef MONO_NX_GUARD_PAGES
#define MONO_NX_GUARD_PAGES 0
#endif
uintptr_t mono_nx_guard_region, mono_nx_guard_region_size;
uintptr_t mono_nx_libnx_heap_start, mono_nx_libnx_heap_end;

// MONO_NX_NV_TRANSFERMEM_MB overrides libnx's 8 MB nvdrv transfer memory (nvservices' own pool for
// nvmap handles and GPU mappings; taken from the libnx heap). tmod21-23 ran out of it.
#ifdef MONO_NX_NV_TRANSFERMEM_MB
u32 __nx_nv_transfermem_size = MONO_NX_NV_TRANSFERMEM_MB * 0x100000;
#endif

void heap_debug()
{
    io_debugf("libnx heap: %p-%p (%zu MB)", 
        (void*)libnx_heap_start, (void*)libnx_heap_end, (size_t)(libnx_heap_end - libnx_heap_start) / 1024 / 1024);
    io_debugf("mono heap: %p-%p (%zu MB)", 
        (void*)mono_heap_start, (void*)mono_heap_end, (size_t)(mono_heap_end - mono_heap_start) / 1024 / 1024);
#ifdef MONO_NX_NV_TRANSFERMEM_MB
    io_debugf("nv transfermem: %u MB", (unsigned)(__nx_nv_transfermem_size >> 20));
#endif
    if (mono_nx_guard_region)
        io_debugf("guard region: %p+0x%zx (reserved from the libnx top)",
            (void*)mono_nx_guard_region, (size_t)mono_nx_guard_region_size);
}

// Custom symbol exported by mono
void mono_nx_fakemmap_init(intptr_t memory_start, intptr_t memory_end);

// Heap init code for libnx, except we steal half the memory from newlib for the custom mono fake mmap allocator.
void __libnx_initheap(void)
{
    void*  addr;
    size_t size = 0;
    size_t mem_available = 0, mem_used = 0;
    extern size_t __nx_heap_size;

    if (envHasHeapOverride()) {
        addr = envGetHeapOverrideAddr();
        size = envGetHeapOverrideSize();
    }
    else {
        if (__nx_heap_size==0) {
            svcGetInfo(&mem_available, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
            svcGetInfo(&mem_used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
            if (mem_available > mem_used+0x200000)
                size = (mem_available - mem_used - 0x200000) & ~0x1FFFFF;
            if (size==0)
                size = 0x2000000*16;
        }
        else {
            size = __nx_heap_size;
        }

        Result rc = svcSetHeapSize(&addr, size);

        if (R_FAILED(rc))
            diagAbortWithResult(MAKERESULT(Module_Libnx, LibnxError_HeapAllocFailed));
    }

	// libnx (newlib malloc: Mesa, GPU buffer objects, textures, Mono internals) gets
	// MONO_NX_LIBNX_HEAP_PERMILLE of the heap, Mono's fake mmap (SGen) the rest.
	// Align the mono side to 4MB because that's the biggest granularity it needs.
	size_t newlib_size = (size_t)((unsigned __int128)size * MONO_NX_LIBNX_HEAP_PERMILLE / 1000);
	mono_heap_start = (((intptr_t)addr + newlib_size) + (0x400000 - 1)) & ~(0x400000 - 1);
	mono_heap_end   = (intptr_t)addr + size;

    libnx_heap_start = (intptr_t)addr;
    libnx_heap_end = mono_heap_start;

    // Newlib
    extern char* fake_heap_start;
    extern char* fake_heap_end;

    mono_nx_libnx_heap_start = libnx_heap_start;
    mono_nx_libnx_heap_end = libnx_heap_end;
    mono_nx_guard_region_size = MONO_NX_GUARD_PAGES * 0x1000;
    mono_nx_guard_region = mono_nx_guard_region_size ? libnx_heap_end - mono_nx_guard_region_size : 0;

    fake_heap_start = (char*)libnx_heap_start;
    fake_heap_end   = (char*)(libnx_heap_end - mono_nx_guard_region_size);

    // Note that even tho we set the pointers newlib's heap is still not ready cause libnx hasn't called the thread init function yet. We can't use malloc yet.
	mono_nx_fakemmap_init(mono_heap_start, mono_heap_end);
}