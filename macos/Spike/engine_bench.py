#!/usr/bin/env python3
"""Compare the Mac's two speech engines on this machine: Lemonade's Metal Kokoro (lemond plus
its `koko` backend process, over HTTP) and in-process Phonon (libdynamite_phonon.dylib).

For each engine: memory (physical footprint, as Activity Monitor's Memory column, and resident
size), CPU time and cores busy while synthesizing, GPU utilisation, idle cost once loaded, and
time to first audio cold (engine not loaded) and warm (median of 5).

    python3 -I macos/Spike/engine_bench.py [kokoro|phonon|both]

Phonon runs in a child process of this script so its memory is its own; Kokoro is measured on
the lemond + koko processes (Lemonade's chat model, llama-server, is not counted).
"""
import ctypes, ctypes.util, http.client, json, os, re, statistics, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
LIB = os.environ.get("DYNAMITE_PHONON_LIB", os.path.join(ROOT, "windows/native/phonon/target/release/libdynamite_phonon.dylib"))
MODEL = os.path.expanduser("~/Library/Application Support/DynamiteTTS/models/phonon-31m-en")
SHORT = "The quick brown fox jumps over the lazy dog near the river bank."
PARA = ("Phonon is a small text to speech model that streams audio as it generates it. "
        "On an Apple Silicon laptop the whole paragraph renders many times faster than realtime, "
        "so playback can begin after the first few frames while the rest is still being produced. "
        "This paragraph exists to measure how long a longer passage takes from the first word to the last, "
        "and whether the speaking rate stays steady throughout.")
RUNS = 5
NCPU = os.cpu_count()

# ---- per-process counters through libproc (no sudo, same user) ----------------------------

class RusageInfoV2(ctypes.Structure):
    _fields_ = [("uuid", ctypes.c_uint8 * 16)] + [(n, ctypes.c_uint64) for n in (
        "user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups", "pageins", "wired_size",
        "resident_size", "phys_footprint", "proc_start_abstime", "proc_exit_abstime",
        "child_user_time", "child_system_time", "child_pkg_idle_wkups", "child_interrupt_wkups",
        "child_pageins", "child_elapsed_abstime", "diskio_bytesread", "diskio_byteswritten")]

class Timebase(ctypes.Structure):
    _fields_ = [("numer", ctypes.c_uint32), ("denom", ctypes.c_uint32)]

libc = ctypes.CDLL(ctypes.util.find_library("c"))
_tb = Timebase(); libc.mach_timebase_info(ctypes.byref(_tb))
TICK_S = _tb.numer / _tb.denom / 1e9

def usage(pid):
    """(cpu seconds, phys_footprint bytes, resident bytes) or None if the process is gone."""
    info = RusageInfoV2()
    if libc.proc_pid_rusage(pid, 2, ctypes.byref(info)) != 0:
        return None
    return ((info.user_time + info.system_time) * TICK_S, info.phys_footprint, info.resident_size)

def gpu_util():
    out = subprocess.run(["ioreg", "-r", "-d", "1", "-c", "IOAccelerator"], capture_output=True, text=True).stdout
    m = re.search(r'"Device Utilization %"=(\d+)', out)
    return int(m.group(1)) if m else None

class Sampler:
    """Polls a set of pids (a callable, so a restarted backend is picked up) every `period` s."""
    def __init__(self, pids, period=0.025):
        self.pids, self.period = pids, period
        self.lock = threading.Lock()
        self.peak_fp = self.peak_rss = 0
        self.gpu = []
        self.stop = threading.Event()
        self.paused = threading.Event()
        self.t = threading.Thread(target=self.run, daemon=True); self.t.start()
        self.g = threading.Thread(target=self.run_gpu, daemon=True); self.g.start()

    def snapshot(self):
        cpu = fp = rss = 0
        for pid in self.pids():
            u = usage(pid)
            if u: cpu += u[0]; fp += u[1]; rss += u[2]
        return cpu, fp, rss

    def run(self):
        while not self.stop.is_set():
            if self.paused.is_set(): time.sleep(self.period); continue
            _, fp, rss = self.snapshot()
            with self.lock:
                self.peak_fp = max(self.peak_fp, fp); self.peak_rss = max(self.peak_rss, rss)
            time.sleep(self.period)

    def run_gpu(self):
        while not self.stop.is_set():
            if self.paused.is_set(): time.sleep(0.1); continue
            g = gpu_util()
            if g is not None:
                with self.lock: self.gpu.append(g)
            time.sleep(0.1)

    def reset_peak(self):
        with self.lock:
            _, fp, rss = self.snapshot()
            self.peak_fp, self.peak_rss, self.gpu = fp, rss, []

    def peaks(self):
        with self.lock:
            return self.peak_fp, self.peak_rss, (max(self.gpu) if self.gpu else None), (statistics.mean(self.gpu) if self.gpu else None)

    def close(self):
        self.stop.set(); self.t.join(); self.g.join()

