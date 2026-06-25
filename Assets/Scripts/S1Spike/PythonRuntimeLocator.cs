// PythonRuntimeLocator.cs — single source of truth for embedded-Python runtime paths.
//
// Resolves the four paths pythonnet needs BEFORE PythonEngine.Initialize():
//   * LibPython   -> Python.Runtime.Runtime.PythonDLL (the libpython to dlopen)
//   * PythonHome  -> PythonEngine.PythonHome + PYTHONHOME env (CPython stdlib root)
//   * VenvSite    -> venv site-packages (kernel deps + backcast-engine live here)
//   * ProjectRoot -> python/ import root (so `import engine` resolves)
//
// Per ADR-0002, resolution lives in ONE place that branches on Application.isEditor:
//   * Editor (dev)   -> python/.venv. Repo-relative (Application.dataPath = <repo>/Assets,
//                       so <repo>/python). uv CPython install is per-user/external and
//                       resolved from pyvenv.cfg `home=` (Windows) or documented const (Mac).
//   * build (deploy) -> Application.streamingAssetsPath/PythonRuntime/{cpython, python}.
//                       Layout is written by BackcastShippableBuild (IPostprocessBuildWithReport).
//                       venv's pyvenv.cfg is DELETED by the post-process — this Locator is
//                       the single source of truth for PYTHONHOME in deploy (#33 grill 2026-06-18).
//
// Build-branch additions (#33):
//   * (#122 removed) ConfigureBeforeInitialize used to set TTWR_PYTHON_BIN for the
//     login-subprocess Python resolver; that subprocess and engine._backend_impl
//     _resolve_python_executable() were removed in #122/findings 0093, so the env is
//     no longer set (no Python reader remains).
//   * On Windows (Editor AND Player), P/Invoke AddDllDirectory + SetDefaultDllDirectories
//     so the loader can find vcruntime140.dll (uv-bundled, sibling to python313.dll) when
//     LoadLibrary resolves transitive .pyd dependencies (pyarrow / duckdb / marimo /
//     pydantic_core, etc.) — without this, VC++ Redistributable becomes a precondition on
//     a clean Win10/11 LTSC or corporate-locked machine. Runs in Editor too because the
//     Editor loads the same uv-bundled CPython + .pyd graph as deploy.
//   * runtime-manifest.json sanity assert (build branch only): every resolved path must
//     exist. Asset gaps fail loudly at startup instead of an opaque ImportError later.
//
// THREADING: EnsureResolved() reads Application.dataPath/streamingAssetsPath which
// must be read on the Unity MAIN thread. ConfigureBeforeInitialize() (called on main,
// pre-Initialize) forces resolution + caches all four strings into static fields, so
// the later background launcher thread reads cached strings only.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Python.Runtime;
using UnityEngine;

public static class PythonRuntimeLocator
{
    // Genuinely external Mac dev: the uv-managed CPython install. Not repo-relative,
    // so these stay documented absolute consts (ADR-0002: "derive where sensible; the
    // uv cpython path is genuinely external/absolute"). Editor Mac leg only.
    const string UV_LIBPYTHON  = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none/lib/libpython3.13.dylib";
    const string UV_PYTHONHOME = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none";

    // CPython minor version — single source for everything that depends on it
    // (the venv "lib/<PY_TAG>" dir, libpython filename). Production patch pin is
    // 3.13.11 win_amd64 (ADR-0001 d7); the minor (3.13) is what's burned into paths.
    const string PY_MINOR = "3.13";
    const string PY_TAG   = "python" + PY_MINOR;

    static bool   _resolved;
    static string _libPython;
    static string _pythonHome;
    static string _venvSite;
    static string _projectRoot;

    public static string LibPython   { get { EnsureResolved(); return _libPython;   } }
    public static string PythonHome  { get { EnsureResolved(); return _pythonHome;  } }
    public static string VenvSite    { get { EnsureResolved(); return _venvSite;    } }
    public static string ProjectRoot { get { EnsureResolved(); return _projectRoot; } }

