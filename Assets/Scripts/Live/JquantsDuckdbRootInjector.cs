// JquantsDuckdbRootInjector.cs — #137 S4 / findings 0107 D1 (DuckDB root → os.environ seam)
//
// The single writer that pushes the app-global DuckDB root (Settings「Data」/ PlayerPrefs) into the embedded
// CPython interpreter's os.environ, so engine/paths.py:jquants_duckdb_root() — which lazy-reads
// os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT") every call — picks the UI value up on the NEXT Replay with
// NO restart (D4). Mirrors V19ReplayLiveE2ERunner.SetOsEnviron (the proven PyString/Py.GIL marshaling).
//
// D3 precedence: SetItem OVERWRITES, so the UI/PlayerPrefs value always beats the `.env` setdefault that
// engine/paths.py applied at import time (DataEngine ctor 引数 > os.environ(UI > .env)). Empty root = "no
// override" → no-op (engine keeps the `.env` value). No-op when Python isn't initialized (non-owner /
// batchmode) so the seam is safe to call from boot and from the Settings field's onEndEdit alike (D1 ①②).
//
// GIL marshaling (memory gil-marshaling-construct-pyobject-inside-py-gil): the PyString operands are built
// INSIDE the Py.GIL() scope — constructing them before acquiring the GIL SIGSEGVs pythonnet.
//
// CLEAR semantics (review #137): an EMPTY root must REVERT to the `.env` value, not strand a previously
// injected override in os.environ. The `.env` baseline (the value setdefault'd at engine.paths import, or
// unset) is captured on the FIRST Inject — which runs at boot right after InitializePython, when
// engine.paths has imported and applied its .env setdefault — and restored whenever the UI field is cleared.
// If there is no UI value AND no .env baseline, the key is left UNSET so Replay hard-errors (ADR-0006: no
// silent fallback), never keeping a stale override.

using Python.Runtime;

public static class JquantsDuckdbRootInjector
{
    public const string EnvKey = "BACKCAST_JQUANTS_DUCKDB_ROOT";

    static bool _baselineCaptured;
    static string _baseline;   // the os.environ value BEFORE any UI override (.env setdefault or null)

    public static void Inject(string root)
    {
        if (!PythonEngine.IsInitialized) return;       // non-owner / batchmode → no interpreter to write
        using (Py.GIL())
        using (var os = Py.Import("os"))
        using (var environ = os.GetAttr("environ"))
        using (var get = environ.GetAttr("get"))
        using (var k = new PyString(EnvKey))
        {
            if (!_baselineCaptured)
            {
                using (var cur = get.Invoke(k)) _baseline = cur.IsNone() ? null : cur.ToString();
                _baselineCaptured = true;
            }

            // empty UI value → fall back to the captured .env baseline (D3); else the UI value overrides it.
            string effective = string.IsNullOrEmpty(root) ? _baseline : root;
            if (effective == null)
            {
                using (var cur = get.Invoke(k)) if (!cur.IsNone()) environ.DelItem(k);  // leave UNSET (ADR-0006)
            }
            else
            {
                using (var v = new PyString(effective)) environ.SetItem(k, v);
            }
        }
    }

    // Test hook: forget the captured baseline so an AFK probe starts clean.
    public static void ResetBaselineForTests() { _baselineCaptured = false; _baseline = null; }
}