MB = 1 / 1e6

def measured(sampler, fn):
    """Runs fn() -> (ttfa_s, audio_s); returns a dict with timing, CPU and peak memory."""
    sampler.reset_peak()
    cpu0, _, _ = sampler.snapshot(); w0 = time.perf_counter()
    ttfa, audio = fn()
    wall = time.perf_counter() - w0; cpu1, fp, rss = sampler.snapshot()
    peak_fp, peak_rss, gpu_peak, gpu_mean = sampler.peaks()
    cpu = cpu1 - cpu0
    return dict(ttfa_ms=ttfa * 1000, wall_s=wall, audio_s=audio, rtf=audio / wall if wall else 0,
                cpu_s=cpu, cores=cpu / wall if wall else 0, machine_pct=100 * cpu / wall / NCPU if wall else 0,
                fp_mb=fp * MB, peak_fp_mb=peak_fp * MB, peak_rss_mb=peak_rss * MB,
                gpu_peak=gpu_peak, gpu_mean=gpu_mean)

def idle(sampler, seconds=10):
    sampler.paused.set(); time.sleep(0.2)
    cpu0, _, _ = sampler.snapshot(); time.sleep(seconds); cpu1, fp, rss = sampler.snapshot()
    sampler.paused.clear()
    return dict(idle_cpu_pct_of_core=100 * (cpu1 - cpu0) / seconds, fp_mb=fp * MB, rss_mb=rss * MB)

def summarize(label, rows):
    med = lambda k: statistics.median(r[k] for r in rows)
    gp = [r["gpu_peak"] for r in rows if r["gpu_peak"] is not None]
    return dict(label=label, n=len(rows), ttfa_ms=med("ttfa_ms"), ttfa_min=min(r["ttfa_ms"] for r in rows),
                ttfa_max=max(r["ttfa_ms"] for r in rows), wall_s=med("wall_s"), audio_s=med("audio_s"),
                rtf=med("rtf"), cpu_s=med("cpu_s"), cores=med("cores"), machine_pct=med("machine_pct"),
                peak_fp_mb=max(r["peak_fp_mb"] for r in rows), peak_rss_mb=max(r["peak_rss_mb"] for r in rows),
                gpu_peak=max(gp) if gp else None)

# ---- Kokoro through Lemonade ---------------------------------------------------------------

HOST, PORT = "127.0.0.1", 13305

def pids_matching(*needles):
    out = subprocess.run(["ps", "-axo", "pid=,command="], capture_output=True, text=True).stdout
    pids = []
    for line in out.splitlines():
        pid, _, cmd = line.strip().partition(" ")
        if any(n in cmd for n in needles):
            pids.append(int(pid))
    return pids

def lemonade_pids():
    return pids_matching("/lemonade/lemond ", "/bin/kokoro/")

def koko_pids():
    return pids_matching("/bin/kokoro/")

def post(path, body, stream=False):
    conn = http.client.HTTPConnection(HOST, PORT, timeout=300)
    conn.request("POST", path, json.dumps(body), {"Content-Type": "application/json"})
    return conn, conn.getresponse()

def kokoro_speak(text, voice):
    def run():
        t0 = time.perf_counter()
        conn, resp = post("/api/v1/audio/speech", {"model": "kokoro-v1", "input": text, "voice": voice, "speed": 1.0,
                                                   "stream_format": "audio", "response_format": "pcm"})
        if resp.status != 200: raise SystemExit(f"speech {resp.status}: {resp.read()[:300]}")
        first, total = None, 0
        while True:
            chunk = resp.read1(65536) if hasattr(resp, "read1") else resp.read(4096)
            if not chunk: break
            if first is None: first = time.perf_counter() - t0
            total += len(chunk)
        conn.close()
        return first, total / 2 / 24000
    return run

