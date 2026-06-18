// BoundStrategyFileProvider.cs — issue #16 "Strategy Editor" (DURABLE tier, seam)
//
// A minimal IStrategyFileProvider bound to a fixed .py PATH: supplyable when that file exists.
// The mainline app uses the richer editor-backed provider (RegistryStrategyFileProvider →
// StrategyDocument's 5-condition contract); this fixed-path provider remains for tests/probes that
// drive the run-gate against a known fixture .py (e.g. BackcastWorkspaceProbe). Extracted to its own
// file in #76 S6b-β-clean when the throwaway ScenarioStartupHitlHarness that originally hosted it was
// retired.

public sealed class BoundStrategyFileProvider : IStrategyFileProvider
{
    readonly string _path;
    public BoundStrategyFileProvider(string path) { _path = path; }

    public bool TryGetStrategyFile(out string path)
    {
        path = _path;
        return !string.IsNullOrEmpty(_path) && System.IO.File.Exists(_path);
    }
}
