// JquantsDuckdbRootStore.cs — #137 S4 / findings 0107 D2 (DuckDB root persistence)
//
// The app-GLOBAL persisted J-Quants DuckDB market-data root, moved off `.env`
// (BACKCAST_JQUANTS_DUCKDB_ROOT) into the Settings「Data」section so the owner sets it in the dialog
// without editing `.env`. Mirrors AppearanceStore: a single PlayerPrefs key, written when the Data field
// commits (onEndEdit) and read at boot to inject os.environ before the first Replay (D1).
//
// WHY app-global PlayerPrefs and NOT a scenario sidecar (findings 0107 D2 / CONTEXT
// "catalog_path（環境/配置の関心・scenario 外）"): the DuckDB root is a PER-MACHINE storage path, not a
// per-run scenario knob — baking it into a strategy sidecar would make the strategy non-portable. The
// app-global Settings/PlayerPrefs面 is exactly where the environment/placement concern belongs.
//
// The `.env` loader in engine/paths.py stays (findings 0107 D-C): pytest / headless E2E runners / hitl
// read `.env`/env directly (they can't read Unity PlayerPrefs). This store only unifies the APP UI.

using UnityEngine;

public static class JquantsDuckdbRootStore
{
    const string Key = "backcast.jquants_duckdb_root";

    // Persist the DuckDB root. Empty/null clears the key so Load() reports "unset" (→ host skips injection,
    // engine/paths.py falls back to the `.env` setdefault — D3 precedence: ctor > os.environ(UI/.env)).
    public static void Save(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) PlayerPrefs.DeleteKey(Key);
        else PlayerPrefs.SetString(Key, root);
        PlayerPrefs.Save();
    }

    // The saved root, or "" when unset (never null — callers treat "" as "no UI override").
    public static string Load() => PlayerPrefs.GetString(Key, "");

    // Test hook: drop the key so an AFK probe doesn't leave machine-global residue (AppearanceStore parity).
    // #129 regression: MUST PlayerPrefs.Save() the deletion. DUCKROOT-03 force-writes a `bad` path to disk
    // (Save→PlayerPrefs.Save()), then this cleanup ran DeleteKey WITHOUT Save — leaving the deletion in-memory
    // only. The runner's documented shutdown segfault (exit 139, findings 0107) skips Unity's normal-exit
    // flush, so the on-disk key kept the dead `backcast_duckroot_bad_…` path and poisoned the owner's live
    // app (Inject → os.environ → preview/RUN read a nonexistent root → NO_DATA). Persisting here closes that.
    public static void ClearForTests() { PlayerPrefs.DeleteKey(Key); PlayerPrefs.Save(); }
}
