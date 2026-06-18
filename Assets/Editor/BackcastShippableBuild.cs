// BackcastShippableBuild.cs — issue #33 "Shippable standalone build packaging"
//
// Owns the build post-process that bundles the embedded Python runtime so the shipped
// exe can run without a system Python install. Design fixed by grill-with-docs
// (2026-06-18); see docs/findings/0049-shippable-standalone-bundled-venv.md.
//
// What it copies into <exe>_Data/StreamingAssets/PythonRuntime/:
//
//   cpython/                              ← uv-managed CPython root, verbatim copy
//     python.exe                          ← subprocess resolver step 1/4 + TTWR_PYTHON_BIN
//     python313.dll                       ← Locator binds as PythonDLL
//     vcruntime140.dll / msvcp140.dll     ← uv-bundled (Locator's AddDllDirectory makes them
//                                            reachable to transitive .pyd loads → no VC++ Redist
//                                            precondition; #33 grill)
//     DLLs/, Lib/, ...
//   python/
//     engine/                             ← import root for `import engine`
//     .venv/Lib/site-packages/            ← duckdb / marimo / pyarrow / scikit-learn / pandas /
//                                            numpy / pydantic / httpx / websockets / joblib + transitive
//                                            (pyvenv.cfg is DELETED here; Locator is single SoT for
//                                            PYTHONHOME in deploy)
//   runtime-manifest.json                 ← {cpython_version, built_at, asset paths}
//                                            sanity-checked by PythonRuntimeLocator at startup
//
// Why each step (don't be tempted to remove):
//   * pyvenv.cfg absence: this post-process copies ONLY <venv>/Lib/site-packages, not the
//     venv root, so the venv root's pyvenv.cfg never appears in the bundle. The explicit
//     File.Delete is a defensive net for a future change that ever does copy the full venv
//     (a dev-machine pyvenv.cfg `home=` would point at the per-user uv install directory
//     and conflict with the Locator's explicit PYTHONHOME on subprocess-spawned python.exe
//     invocations). Today the delete is a no-op; tomorrow it's the guardrail.
//   * compileall --invalidation-mode unchecked-hash: post-process file copies reset .py
//     mtime → timestamp-validated .pyc are treated as stale → every cold start re-compiles
//     the entire venv (~marimo/scikit-learn are huge). unchecked-hash makes .pyc usable as
//     long as the .py hash matches, which is the right semantic for an immutable deploy
//     artifact. Building both -o 0 and -o 1 covers CPython invocation flag variance.
//   * No -O 2: it strips __doc__ and conservative libs (nautilus inspect-style) can break.
//   * runtime-manifest.json: lets Locator fail loudly on asset gaps with a single
//     actionable error instead of an opaque deep ImportError.
//
// Not in scope (#33 grill):
//   * python/strategies, python/spike, python/tests — strategies authored in-app, spike
//     is throwaway, tests run in CI.
//   * DuckDB market data root (/Volumes/StockData/...) — env BACKCAST_JQUANTS_DUCKDB_ROOT.
//   * VC++ Redistributable installer — uv ships the DLLs; Locator's AddDllDirectory wires them.
//   * Job Object for login subprocess hygiene → #82.
//   * Mac standalone verification gate — code paths exist for symmetry, Windows is the
//     deploy target (cutover #5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class BackcastShippableBuild : IPostprocessBuildWithReport
{
    // Run late so other post-processors (if any) finish first.
    public int callbackOrder => 100;

    public void OnPostprocessBuild(BuildReport report)
    {
        var target = report.summary.platform;
        if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneOSX)
        {
            UnityEngine.Debug.Log($"[BackcastShippableBuild] skipped (target={target}): only Standalone Win64 / OSX bundle the Python runtime.");
            return;
        }

        string outputPath = report.summary.outputPath;
        if (string.IsNullOrEmpty(outputPath))
            throw new BuildFailedException("[BackcastShippableBuild] BuildReport.summary.outputPath is empty.");

        string streamingAssets = ResolveStreamingAssetsPath(outputPath, target);
        string runtimeRoot = Path.Combine(streamingAssets, "PythonRuntime");

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);
        Directory.CreateDirectory(runtimeRoot);

        string repoRoot   = Directory.GetParent(Application.dataPath).FullName;
        string pythonRoot = Path.Combine(repoRoot, "python");
        string venvRoot   = Path.Combine(pythonRoot, ".venv");

        if (!Directory.Exists(venvRoot))
            throw new BuildFailedException($"[BackcastShippableBuild] python/.venv not found at {venvRoot}. " +
                "Stage the deploy-OS venv before building (uv sync in python/).");

        // Reuse the Locator's parser so the post-process and runtime agree on `home=`
        // semantics (review finding: drift between two parsers would silently disagree
        // on which CPython is bundled vs which one the Locator resolves at runtime).
        string cpythonSrc = PythonRuntimeLocator.ResolveVenvHome(venvRoot);
        if (!Directory.Exists(cpythonSrc))
            throw new BuildFailedException($"[BackcastShippableBuild] uv CPython home {cpythonSrc} " +
                $"(from {Path.Combine(venvRoot, "pyvenv.cfg")} `home=`) does not exist.");

        UnityEngine.Debug.Log($"[BackcastShippableBuild] bundling Python runtime → {runtimeRoot}");

        // 1. uv CPython root → cpython/
        string cpythonDst = Path.Combine(runtimeRoot, "cpython");
        CopyDirectory(cpythonSrc, cpythonDst, excludeNames: PycacheNames);
        UnityEngine.Debug.Log($"[BackcastShippableBuild]   cpython ← {cpythonSrc}");

        // 2. python/engine → python/engine
        string engineSrc = Path.Combine(pythonRoot, "engine");
        string engineDst = Path.Combine(runtimeRoot, "python", "engine");
        CopyDirectory(engineSrc, engineDst, excludeNames: PycacheNames);
        UnityEngine.Debug.Log($"[BackcastShippableBuild]   python/engine ← {engineSrc}");

        // 3. python/.venv/{Lib,lib}/site-packages → python/.venv/.../site-packages
        //    (Windows venvs use Lib/site-packages, macOS uses lib/python3.13/site-packages.)
        string sitePackagesSrc;
        string sitePackagesDst;
        if (target == BuildTarget.StandaloneWindows64)
        {
            sitePackagesSrc = Path.Combine(venvRoot, "Lib", "site-packages");
            sitePackagesDst = Path.Combine(runtimeRoot, "python", ".venv", "Lib", "site-packages");
        }
        else
        {
            sitePackagesSrc = Path.Combine(venvRoot, "lib", "python3.13", "site-packages");
            sitePackagesDst = Path.Combine(runtimeRoot, "python", ".venv", "lib", "python3.13", "site-packages");
        }
        if (!Directory.Exists(sitePackagesSrc))
            throw new BuildFailedException($"[BackcastShippableBuild] venv site-packages not found at {sitePackagesSrc}.");
        CopyDirectory(sitePackagesSrc, sitePackagesDst, excludeNames: PycacheNames);
        UnityEngine.Debug.Log($"[BackcastShippableBuild]   site-packages ← {sitePackagesSrc}");

        // 4. pyvenv.cfg DELETE in the copied venv (Locator is single SoT for PYTHONHOME
        //    in deploy; leaving the dev pyvenv.cfg here points at the per-user uv install
        //    directory and conflicts with the Locator on subprocess python.exe invocations).
        //    The source venv on disk is untouched — we only delete from the copy.
        string copiedPyvenvCfg = Path.Combine(runtimeRoot, "python", ".venv", "pyvenv.cfg");
        if (File.Exists(copiedPyvenvCfg))
        {
            File.Delete(copiedPyvenvCfg);
            UnityEngine.Debug.Log($"[BackcastShippableBuild]   deleted (deploy) pyvenv.cfg");
        }

        // 5. compileall both trees with hash-based invalidation (post-process resets .py
        //    mtime → timestamp validation would invalidate every .pyc on first run).
        string pyExe = Path.Combine(cpythonDst,
            target == BuildTarget.StandaloneWindows64 ? "python.exe" : "bin/python3");
        RunCompileall(pyExe, sitePackagesDst);
        RunCompileall(pyExe, Path.Combine(runtimeRoot, "python"));

        // 6. runtime-manifest.json — Locator reads this at startup to fail loudly on
        //    asset gaps. cpython_version is derived by invoking the bundled python.exe
        //    (no assumption about which pinned 3.13.x the dev machine happens to have).
        string cpythonVersion = QueryPythonVersion(pyExe);
        WriteManifest(runtimeRoot, target, cpythonVersion);

        UnityEngine.Debug.Log($"[BackcastShippableBuild] done. python={cpythonVersion} → {runtimeRoot}");
    }

    // ---- CLI / menu entry points -------------------------------------------------

    [MenuItem("Tools/Backcast/Build Shippable (Windows64)")]
    public static void BuildWindows64()
    {
        BuildShippable(BuildTarget.StandaloneWindows64);
    }

    [MenuItem("Tools/Backcast/Build Shippable (macOS)")]
    public static void BuildOSX()
    {
        BuildShippable(BuildTarget.StandaloneOSX);
    }

    static void BuildShippable(BuildTarget target)
    {
        string buildRoot = Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "build",
            target == BuildTarget.StandaloneWindows64 ? "windows64" : "osx");
        Directory.CreateDirectory(buildRoot);

        string exePath = Path.Combine(buildRoot,
            target == BuildTarget.StandaloneWindows64 ? "backcast.exe" : "backcast.app");

        var scenes = EditorBuildSettings.scenes;
        var enabledScenes = new List<string>();
        foreach (var s in scenes) if (s.enabled) enabledScenes.Add(s.path);
        if (enabledScenes.Count == 0)
            throw new BuildFailedException(
                "[BackcastShippableBuild] EditorBuildSettings has no enabled scenes. " +
                "Run Tools > Backcast > Build Workspace Scene first.");

        var options = new BuildPlayerOptions {
            scenes           = enabledScenes.ToArray(),
            locationPathName = exePath,
            target           = target,
            options          = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"[BackcastShippableBuild] BuildPlayer result={report.summary.result}");

        UnityEngine.Debug.Log($"[BackcastShippableBuild] built {exePath}");
    }

    // ---- internals --------------------------------------------------------------

    // Pycache exclusion: cpython stdlib + site-packages ship a lot of pre-existing
    // __pycache__ from the dev machine; we drop those and let compileall recreate them
    // with deploy-stable hash-based invalidation. Without the exclusion we'd ship
    // mixed timestamp/hash .pyc + duplicate entries.
    static readonly string[] PycacheNames = { "__pycache__" };

    static string ResolveStreamingAssetsPath(string outputPath, BuildTarget target)
    {
        if (target == BuildTarget.StandaloneOSX)
        {
            // <something>.app/Contents/Resources/Data/StreamingAssets
            return Path.Combine(outputPath, "Contents", "Resources", "Data", "StreamingAssets");
        }
        // Windows: <exe-dir>/<exe-stem>_Data/StreamingAssets
        string exeDir  = Path.GetDirectoryName(outputPath);
        string exeStem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(exeDir, exeStem + "_Data", "StreamingAssets");
    }

    static void CopyDirectory(string src, string dst, string[] excludeNames)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = dir.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsExcluded(rel, excludeNames)) continue;
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsExcluded(rel, excludeNames)) continue;
            string dstFile = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile));
            File.Copy(file, dstFile, overwrite: true);
        }
    }

    static bool IsExcluded(string relPath, string[] excludeNames)
    {
        if (excludeNames == null) return false;
        foreach (string part in relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            foreach (string ex in excludeNames)
                if (part == ex) return true;
        return false;
    }

    static void RunCompileall(string pyExe, string tree)
    {
        // unchecked-hash: deploy is immutable, source hash is the right invalidation key.
        // -o 0 -o 1: both flag levels (we don't know how the embedded interpreter will be
        //   invoked at runtime). -o 2 strips __doc__ and breaks libs that introspect docstrings.
        // -j 0: all cores. -q: silence per-file logging (we surface our own summary).
        string args = "-m compileall --invalidation-mode unchecked-hash -o 0 -o 1 -j 0 -q " + Quote(tree);
        int exit = RunProcess(pyExe, args, out string stdout, out string stderr);
        if (exit != 0)
            throw new BuildFailedException(
                $"[BackcastShippableBuild] compileall failed (exit={exit}) on {tree}\n--- stderr ---\n{stderr}\n--- stdout ---\n{stdout}");
        UnityEngine.Debug.Log($"[BackcastShippableBuild]   compileall ✓ {tree}");
    }

    static string QueryPythonVersion(string pyExe)
    {
        int exit = RunProcess(pyExe, "-c \"import sys; print(sys.version.split()[0])\"", out string stdout, out string stderr);
        if (exit != 0)
            throw new BuildFailedException(
                $"[BackcastShippableBuild] {pyExe} -c failed (exit={exit})\n--- stderr ---\n{stderr}");
        return stdout.Trim();
    }

    // Runs a child process and drains BOTH stdout and stderr concurrently. Reading only
    // one pipe while the child writes both will deadlock once the unread pipe's OS buffer
    // fills (~4KB on Windows): the child blocks in WriteFile, WaitForExit blocks on the
    // child, and the Editor freezes with no diagnostic. Use BeginOutputReadLine /
    // BeginErrorReadLine to drain both streams on background threads.
    static int RunProcess(string fileName, string arguments, out string stdout, out string stderr)
    {
        var psi = new ProcessStartInfo {
            FileName               = fileName,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        stdout = stdoutSb.ToString();
        stderr = stderrSb.ToString();
        return proc.ExitCode;
    }

    // Quote a path argument for Windows CreateProcess. Doubles internal quotes and wraps
    // in quotes if the path contains spaces — sufficient for paths we control (no
    // arguments containing backslash-quote sequences here).
    static string Quote(string s)
    {
        if (!s.Contains(' ') && !s.Contains('\t')) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    static void WriteManifest(string runtimeRoot, BuildTarget target, string cpythonVersion)
    {
        // Use Newtonsoft.Json (com.unity.nuget.newtonsoft-json, already in Packages/
        // manifest.json and used by LayoutStore / ScenarioSidecarStore). Hand-built JSON
        // would silently mishandle control chars in cpython_version (CPython embeds
        // \r\n in sys.version on some Windows patch builds) and force every reader to
        // hand-parse — Newtonsoft gives proper escaping for free.
        var doc = new {
            schema          = 1,
            issue           = 33,
            target          = target.ToString(),
            cpython_version = cpythonVersion,
            built_at        = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            paths           = new {
                cpython            = "cpython",
                project_root       = "python",
                venv_site_relative = target == BuildTarget.StandaloneWindows64
                    ? "python/.venv/Lib/site-packages"
                    : "python/.venv/lib/python3.13/site-packages",
            },
        };

        string manifest = Path.Combine(runtimeRoot, "runtime-manifest.json");
        File.WriteAllText(manifest, JsonConvert.SerializeObject(doc, Formatting.Indented));
        UnityEngine.Debug.Log($"[BackcastShippableBuild]   manifest → {manifest}");
    }
}
