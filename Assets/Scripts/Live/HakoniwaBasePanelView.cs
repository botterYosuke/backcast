// HakoniwaBasePanelView.cs — issue #61 "mode-conditional base tiles" (DURABLE tier, Unity boundary)
//
// One of the 4 mode-independent base panels (BuyingPower / Orders / Positions / RunResult) rendered
// as a Hakoniwa tile BODY (findings 0028 §3). A MonoBehaviour added to the tile body (mirrors
// ChartView's construction), so the dynamic base tiles carry their renderer like the chart tiles do.
//
// uGUI, NOT IMGUI: the retired ProductionLiveShell.OnGUI/GUILayout panels can't live in a
// RectTransform tile, so the rendering is new — but the DATA VM (LivePanelViewModel, via
// WorkspaceEngineHost.Panel) is reused verbatim (these panels were always live-only).
//
// HONEST EMPTY STATE (owner 2026-06-16, Option 1): the replay TradingState poll carries NO
// portfolio / orders / run_result (python/engine/core.py:_build_trading_state_locked), so in Replay
// these panels show "(no data)" — NOT fabricated values and NOT a stub to be peeled later. Wiring
// real replay numbers is the tracked follow-up #65 (additive TradingState output / backtest summary).

using System.Text;
using UnityEngine;
using UnityEngine.UI;

public sealed class HakoniwaBasePanelView : MonoBehaviour
{
    public enum Kind { BuyingPower, Orders, Positions, RunResult }

    Kind _kind;
    Text _text;
    readonly StringBuilder _sb = new StringBuilder(256);

    public void Build(RectTransform body, Kind kind, Font font)
    {
        _kind = kind;
        var go = new GameObject("PanelText", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(body, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(6f, 6f); rt.offsetMax = new Vector2(-6f, -6f);
        _text = go.GetComponent<Text>();
        _text.font = font; _text.fontSize = 12; _text.color = new Color(0.90f, 0.90f, 0.92f, 1f);
        _text.alignment = TextAnchor.UpperLeft; _text.raycastTarget = false;
        _text.horizontalOverflow = HorizontalWrapMode.Wrap;
        _text.verticalOverflow = VerticalWrapMode.Truncate;
        _text.text = Title() + "\n(no data — Replay)";
    }

    // Render from the live VM. In Replay (live == false) the poll has no panel data → honest empty
    // state (follow-up #65). A null vm in Live is treated the same as no events yet.
    public void Render(LivePanelViewModel vm, bool live)
    {
        if (_text == null) return;
        if (!live) { _text.text = Title() + "\n(no data — Replay)"; return; }

        _sb.Clear();
        _sb.Append(Title()).Append('\n');
        switch (_kind)
        {
            case Kind.BuyingPower: AppendBuyingPower(vm); break;
            case Kind.Orders:      AppendOrders(vm);      break;
            case Kind.Positions:   AppendPositions(vm);   break;
            case Kind.RunResult:   AppendRunResult(vm);   break;
        }
        _text.text = _sb.ToString();
    }

    string Title() => _kind switch
    {
        Kind.BuyingPower => "Buying Power",
        Kind.Orders      => "Orders",
        Kind.Positions   => "Positions",
        Kind.RunResult   => "Run Result",
        _                => "Panel",
    };

    void AppendBuyingPower(LivePanelViewModel vm)
    {
        if (vm != null && vm.HasAccount)
        {
            LiveAccountEvent a = vm.LatestAccount;
            _sb.Append("bp=").Append(a.BuyingPower).Append("  cash=").Append(a.Cash);
        }
        else _sb.Append("(no account snapshot)");
    }

    void AppendOrders(LivePanelViewModel vm)
    {
        if (vm != null && vm.HasOrder)
        {
            LiveOrderEvent o = vm.LatestOrder;
            _sb.Append(o.ClientOrderId).Append("  ").Append(o.Status)
               .Append("  filled=").Append(o.FilledQty).Append('@').Append(o.AvgPrice).Append('\n');
        }
        else _sb.Append("(none)\n");
        _sb.Append("filled-order count: ").Append(vm != null ? vm.FilledOrderCount : 0);
    }

    void AppendPositions(LivePanelViewModel vm)
    {
        var acct = vm != null && vm.HasAccount ? vm.LatestAccount : default;
        if (vm != null && vm.HasAccount && acct.Positions != null && acct.Positions.Count > 0)
        {
            foreach (LivePosition p in acct.Positions)
                _sb.Append(p.symbol).Append("  qty=").Append(p.qty)
                   .Append("  avg=").Append(p.avg_price).Append("  uPnL=").Append(p.unrealized_pnl).Append('\n');
        }
        else _sb.Append("(flat / no account snapshot)");
    }

    void AppendRunResult(LivePanelViewModel vm)
    {
        bool any = false;
        if (vm != null && vm.HasLifecycle)
        {
            LiveLifecycleEvent l = vm.LatestLifecycle;
            _sb.Append("run=").Append(l.RunId).Append("  ").Append(l.Status).Append('\n');
            any = true;
        }
        if (vm != null && vm.HasTelemetry)
        {
            LiveTelemetryEvent t = vm.LatestTelemetry;
            _sb.Append("realized=").Append(t.RealizedPnl).Append("  unrealized=").Append(t.UnrealizedPnl)
               .Append("  orders=").Append(t.OrderCount).Append("  fills=").Append(t.FillCount);
            any = true;
        }
        if (!any) _sb.Append("(no run)");
    }
}
