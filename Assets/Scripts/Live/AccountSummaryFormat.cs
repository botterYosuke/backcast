// AccountSummaryFormat.cs — issues #174-178 "account summary bar" (ADR-0038 / findings 0126)
//
// The NEW deterministic formatters the account summary bar needs that the retired dock panels did
// not have:
//   * PLACEHOLDER       — the "—" shown on the bar before account/portfolio data arrives (§Decision 5).
//   * Money             — the compact bar-primary money string (thousands-grouped, no decimals); the
//                         bar keeps the game-resource "one number" look. Hover cards keep the panels'
//                         raw-double precision (those reuse the existing Format*/FormatReplay*).
//   * DeriveLiveEquity  — §Decision 4 / D8: the venue account snapshot has NO equity field, so Live
//                         equity is derived `Cash + Σ(qty × avg_price + unrealized_pnl)` (cash + MTM
//                         position value), matching ADR-0007's equity definition. Replay reads
//                         PortfolioSnapshot.Equity directly (this class is not used there).
//   * SumUnrealized     — Σ position.unrealized_pnl over the account; the SIGN drives slot ①'s green/red
//                         colour AND is the equity derivation's MTM term (kept coherent on purpose).
//   * Live/Replay account summary — slot ①'s hover card: 純資産 / 含み損益 / 確定損益 / 現金 (4 lines).
//                         Live aggregates account (cash/positions→unrealized) + telemetry (realized);
//                         Replay reads the PortfolioSnapshot. ① is a NEW aggregation (no single dock
//                         panel had it), so it lives here, not in a retired Format* (findings 0126 D3).
//
// Pure (no UnityEngine) so AccountSummaryBarE2ERunner can assert the exact strings/derivation headlessly.

using System.Globalization;
using System.Text;

public static class AccountSummaryFormat
{
    // The bar's "no data yet" primary (every slot before its source snapshot arrives).
    public const string PLACEHOLDER = "—";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Compact bar-primary money: thousands-grouped, no decimals (e.g. 154421.0 → "154,421").
    public static string Money(double v) => v.ToString("#,0", Inv);

    // §Decision 4 / D8: Live equity = Cash + Σ(qty × avg_price + unrealized_pnl). The venue account
    // snapshot carries cash/positions but NO equity, so we derive it (cash + mark-to-market position
    // value). Flat / no positions → just cash.
    public static double DeriveLiveEquity(LiveAccountEvent a)
    {
        double eq = a.Cash;
        if (a.Positions != null)
            foreach (LivePosition p in a.Positions)
                eq += p.qty * p.avg_price + p.unrealized_pnl;
        return eq;
    }

    // Σ position.unrealized_pnl (the slot ① colour sign + the equity MTM term — same source, coherent).
    public static double SumUnrealized(LiveAccountEvent a)
    {
        double u = 0.0;
        if (a.Positions != null)
            foreach (LivePosition p in a.Positions) u += p.unrealized_pnl;
        return u;
    }

    // slot ① hover card (Live): 純資産 / 含み損益 / 確定損益 / 現金. Equity + unrealized come from the
    // account (derived / Σ), realized from telemetry (the account has no realized). Realized shows the
    // placeholder until a telemetry event has arrived.
    public static string LiveAccountSummary(LivePanelViewModel vm)
    {
        if (!vm.HasAccount) return "(no account snapshot)";
        LiveAccountEvent a = vm.LatestAccount;
        string realized = vm.HasTelemetry ? vm.LatestTelemetry.RealizedPnl.ToString(Inv) : PLACEHOLDER;
        var sb = new StringBuilder();
        sb.Append("equity=").Append(DeriveLiveEquity(a).ToString(Inv)).Append('\n');
        sb.Append("unrealized=").Append(SumUnrealized(a).ToString(Inv)).Append('\n');
        sb.Append("realized=").Append(realized).Append('\n');
        sb.Append("cash=").Append(a.Cash.ToString(Inv));
        return sb.ToString();
    }

    // slot ① hover card (Replay): the PortfolioSnapshot carries equity / unrealized / realized directly;
    // cash ≈ buying_power in Replay (Python sends cash=bp — findings 0126 codebase 裏取り).
    public static string ReplayAccountSummary(PortfolioSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("equity=").Append(s.Equity.ToString(Inv)).Append('\n');
        sb.Append("unrealized=").Append(s.UnrealizedPnl.ToString(Inv)).Append('\n');
        sb.Append("realized=").Append(s.RealizedPnl.ToString(Inv)).Append('\n');
        sb.Append("cash=").Append(s.BuyingPower.ToString(Inv));
        return sb.ToString();
    }
}