def bench_kokoro():
    voice = json.load(open(os.path.expanduser("~/Library/Application Support/DynamiteTTS/settings.json"))).get("voice", "af_heart")
    print(f"== Kokoro (Lemonade Metal), voice {voice}", flush=True)
    sampler = Sampler(lemonade_pids, period=0.05)
    res = {}
    conn, resp = post("/api/v1/unload", {"model_name": "kokoro-v1"}); print("unload:", resp.status, resp.read()[:120]); conn.close()
    time.sleep(2)
    res["unloaded_idle"] = idle(sampler, 3)
    res["cold"] = measured(sampler, kokoro_speak(SHORT, voice))
    print("cold:", res["cold"], flush=True)
    res["idle"] = idle(sampler)
    print("idle loaded:", res["idle"], flush=True)
    for name, text in (("short", SHORT), ("para", PARA)):
        rows = [measured(sampler, kokoro_speak(text, voice)) for _ in range(RUNS)]
        res[name] = summarize(name, rows); print(name, res[name], flush=True)
    res["after_runs"] = idle(sampler, 3)
    kp = koko_pids()
    res["koko_only_fp_mb"] = sum(usage(p)[1] for p in kp) * MB if kp else None
    sampler.close()
    return res

# ---- Phonon in a child process -------------------------------------------------------------

def phonon_child():
    me = os.getpid()
    sampler = Sampler(lambda: [me])
    res = {"baseline": idle(sampler, 1)}
    threads = int(os.environ.get("PHONON_THREADS", "6"))

    class DpResult(ctypes.Structure):
        _fields_ = [("samples", ctypes.c_size_t), ("ttfa_ms", ctypes.c_double), ("total_ms", ctypes.c_double)]
    CB = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.POINTER(ctypes.c_float), ctypes.c_size_t, ctypes.c_void_p)
    state = {}
    @CB
    def cb(pcm, n, user):
        if state.get("first") is None: state["first"] = time.perf_counter()
        state["samples"] += n
        return 1

    lib = None
    def speak(text, prime=False):
        def run():
            state.update(first=None, samples=0)
            t0 = state.get("t0") or time.perf_counter()
            r = DpResult()
            if not lib.dp_speak(h[0], text.encode(), None, ctypes.c_float(-1), cb, None, ctypes.byref(r)):
                raise SystemExit(lib.dp_last_error(h[0]))
            state["t0"] = None
            return state["first"] - t0, state["samples"] / 24000
        return run

    h = [None]
    def cold():
        nonlocal lib
        state["t0"] = time.perf_counter()
        lib = ctypes.CDLL(LIB)
        lib.dp_new.restype = ctypes.c_void_p
        lib.dp_new.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint32]
        lib.dp_speak.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_float, CB, ctypes.c_void_p, ctypes.POINTER(DpResult)]
        lib.dp_last_error.restype = ctypes.c_char_p
        h[0] = lib.dp_new(MODEL.encode(), b"en", threads)
        state["load_ms"] = (time.perf_counter() - state["t0"]) * 1000
        return speak(SHORT)()
    res["cold"] = measured(sampler, cold); res["cold"]["load_ms"] = state["load_ms"]
    res["idle"] = idle(sampler)
    for name, text in (("short", SHORT), ("para", PARA)):
        rows = [measured(sampler, speak(text)) for _ in range(RUNS)]
        res[name] = summarize(name, rows)
    res["threads"] = threads
    res["after_runs"] = idle(sampler, 3)
    sampler.close()
    print("RESULT " + json.dumps(res))

def bench_phonon():
    print(f"== Phonon (in-process, {LIB})", flush=True)
    out = subprocess.run([sys.executable, "-I", __file__, "phonon-child"], capture_output=True, text=True)
    line = next((l for l in out.stdout.splitlines() if l.startswith("RESULT ")), None)
    if not line: raise SystemExit(out.stdout + out.stderr)
    res = json.loads(line[7:])
    for k, v in res.items(): print(k, v)
    return res

if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "both"
    if what == "phonon-child":
        phonon_child(); sys.exit()
    results = {}
    if what in ("kokoro", "both"): results["kokoro"] = bench_kokoro()
    if what in ("phonon", "both"): results["phonon"] = bench_phonon()
    path = os.path.join(HERE, "engine_bench_results.json")
    json.dump(results, open(path, "w"), indent=1)
    print("wrote", path)
