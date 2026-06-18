// MarimoStrategyTemplate.cs — issue #76 S6b-β-clean U2 (DURABLE tier, pure data)
//
// The starter marimo skeleton File→New seeds into the adopted editor (findings 0046,
// "S6b-β-clean 設計の木" U2). Replaces the old empty-buffer New: a fresh workspace is now an
// immediately-runnable (once saved) reactive marimo strategy, not a blank page — owner方針
// 「完成形・仮状態なし」.
//
// Shape mirrors docs/samples/code/00_observe.py (observe-only, no orders): `import marimo` +
// `app = marimo.App()` at module level so engine.strategy_runtime.strategy_kind.is_marimo_app_source
// is true, and ONE @app.cell that reads the host-seeded get_bar() driver — the minimal valid marimo
// strategy (thin_drain's empty-roots reject passes because a driver is read). Minimal comments only
// name the host seams get_bar() / get_portfolio() / submit_market() (#64 "editor = cell, 最小 ceremony").
//
// A PLAIN C# constant so the AFK probe (MenuBarCutoverProbe Section1) can assert the seeded text
// without a Python round-trip.

public static class MarimoStrategyTemplate
{
    // NOTE: '\n' line endings (UTF-8, no CR) match how the editor/document model stores buffers.
    public const string NewStrategy =
        "import marimo\n" +
        "\n" +
        "app = marimo.App()\n" +
        "\n" +
        "\n" +
        "@app.cell\n" +
        "def _observe():\n" +
        "    # 最小の戦略: バーを読むだけ・発注なし（観察専用）。\n" +
        "    # get_bar()=現在のバー / get_portfolio()=建玉・現金 / submit_market(qty, instrument_id=)=発注。\n" +
        "    bar = get_bar()  # noqa: F821  host が注入する driver\n" +
        "    note = f\"{bar.instrument_id} close={bar.close}\"\n" +
        "    return (note,)\n";
}