    // Performs the pre-Initialize pythonnet wiring the probes used to do inline:
    // sets the libpython to dlopen, PYTHONHOME (env + PythonEngine), PYTHONPATH
    // (venv site-packages + project root), and the Windows DLL search path. MUST be
    // called on the Unity MAIN thread BEFORE PythonEngine.Initialize().
    // (#122 removed the login subprocess and its TTWR_PYTHON_BIN resolver, so no
    // Python reader of that env remains and it is no longer set here.)
    public static void ConfigureBeforeInitialize()
    {
        EnsureResolved();

        Python.Runtime.Runtime.PythonDLL = _libPython;
        Environment.SetEnvironmentVariable("PYTHONHOME", _pythonHome);
        Environment.SetEnvironmentVariable("PYTHONPATH", _venvSite + Path.PathSeparator + _projectRoot);
        // REGRESSION GATE (#134) — DO NOT DELETE THIS LINE.
        // Prevent matplotlib from loading GUI backends (like TkAgg) which can cause Tcl_Panic on background threads.
        // Without MPLBACKEND=Agg, matplotlib auto-resolves an interactive backend (TkAgg → tkinter/Tcl). If anything
        // imports matplotlib on a background thread — e.g. PickerInstrumentFetch → InvokeListInstruments running on a
        // worker — Tcl is touched off its creating thread and Tcl_Panic crashes Unity (native, hard to catch in AFK).
        // Gate: python/tests/test_mplbackend_agg_gate.py (scenario MPLBACKEND-01); removing this env makes that pytest
        // RED. Companion fix: #133 (login-dialog tkinter teardown). See docs/findings/0107.
        Environment.SetEnvironmentVariable("MPLBACKEND", "Agg");
        PythonEngine.PythonHome = _pythonHome;

        // Windows: uv-bundled vcruntime140.dll / msvcp140.dll live next to python313.dll
        // under PythonHome. Without expanding the loader search path, transitive .pyd
        // dependencies (pyarrow / duckdb / marimo / pydantic_core, etc.) that need the
        // VC runtime fall back to System32 — and a fresh Win10/11 LTSC or corporate-
        // locked machine may not have VC++ Redistributable pre-installed.
        //
        // Player and Editor use different Win32 APIs to minimize blast radius:
        //   * Player: SetDefaultDllDirectories + AddDllDirectory (modern, secure default —
        //     disables PATH-based DLL search process-wide, which is desirable for a
        //     shipped self-contained app where any PATH reliance would be a hidden host
        //     dependency).
        //   * Editor: SetDllDirectory (legacy single-dir API — inserts cpython/ before
        //     System32 in the search path but PRESERVES PATH search). The Editor hosts
        //     arbitrary third-party plugins (analytics SDKs, profilers, VCS integrations)
        //     whose lazy DLL loads may rely on PATH; the modern API's process-wide PATH
        //     disable would silently break them. SetDllDirectory adds what we need
        //     without subtracting what others might.
        if (Application.platform == RuntimePlatform.WindowsPlayer)
            AddWindowsDllSearchDir_Player(_pythonHome);
        else if (Application.platform == RuntimePlatform.WindowsEditor)
            AddWindowsDllSearchDir_Editor(_pythonHome);
    }

    static void EnsureResolved()
    {
        if (_resolved) return;

        if (Application.isEditor)
        {
            // <repo>/Assets -> <repo> -> <repo>/python (import root). Derived from
            // project layout so moving the repo cannot stale dev paths.
            string repoRoot = Directory.GetParent(Application.dataPath).FullName;
            _projectRoot    = Path.Combine(repoRoot, "python");
            string venvRoot = Path.Combine(_projectRoot, ".venv");

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows dev: uv CPython is per-user (AppData\Roaming\uv\python\...).
                // pyvenv.cfg `home=` records it portably across machines.
                _pythonHome = ResolveVenvHome(venvRoot);
                _libPython  = Path.Combine(_pythonHome, "python" + PY_MINOR.Replace(".", "") + ".dll");
                _venvSite   = Path.Combine(venvRoot, "Lib", "site-packages");
            }
            else
            {
                // macOS dev: external uv install carried as documented absolute consts.
                _libPython  = UV_LIBPYTHON;
                _pythonHome = UV_PYTHONHOME;
                _venvSite   = Path.Combine(venvRoot, "lib", PY_TAG, "site-packages");
            }
        }
        else
        {
            // Deploy (#33): post-process copies cpython/ + python/{engine, .venv} under
            // StreamingAssets/PythonRuntime/. Layout is written by
            // BackcastShippableBuild.PostProcess and verified by the manifest assert below.
            string baseDir = Path.Combine(Application.streamingAssetsPath, "PythonRuntime");
            _projectRoot   = Path.Combine(baseDir, "python");
            _pythonHome    = Path.Combine(baseDir, "cpython");

            if (Application.platform == RuntimePlatform.WindowsPlayer)
            {
                _libPython = Path.Combine(_pythonHome, "python" + PY_MINOR.Replace(".", "") + ".dll");
                _venvSite  = Path.Combine(_projectRoot, ".venv", "Lib", "site-packages");
            }
            else
            {
                // macOS Player (code path exists for symmetry; verification gate is
                // Windows-only per #33 grill — cutover #5 deploy target is Windows).
                _libPython = Path.Combine(_pythonHome, "lib", "libpython" + PY_MINOR + ".dylib");
                _venvSite  = Path.Combine(_projectRoot, ".venv", "lib", PY_TAG, "site-packages");
            }

            AssertDeployBundleIntact(baseDir);
        }

