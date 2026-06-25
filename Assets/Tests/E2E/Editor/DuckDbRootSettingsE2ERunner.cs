// DuckDbRootSettingsE2ERunner.cs — Surface E2E for the Settings「Data」DuckDB root (#137 S4 / findings 0107).
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod DuckDbRootSettingsE2ERunner.Run -logFile <abs>
//   # expect per-id [E2E DUCKROOT-0N PASS] + [E2E DUCKROOT PASS] / (DUCKROOT-04 boots MOCK Python, so the
//   #  process may exit=139 on pythonnet shutdown — the PASS TAG is the verdict, not the exit code: #107规約)
//   # confirm with Bash `grep -a "E2E DUCKROOT"`.
//
// Splits the C#↔Python seam (behavior-to-e2e 2-gate rule):
//   * DUCKROOT-01/02/03 are Python-FREE: the PlayerPrefs store, the pure path validator, and the Data view's
//     browse/commit wiring (StubFileDialog seam + store persistence + fail-soft cancel + red error).
//   * DUCKROOT-04 boots MOCK Python and drives the REAL injection end-to-end: host inject → os.environ →
//     engine.paths.jquants_listed_info_path() resolves the real file. This is the only leg that needs the
//     embedded interpreter; the engine read side is the production resolver, not a fake.

using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using Python.Runtime;

public static class DuckDbRootSettingsE2ERunner
{
    static readonly System.Collections.Generic.List<string> _fail = new System.Collections.Generic.List<string>();

    public static void Run()
    {
        try
        {
            Section("DUCKROOT-01", Section01_StoreRoundTrip);
            Section("DUCKROOT-02", Section02_PathValidation);
            Section("DUCKROOT-03", Section03_BrowseFolderSeamAndCommit);
            Section("DUCKROOT-04", Section04_OsEnvironInjectionMockPython);
            Section("DUCKROOT-05", Section05_StaleStoredRootFailsSoft);
        }
        finally { JquantsDuckdbRootStore.ClearForTests(); }

        if (_fail.Count == 0)
            Debug.Log("[E2E DUCKROOT PASS] store round-trip (DUCKROOT-01) / path validation (DUCKROOT-02) / " +
                      "BrowseFolder stub seam + commit + fail-soft + red error (DUCKROOT-03) / os.environ inject → " +
                      "engine.paths resolves (DUCKROOT-04) / stale stored root fails soft (DUCKROOT-05) — #137 S4 + #129, findings 0107/0104");
        else
            Debug.LogError("[E2E DUCKROOT FAIL]\n  - " + string.Join("\n  - ", _fail));

        EditorApplication.Exit(_fail.Count == 0 ? 0 : 1);
    }

    static void Section(string id, Func<string> body)
    {
        string err;
        try { err = body(); }
        catch (Exception e) { err = "exception: " + e; }
        if (err == null) Debug.Log("[E2E " + id + " PASS]");
        else { _fail.Add(id + ": " + err); Debug.LogError("[E2E " + id + " FAIL] " + err); }
    }

    // DUCKROOT-01: the app-global PlayerPrefs store round-trips, treats empty as "clear", and ClearForTests
    // wipes residue. RED litmus: persist to a sidecar / per-run field instead → Load() after a fresh process
    // wouldn't carry the value (non-portable — the contract D2 forbids).
    static string Section01_StoreRoundTrip()
    {
        JquantsDuckdbRootStore.ClearForTests();
        if (JquantsDuckdbRootStore.Load() != "") return "default Load() is not empty after clear";

        JquantsDuckdbRootStore.Save("/Volumes/StockData/jp");
        if (JquantsDuckdbRootStore.Load() != "/Volumes/StockData/jp") return "Save→Load did not round-trip";

        JquantsDuckdbRootStore.Save("");                // empty = clear (no override → engine keeps .env)
        if (JquantsDuckdbRootStore.Load() != "") return "Save(empty) did not clear the key";

        JquantsDuckdbRootStore.Save("/tmp/x");
        JquantsDuckdbRootStore.ClearForTests();
        if (JquantsDuckdbRootStore.Load() != "") return "ClearForTests did not wipe the key";
        return null;
    }

