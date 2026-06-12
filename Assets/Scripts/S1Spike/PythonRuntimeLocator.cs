// PythonRuntimeLocator.cs — issue #9 S1 "Replay seam tracer" (M3 move 1)
//
// Single source of truth for the four embedded-Python runtime paths pythonnet
// needs BEFORE PythonEngine.Initialize():
//   * LibPython   -> Python.Runtime.Runtime.PythonDLL (the libpython to dlopen)
//   * PythonHome  -> PythonEngine.PythonHome + PYTHONHOME env (CPython stdlib root)
//   * VenvSite    -> venv site-packages (nautilus + backcast-engine live here)
//   * ProjectRoot -> python/ import root (so `import engine` resolves)
//
// This is the REAL abstraction that resolves the absolute-path hardcode debt S0
// deferred ("Step1 #3: relativize via StreamingAssets"). The S1 probe adopts it
// (M3 move 2); the S0 throwaway probes (S0EditorProbe / S0SpikeHarness) keep
// their own consts — we do not churn throwaways. Per ADR-0002
// (docs/adr/0002-embedded-python-runtime-placement-and-resolution.md) resolution
// lives in ONE place that branches on Application.isEditor:
//   * Editor (dev)   -> python/.venv, DERIVED from the repo layout (Application
//                       .dataPath = <repo>/Assets, so <repo>/python). The
//                       genuinely EXTERNAL uv CPython install (~/.local/share/uv)
//                       is not repo-relative, so it stays a documented const.
//   * build (deploy) -> Application.streamingAssetsPath base. AUTHORED so the
//                       abstraction + the Editor/build seam exist, but the
//                       post-process copy that PUTS the venv there and a real
//                       standalone-exe run are DEFERRED to the #2 Windows leg
//                       (ADR-0002 slice split). This slice does NOT verify the
//                       build branch — treat it as unexercised.
//
// Placed in Assets/Scripts/ (runtime / Assembly-CSharp) because the build branch
// uses Application.streamingAssetsPath, a RUNTIME API. The Editor probe
// (Assembly-CSharp-Editor) can see runtime types, so S1AdapterSmokeProbe (move 2)
// will call ConfigureBeforeInitialize() in place of its own consts.
//
// THREADING: EnsureResolved() reads Application.dataPath, which must be read on
// the Unity MAIN thread. ConfigureBeforeInitialize() (called on main, pre-
// Initialize) forces resolution + caches all four strings into static fields, so
// the later background launcher thread reads cached strings only — it never
// touches a Unity API off-main. First resolution MUST therefore happen on main.

using System;
using System.IO;
using Python.Runtime;
using UnityEngine;

public static class PythonRuntimeLocator
{
    // Genuinely external: the uv-managed CPython 3.13.13 install. Not repo-
    // relative, so these stay documented absolute consts (ADR-0002: "derive where
    // sensible; the uv cpython path is genuinely external/absolute").
    const string UV_LIBPYTHON  = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none/lib/libpython3.13.dylib";
    const string UV_PYTHONHOME = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none";

    // CPython minor version — single source for everything that depends on it
    // (the venv "lib/<PY_TAG>" dir and the build-branch libpython filename), so a
    // minor bump can't drift one of them. The uv consts above carry the full patch
    // version (3.13.13) and stay independent.
    const string PY_MINOR = "3.13";
    // venv site-packages is "<python root>/.venv/lib/<PY_TAG>/site-packages".
    const string PY_TAG = "python" + PY_MINOR;

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
    // sets the libpython to dlopen, PYTHONHOME (env + PythonEngine), and PYTHONPATH
    // (venv site-packages + project root). MUST be called on the Unity MAIN thread
    // BEFORE PythonEngine.Initialize() and before any background thread reads the
    // path properties (it forces main-thread resolution + caching).
    public static void ConfigureBeforeInitialize()
    {
        EnsureResolved();

        Python.Runtime.Runtime.PythonDLL = _libPython;
        Environment.SetEnvironmentVariable("PYTHONHOME", _pythonHome);
        Environment.SetEnvironmentVariable("PYTHONPATH", _venvSite + Path.PathSeparator + _projectRoot);
        PythonEngine.PythonHome = _pythonHome;
    }

    static void EnsureResolved()
    {
        if (_resolved) return;

        if (Application.isEditor)
        {
            // <repo>/Assets -> <repo> -> <repo>/python (the import root). Derived
            // from the project layout so moving the repo cannot stale the dev paths.
            string repoRoot = Directory.GetParent(Application.dataPath).FullName;
            _projectRoot    = Path.Combine(repoRoot, "python");
            _venvSite       = Path.Combine(_projectRoot, ".venv", "lib", PY_TAG, "site-packages");
            _libPython      = UV_LIBPYTHON;   // external uv install (documented const)
            _pythonHome     = UV_PYTHONHOME;  // external uv install (documented const)
        }
        else
        {
            // DEFERRED (ADR-0002 slice split / #2 Windows leg): a post-process build
            // hook will copy the deploy-OS CPython + venv under StreamingAssets with
            // this layout. NOT exercised by this slice — authored only so the
            // abstraction and the Editor/build seam exist.
            string baseDir = Path.Combine(Application.streamingAssetsPath, "PythonRuntime");
            _projectRoot   = Path.Combine(baseDir, "python");
            _venvSite      = Path.Combine(_projectRoot, ".venv", "lib", PY_TAG, "site-packages");
            _pythonHome    = Path.Combine(baseDir, "cpython");
            _libPython     = Path.Combine(_pythonHome, "lib", "libpython" + PY_MINOR + ".dylib");
        }

        _resolved = true;
    }
}
