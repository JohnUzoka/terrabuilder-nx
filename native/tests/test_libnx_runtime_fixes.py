#!/usr/bin/env python3
"""Host regression for the libnx fake-mmap alignment and emulated-TLS destructor fixes.

Compiles the real mono-mmap-libnx.c and the HOST_LIBNX part of mono-tls.c from a
dotnet_runtime checkout against minimal stub headers, then runs
test_libnx_runtime_fixes.c. Each case runs in its own process so an abort (the
pre-fix behaviour) is reported per case.

Usage: python3 test_libnx_runtime_fixes.py --runtime-source /path/to/runtime-source
"""
import argparse
from pathlib import Path
import subprocess
import tempfile

STUBS = r'''
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <pthread.h>
typedef uint8_t u8; typedef uint64_t u64; typedef int32_t s32;
typedef void *gpointer; typedef int gboolean; typedef int MonoNativeTlsKey;
#define TRUE 1
#define FALSE 0
typedef int MonoMemAccountType;
enum { MONO_MEM_ACCOUNT_OTHER, MONO_MEM_ACCOUNT_SGEN_NURSERY, MONO_MEM_ACCOUNT_INTERP_STACK };
enum { MONO_MMAP_NONE = 0, MONO_MMAP_READ = 1, MONO_MMAP_WRITE = 2, MONO_MMAP_EXEC = 4,
       MONO_MMAP_DISCARD = 8, MONO_MMAP_PRIVATE = 16, MONO_MMAP_SHARED = 32,
       MONO_MMAP_ANON = 64, MONO_MMAP_FIXED = 128, MONO_MMAP_32BIT = 256 };
static inline void mono_account_mem(MonoMemAccountType t, ssize_t n) { (void)t; (void)n; }
typedef struct { gboolean inside_critical_region; } MonoThreadInfo;
static inline MonoThreadInfo *mono_thread_info_current_unchecked(void) { return NULL; }
#define g_error(...) do { fprintf(stderr, "g_error: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); abort(); } while (0)
#define g_assert(x) do { if (!(x)) { fprintf(stderr, "g_assert: %s\n", #x); abort(); } } while (0)
#define G_LOG_LEVEL_ERROR 0
#define MONO_TRACE_GC 0
#define MONO_TRACE_DIAGNOSTICS 0
#define mono_trace(level, mask, ...) do { (void)(level); (void)(mask); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); } while (0)
#define mono_trace_message(mask, ...) do { (void)(mask); } while (0)
/* libnx: one real TLS slot with a destructor, one simulated thread. */
typedef int Mutex;
#define INVALID_HANDLE 0
static inline void mutexLock(Mutex *m) { (void)m; }
static inline void mutexUnlock(Mutex *m) { (void)m; }
extern void (*stub_tls_dtor)(void *);
extern void *stub_tls_value;
static inline s32 threadTlsAlloc(void (*d)(void *)) { stub_tls_dtor = d; return 0; }
static inline void *threadTlsGet(s32 slot) { (void)slot; return stub_tls_value; }
static inline void threadTlsSet(s32 slot, void *v) { (void)slot; stub_tls_value = v; }
static inline void stub_thread_reset(void) { stub_tls_value = NULL; }
static inline void stub_thread_exit(void) { void *v = stub_tls_value; stub_tls_value = NULL; if (v && stub_tls_dtor) stub_tls_dtor(v); }
'''

HARNESS_GLOBALS = 'void (*stub_tls_dtor)(void *);\nvoid *stub_tls_value;\n'


def libnx_part(text, name, marker):
    start = text.index(marker)
    end = text.rindex("#endif")
    return f'#include "stubs.h"\n#line {text[:start].count(chr(10)) + 2} "{name}"\n' + text[start + len(marker):end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-source", required=True, type=Path)
    parser.add_argument("--work-dir", type=Path, default=Path(tempfile.gettempdir()) / "libnx-runtime-fixes")
    parser.add_argument("--cc", default="cc")
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir))
    utils = args.runtime_source / "src/mono/mono/utils"
    (work / "stubs.h").write_text(STUBS)
    mmap = (utils / "mono-mmap-libnx.c").read_text()
    for name in ("switch.h", "mono-logger-internals.h"):
        (work / name).write_text('#include "stubs.h"\n')
    body = mmap[mmap.index("// Custom mmap-like impl"):mmap.rindex("#endif")]
    (work / "mmap.c").write_text('#include "stubs.h"\n#include <stddef.h>\n#line %d "mono-mmap-libnx.c"\n' %
                                 (mmap[:mmap.index("// Custom mmap-like impl")].count("\n") + 1) + body)
    tls = (utils / "mono-tls.c").read_text()
    (work / "tls.c").write_text(libnx_part(tls, "mono-tls.c", "#if HOST_LIBNX"))
    harness = Path(__file__).with_suffix(".c")
    (work / "globals.c").write_text(HARNESS_GLOBALS)
    exe = work / "test"
    cmd = [args.cc, "-std=gnu11", "-O2", "-Wall", "-Wno-unused-function", "-Werror", "-I", str(work),
           str(harness), str(work / "mmap.c"), str(work / "tls.c"), str(work / "globals.c"), "-lpthread", "-o", str(exe)]
    subprocess.run(cmd, check=True)
    failed = 0
    for case in ("mmap-align", "tls-detached-exit", "tls-attached-exit"):
        r = subprocess.run([str(exe), case], capture_output=True, text=True)
        out = (r.stdout + r.stderr).strip()
        status = "ok" if r.returncode == 0 else ("ABORT" if r.returncode < 0 else "FAIL")
        print(f"[{status}] {case}\n    " + out.replace("\n", "\n    "))
        failed += r.returncode != 0
    print("ALL PASS" if not failed else f"{failed} case(s) failed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
