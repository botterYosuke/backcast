// RegistryStrategyFileProvider.cs — issue #78 "WYSIWYR: Run reads the editor" (DURABLE tier, seam wiring)
//
// The run layer's realization of the StrategyProviderRegistry contract: "the future
// Replay/Live run layer consults the registry to find a strategy file provider by
// floating-window id" (StrategyProviderRegistry.cs header, findings 0010 §5). Run / Step /
// LiveAuto must run WHAT THE EDITOR SHOWS, not an env-default .py — so instead of capturing
// a strategy path eagerly, they hold THIS adapter, which on every call re-resolves through
// the registry to the editor's live IStrategyFileProvider (findings 0044 §2-1).
//
// LAZY by design (UnityEngine-free so the AFK gate drives it headless):
//   * Re-looks up the registry on EACH TryGetStrategyFile call, so the editor's dirty / bound
//     path / file-exists state is always re-evaluated fresh (the document's own 5-condition
//     contract does the work — this adapter adds NO new gating).
//   * Not registered yet / window torn down  -> false  -> Run blocked (the "unloaded" case).
//   * The registry intentionally does NOT pick an active/current window (that is run-UI's job),
//     so the target window id is fixed HERE. Today the editor is effectively a singleton
//     ("strategy_editor:region_001"); a future multi-editor active-pick is an additive slice (#78 out).

public sealed class RegistryStrategyFileProvider : IStrategyFileProvider
{
    readonly StrategyProviderRegistry _registry;
    readonly string _windowId;

    public RegistryStrategyFileProvider(StrategyProviderRegistry registry, string windowId)
    {
        _registry = registry;
        _windowId = windowId;
    }

    public bool TryGetStrategyFile(out string path)
    {
        path = null;
        if (_registry == null || string.IsNullOrEmpty(_windowId)) return false;
        if (!_registry.TryGet(_windowId, out var provider) || provider == null) return false;
        return provider.TryGetStrategyFile(out path);
    }
}
