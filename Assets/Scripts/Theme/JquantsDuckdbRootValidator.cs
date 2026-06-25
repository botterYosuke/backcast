// JquantsDuckdbRootValidator.cs — #137 S4 / findings 0107 D-E (per-field DuckDB root error)
//
// Pure validation for the Settings「Data」section's DuckDB root field, mirroring the Python contract in
// engine/paths.py:jquants_listed_info_path (the root must exist AND contain `listed_info.duckdb`, the
// Replay-mode instrument-universe source). Surfaced as a per-field red error (findings 0107 D-E), the same
// pattern ScenarioStartupTile uses for its field errors. Pure + file-system only (no Unity, no Python) so
// the AFK gate drives it headlessly with real temp dirs.
//
// EMPTY is NOT an error here (it means "no UI override" — engine/paths.py falls back to the `.env` value);
// the hard-error-on-unset contract (ADR-0006) lives at Replay time in the engine, not in this field.

using System.IO;

public static class JquantsDuckdbRootValidator
{
    public const string ListedInfoFile = "listed_info.duckdb";

    // null = OK (or empty = no override). A non-empty path is validated: it must be an existing directory
    // that contains listed_info.duckdb, else a human message for the red per-field error label.
    public static string Validate(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;            // unset/whitespace → no override, not an error
        if (!Directory.Exists(root)) return "folder not found: " + root;
        string listed = Path.Combine(root, ListedInfoFile);
        if (!File.Exists(listed)) return "missing " + ListedInfoFile + " in this folder";
        return null;
    }
}