    // DUCKROOT-02: the pure validator mirrors engine/paths.py:jquants_listed_info_path — empty is OK ("no
    // override"), a missing folder errors, an existing folder WITHOUT listed_info.duckdb errors, and a folder
    // WITH it passes. RED litmus: drop the listed_info.duckdb existence check → the "missing file" case passes.
    static string Section02_PathValidation()
    {
        if (JquantsDuckdbRootValidator.Validate("") != null) return "empty path flagged as error (must be OK = no override)";
        if (JquantsDuckdbRootValidator.Validate(null) != null) return "null path flagged as error";

        string missing = Path.Combine(Path.GetTempPath(), "backcast_duckroot_missing_" + Guid.NewGuid().ToString("N"));
        if (JquantsDuckdbRootValidator.Validate(missing) == null) return "nonexistent folder not flagged";

        string dir = Path.Combine(Path.GetTempPath(), "backcast_duckroot_val_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (JquantsDuckdbRootValidator.Validate(dir) == null)
                return "folder without listed_info.duckdb not flagged";
            File.WriteAllText(Path.Combine(dir, JquantsDuckdbRootValidator.ListedInfoFile), "");
            if (JquantsDuckdbRootValidator.Validate(dir) != null)
                return "valid folder (with listed_info.duckdb) wrongly flagged";
            return null;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // DUCKROOT-03: the Data view's [...] browse button routes the StubFileDialog folder into the field + the
    // store + the host onCommit, the data field carries the S1 border/placeholder, a cancel (null) is
    // fail-soft (field untouched), and a bad folder lights the red error. RED litmus: unwire the browse
    // button (onClick → nothing) → the field/store stay empty after the click.
    static string Section03_BrowseFolderSeamAndCommit()
    {
        JquantsDuckdbRootStore.ClearForTests();
        var go = new GameObject("duckdb_data_view_e2e", typeof(RectTransform));
        try
        {
            var c = ThemeService.Current.colors;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // a real folder (with listed_info.duckdb) so the committed value is valid (no red error).
            string dir = Path.Combine(Path.GetTempPath(), "backcast_duckroot_browse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, JquantsDuckdbRootValidator.ListedInfoFile), "");
            try
            {
                var stub = new StubFileDialog { NextFolderResult = dir };
                string committed = null;
                var view = new SettingsDataSectionView(
                    initial => stub.BrowseFolder("Select J-Quants DuckDB folder", initial),
                    r => committed = r,
                    font);
                view.Build((RectTransform)go.transform);

                var field = FindInput(go, "duckdb_field");
                if (field == null) return "duckdb_field InputField missing";

                // S1 for the data field: border + placeholder.
                var border = field.GetComponent<Outline>();
                if (border == null || !SameColor(border.effectColor, c.border)) return "data field border not the `border` role";
                var ph = field.placeholder as Text;
                if (ph == null || string.IsNullOrEmpty(ph.text)) return "data field has no non-empty placeholder";

                // click [...] → folder routed into field + store + onCommit; the stub recorded the title.
                var btn = FindButton(go, "btn_browse");
                if (btn == null) return "btn_browse missing";
                btn.onClick.Invoke();

                if (field.text != dir) return "browse did not set the field to the picked folder";
                if (JquantsDuckdbRootStore.Load() != dir) return "browse did not persist the folder to the store";
                if (committed != dir) return "browse did not invoke onCommit(folder) (os.environ would not be injected)";
                if (stub.LastBrowseTitle == null) return "BrowseFolder was not called through the dialog seam";

                // valid folder → no red error.
                var err = FindText(go, "duckdb_err");
                if (err == null) return "duckdb_err label missing";
                if (err.enabled) return "valid folder lit the red error";

                // cancel (null) is fail-soft: the field keeps its value.
                stub.NextFolderResult = null;
                btn.onClick.Invoke();
                if (field.text != dir) return "cancel (null) clobbered the field (must be fail-soft)";

                // a bad folder typed + committed lights the red error and still persists. Faithful to Unity:
                // onEndEdit fires with field.text as its arg, so set the field FIRST (SetTextWithoutNotify so
                // the no-onValueChanged data field is not double-driven), then fire onEndEdit(field.text).
                string bad = Path.Combine(Path.GetTempPath(), "backcast_duckroot_bad_" + Guid.NewGuid().ToString("N"));
                field.SetTextWithoutNotify(bad);
                field.onEndEdit.Invoke(field.text);
                if (!err.enabled) return "nonexistent folder did not light the red error";
                if (JquantsDuckdbRootStore.Load() != bad) return "onEndEdit did not persist the typed value";
                return null;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        finally { UnityEngine.Object.DestroyImmediate(go); JquantsDuckdbRootStore.ClearForTests(); }
    }

    // DUCKROOT-04: the real os.environ injection. Boot MOCK Python, inject a root, and prove the embedded
    // interpreter sees it AND the production resolver engine.paths.jquants_listed_info_path() resolves the
    // real file under it (D1/D4 end-to-end). RED litmus: make Inject a no-op → os.environ readback ≠ root.
    static string Section04_OsEnvironInjectionMockPython()
    {
        string dir = Path.Combine(Path.GetTempPath(), "backcast_duckroot_inject_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, JquantsDuckdbRootValidator.ListedInfoFile), "");
        WorkspaceEngineHost host = null;
        try
        {
            host = new WorkspaceEngineHost();
            host.InitializePython("MOCK");
            if (!PythonEngine.IsInitialized) return "PythonEngine not initialized after InitializePython(MOCK)";

            // empty root is a no-op (must not write os.environ).
            JquantsDuckdbRootInjector.Inject("");
            JquantsDuckdbRootInjector.Inject(dir);                 // the real injection (D1)

            string readback = ReadEnviron(JquantsDuckdbRootInjector.EnvKey);
            if (readback != dir) return $"os.environ readback '{readback}' ≠ injected root '{dir}'";

            // the production resolver now sees the file under the injected root (no restart — D4).
            if (!ListedInfoResolves()) return "engine.paths.jquants_listed_info_path() did not resolve under the injected root";
            return null;
        }
        finally
        {
            try { host?.Stop(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // DUCKROOT-05 (#129 regression — the bug the Python-FREE ChartSpawnPreviewE2ERunner could not catch):
    // a stale/dead STORED root must NOT poison os.environ. The owner's PlayerPrefs held a leaked test temp
    // path (backcast_duckroot_bad_…) that no longer existed; Inject blindly wrote it into os.environ, so the
    // engine resolved a nonexistent DuckDB root and EVERY read (chart preview AND Replay RUN) returned
    // NO_DATA / FileNotFoundError. The injector now validates the stored root and reverts to the .env
    // baseline when it doesn't resolve. RED litmus: drop the Validate guard in
    // JquantsDuckdbRootInjector.Inject → os.environ readback == the dead path → RED.
    static string Section05_StaleStoredRootFailsSoft()
    {
        string good = Path.Combine(Path.GetTempPath(), "backcast_duckroot_good_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, JquantsDuckdbRootValidator.ListedInfoFile), "");
        string dead = Path.Combine(Path.GetTempPath(), "backcast_duckroot_dead_" + Guid.NewGuid().ToString("N")); // never created
        WorkspaceEngineHost host = null;
        try
        {
            host = new WorkspaceEngineHost();
            host.InitializePython("MOCK");
            if (!PythonEngine.IsInitialized) return "PythonEngine not initialized after InitializePython(MOCK)";

            // seed a VALID .env-style baseline, then let the FIRST Inject capture it as the baseline.
            JquantsDuckdbRootInjector.ResetBaselineForTests();
            SetEnviron(JquantsDuckdbRootInjector.EnvKey, good);
            JquantsDuckdbRootInjector.Inject("");        // first call captures baseline=good, no override
            if (ReadEnviron(JquantsDuckdbRootInjector.EnvKey) != good) return "baseline seed lost before stale inject";

            // a dead STORED root (folder doesn't exist) must be rejected → revert to the good baseline.
            JquantsDuckdbRootInjector.Inject(dead);
            string readback = ReadEnviron(JquantsDuckdbRootInjector.EnvKey);
            if (readback == dead) return $"stale stored root POISONED os.environ (got dead path '{dead}') — the #129 bug";
            if (readback != good) return $"stale stored root did not revert to baseline: got '{readback}' (expected '{good}')";
            return null;
        }
        finally
        {
            try { host?.Stop(); } catch { }
            try { JquantsDuckdbRootInjector.ResetBaselineForTests(); } catch { }
            try { Directory.Delete(good, true); } catch { }
        }
    }

    // ── Python read helpers (mirror V19 SetOsEnviron's Py.GIL/PyString marshaling) ──
    static void SetEnviron(string key, string val)
    {
        using (Py.GIL())
        using (var os = Py.Import("os"))
        using (var environ = os.GetAttr("environ"))
        using (var k = new PyString(key))
        using (var v = new PyString(val))
            environ.SetItem(k, v);
    }

    static string ReadEnviron(string key)
    {
        using (Py.GIL())
        using (var os = Py.Import("os"))
        using (var environ = os.GetAttr("environ"))
        using (var get = environ.GetAttr("get"))
        using (var k = new PyString(key))
        using (var v = get.Invoke(k))
            return v.IsNone() ? null : v.ToString();
    }

    static bool ListedInfoResolves()
    {
        using (Py.GIL())
        using (var paths = Py.Import("engine.paths"))
        using (var fn = paths.GetAttr("jquants_listed_info_path"))
        using (var res = fn.Invoke())
            return !res.IsNone();
    }

    // ── uGUI find helpers ──
    static InputField FindInput(GameObject root, string name)
    {
        foreach (var f in root.GetComponentsInChildren<InputField>(true)) if (f.gameObject.name == name) return f;
        return null;
    }
    static Button FindButton(GameObject root, string name)
    {
        foreach (var b in root.GetComponentsInChildren<Button>(true)) if (b.gameObject.name == name) return b;
        return null;
    }
    static Text FindText(GameObject root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Text>(true)) if (t.gameObject.name == name) return t;
        return null;
    }
    static bool SameColor(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.003f && Mathf.Abs(a.g - b.g) < 0.003f && Mathf.Abs(a.b - b.b) < 0.003f;
}
