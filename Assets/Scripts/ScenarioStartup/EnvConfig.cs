// EnvConfig.cs — issue #29 HITL config resolution (per-machine paths from .env / env vars)
//
// External-storage paths (the J-Quants parquet catalog) differ per machine, so they must NOT
// be hardcoded in the harness — they come from `.env` / process env (owner 2026-06-14),
// matching the engine-side convention (engine.paths.jquants_catalog_path reads ARTIFACTS_PATH).
//
// Resolution order for a key: process env var (Environment.GetEnvironmentVariable) wins, then
// the first `.env` file found at <repo>/.env or <repo>/python/.env. `.env` is gitignored, so
// each machine keeps its own. Lines are `KEY=VALUE` (# comments and blank lines skipped;
// surrounding single/double quotes stripped). Parsed lazily once per process.

using System.Collections.Generic;
using System.IO;

public static class EnvConfig
{
    static Dictionary<string, string> _dotenv;

    // Process env wins; then .env file; then the supplied fallback.
    public static string Get(string key, string fallback = null)
    {
        string fromEnv = System.Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        EnsureLoaded();
        return _dotenv.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v) ? v : fallback;
    }

    static void EnsureLoaded()
    {
        if (_dotenv != null) return;
        _dotenv = new Dictionary<string, string>();

        // ProjectRoot = <repo>/python ; repo root = its parent.
        string projectRoot = PythonRuntimeLocator.ProjectRoot;
        string repoRoot = string.IsNullOrEmpty(projectRoot) ? null : Directory.GetParent(projectRoot)?.FullName;

        foreach (string candidate in CandidatePaths(repoRoot, projectRoot))
        {
            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
            ParseInto(candidate, _dotenv);
            // first file wins for a given key (don't let a later file override); merge missing keys only
        }
    }

    static IEnumerable<string> CandidatePaths(string repoRoot, string projectRoot)
    {
        if (!string.IsNullOrEmpty(repoRoot)) yield return Path.Combine(repoRoot, ".env");
        if (!string.IsNullOrEmpty(projectRoot)) yield return Path.Combine(projectRoot, ".env");
    }

    static void ParseInto(string path, Dictionary<string, string> into)
    {
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[val.Length - 1] == '"') || (val[0] == '\'' && val[val.Length - 1] == '\'')))
                val = val.Substring(1, val.Length - 2);
            if (!into.ContainsKey(key)) into[key] = val; // process-env precedence handled in Get; first .env wins
        }
    }
}
