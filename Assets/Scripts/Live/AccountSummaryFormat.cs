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
//   * Live/Replay account summary — slot ①'s hover card: 純資産 / 含み損益 / 確定損益 / 現金 (4 lines,
//                         each prefixed with its Japanese label so a hover reader knows what each number is).
//                         Live aggregates account (cash/positions→unrealized) + telemetry (realized);
//                         Replay reads the PortfolioSnapshot. ① is a NEW aggregation (no single dock
//                         panel had it), so it lives here, not in a retired Format* (findings 0126 D3).
//
// Pure (no UnityEngine) so AccountSummaryBarE2ERunner can assert the exact strings/derivation headlessly.

using System;
using System.Globalization;
using System.Text;

public static class AccountSummaryFormat
{
    // The bar's "no data yet" primary (every slot before its source snapshot arrives).
    public const string PLACEHOLDER = "—";

    // Hover-card detail BEFORE any snapshot arrives. The PRIMARY shows a bare "—", but the hover card keeps the
    // LABELED rows (純資産: — / 買付け余力: — …) so a hover ALWAYS tells the reader WHAT the slot is, even pre-run
    // (owner report 2026-06-27: a bare "—" looked empty/broken — the whole point of the labels was 「何の項目か
    // わかるように」). Once data arrives Push*AccountBar overwrites these with the real Live/Replay summaries.
    public static string EmptyDetail(int slot)
    {
        switch (slot)
        {
            case 0: return "純資産: —\n含み損益: —\n確定損益: —\n現金: —";
            case 1: return "買付け余力: —  現金: —";
            case 2: return "(建玉なし)";
            case 3: return "(注文なし)";
            default: return PLACEHOLDER;
        }
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Compact bar-primary money: thousands-grouped, no decimals (e.g. 154421.0 → "154,421"). The HOVER
    // cards keep this full-precision string (byte-identical reuse with the retired Format*/FormatReplay*).
    public static string Money(double v) => v.ToString("#,0", Inv);

    // D11 (owner 2026-06-27, findings 0126 §視覚リファインメント): the BAR PRIMARY abbreviates money with a
    // k/M/B suffix to keep the game-resource "one short number" look in the compact left-packed slots
    // (298000→"298k", 3170→"3.17k", 1234567→"1.23M"). 3 significant figures, trailing zeros trimmed.
    // |v| < 1000 stays the plain grouped integer (no suffix). This is a SEPARATE formatter from Money so the
    // hover cards keep full precision (298,000) — never fold this back into Money (it would break the byte-
    // identical hover reuse, findings 0126 D5). Counts (positions/orders) do NOT use this — they print plain.
    static readonly string[] CompactSuffix = { "k", "M", "B", "T" };
    public static string MoneyCompact(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return PLACEHOLDER;
        if (Math.Abs(v) < 1000.0) return Money(v);   // plain grouped integer (same form the hover card uses)
        int unit = 0;
        double scaled = v / 1000.0;                  // keep the sign on `scaled` so ToString renders "-" itself
        while (Math.Abs(scaled) >= 1000.0 && unit < CompactSuffix.Length - 1) { scaled /= 1000.0; unit++; }
        double mag = Math.Abs(scaled);
        string fmt = mag >= 100.0 ? "0" : mag >= 10.0 ? "0.#" : "0.##";
        // rounding guard: 999.6 with fmt "0" would render "1000k"; promote to the next unit so it reads "1M".
        if (mag >= 999.5 && unit < CompactSuffix.Length - 1) { scaled /= 1000.0; unit++; fmt = "0.##"; }
        return scaled.ToString(fmt, Inv) + CompactSuffix[unit];
    }

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
        sb.Append("純資産: ").Append(DeriveLiveEquity(a).ToString(Inv)).Append('\n');
        sb.Append("含み損益: ").Append(SumUnrealized(a).ToString(Inv)).Append('\n');
        sb.Append("確定損益: ").Append(realized).Append('\n');
        sb.Append("現金: ").Append(a.Cash.ToString(Inv));
        return sb.ToString();
    }

    // slot ① hover card (Replay): the PortfolioSnapshot carries equity / unrealized / realized directly;
    // cash ≈ buying_power in Replay (Python sends cash=bp — findings 0126 codebase 裏取り).
    public static string ReplayAccountSummary(PortfolioSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("純資産: ").Append(s.Equity.ToString(Inv)).Append('\n');
        sb.Append("含み損益: ").Append(s.UnrealizedPnl.ToString(Inv)).Append('\n');
        sb.Append("確定損益: ").Append(s.RealizedPnl.ToString(Inv)).Append('\n');
        sb.Append("現金: ").Append(s.BuyingPower.ToString(Inv));
        return sb.ToString();
    }
}