        _resolved = true;
    }

    // Reads the venv's pyvenv.cfg `home = <uv cpython root>` line — the venv's own
    // portable record of its base interpreter. Used by:
    //   * EnsureResolved (Editor Windows) to derive the per-user uv CPython at runtime
    //   * BackcastShippableBuild post-process to locate the CPython to bundle
    // Public so the post-process script can share the parser (single source of truth
    // for `home=` semantics; drift between the two would silently disagree on which
    // CPython is bundled vs which one is loaded).
    public static string ResolveVenvHome(string venvRoot)
    {
        string cfg = Path.Combine(venvRoot, "pyvenv.cfg");
        if (File.Exists(cfg))
        {
            foreach (string line in File.ReadAllLines(cfg))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim() == "home")
                    return line.Substring(eq + 1).Trim();
            }
        }
        throw new InvalidOperationException(
            "PythonRuntimeLocator: could not resolve uv CPython home from " + cfg +
            " (missing `home=`). Stage the venv (python/.venv) before running.");
    }

    // Hard-fails loudly when the deploy bundle is incomplete (post-process bug, mis-
    // staged install, antivirus quarantine). An asset gap here would otherwise surface
    // as an opaque dlopen / ImportError deep inside PythonEngine.Initialize.
    static void AssertDeployBundleIntact(string baseDir)
    {
        string manifest = Path.Combine(baseDir, "runtime-manifest.json");
        if (!File.Exists(manifest))
            throw new InvalidOperationException(
                "PythonRuntime asset incomplete: missing " + manifest +
                ". The build post-process did not run, or the StreamingAssets bundle is corrupt. " +
                "Rebuild via Tools > Backcast > Build Shippable (Windows64).");

        foreach (string p in new[] { _libPython, _pythonHome, _venvSite, _projectRoot })
        {
            if (!File.Exists(p) && !Directory.Exists(p))
                throw new InvalidOperationException(
                    "PythonRuntime asset incomplete: missing " + p +
                    ". Rebuild via Tools > Backcast > Build Shippable (Windows64).");
        }
    }

    // Windows loader hygiene — let .pyd transitive deps see uv-bundled vcruntime140.dll
    // (sibling to python313.dll under cpython/). Without this, the Locator becomes
    // dependent on a system-wide VC++ Redistributable install, which fresh Win10 LTSC
    // and locked-down corporate machines may lack.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectory(string lpPathName);

    const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    const uint LOAD_LIBRARY_SEARCH_USER_DIRS    = 0x00000400;

    // Player: modern AddDllDirectory + SetDefaultDllDirectories. Process-wide PATH
    // disable is the recommended secure default for a shipped self-contained app.
    static void AddWindowsDllSearchDir_Player(string dir)
    {
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS))
            throw new InvalidOperationException(
                "SetDefaultDllDirectories failed (LastError=" + Marshal.GetLastWin32Error() + ")");

        IntPtr cookie = AddDllDirectory(dir);
        if (cookie == IntPtr.Zero)
            throw new InvalidOperationException(
                "AddDllDirectory failed for " + dir +
                " (LastError=" + Marshal.GetLastWin32Error() + ")");
    }

    // Editor: legacy SetDllDirectory inserts `dir` BEFORE System32 in the standard
    // search path but PRESERVES PATH-based DLL search — so third-party editor plugins
    // that lazily LoadLibrary from a PATH-located DLL keep working. Single-dir limit
    // is fine: cpython/ is the only directory we need to add.
    static void AddWindowsDllSearchDir_Editor(string dir)
    {
        if (!SetDllDirectory(dir))
            throw new InvalidOperationException(
                "SetDllDirectory failed for " + dir +
                " (LastError=" + Marshal.GetLastWin32Error() + ")");
    }
}
