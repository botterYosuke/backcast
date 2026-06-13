// ReplayPanelDecoder.cs — issue #10 "Replay chart" (panel slice, durable tier)
//
// The typed decode API for the replay side-panel JSON streams (order rows,
// portfolio snapshot, run summary). This is the DURABLE half of the panel
// slice (mirrors ReplayBarDecoder): typed models + single decode entry points
// (DecodeOrder / DecodePortfolio / DecodeRunResult) that downstream widgets
// reuse. The throwaway halves (M2 AFK decode probe, M3 HITL playmode widget)
// live elsewhere and consume this API.
//
// PARSER IS HIDDEN: each Decode uses Unity's built-in JsonUtility internally,
// but no caller sees that — the parser is an implementation detail behind the
// Decode methods, so it can be swapped (e.g. to a dict-capable parser) without
// touching consumers. No Newtonsoft is added (not in the manifest; premature).
//
// PAYLOAD sources (owner-verified):
//   * order / portfolio handlers — python/engine/live/gui_bridge_actor.py.
//     order = { symbol, client_order_id, venue_order_id, strategy_id, side,
//     status, qty, price, timestamp_ms }. portfolio = { buying_power, equity,
//     positions: [ { symbol, qty, avg_price } ], orders }. orders is always []
//     here so the DTO does not declare it.
//   * run summary — python/engine/nautilus_backtest_runner.py. summary =
//     { fills_count, equity_points, max_drawdown, sharpe, sortino }.
//
// JsonUtility binding rule this file depends on:
//   * Binds by VERBATIM field name. Array-element DTOs (PositionRow) keep
//     snake_case to match JSON keys EXACTLY — do NOT rename to PascalCase: a
//     name mismatch is silently zero-filled (no error), so a renamed element
//     field would decode to 0/null with no signal. The consumer-facing value
//     types (OrderRow / PortfolioSnapshot / RunResult) are hand-mapped from the
//     private DTOs, so THOSE use PascalCase (mirrors #10's ReplayBarFrame).
//
// Decode CONTRACT (all three methods):
//   * null / empty / whitespace json -> empty result (default / empty Positions,
//     no throw).
//   * json == "null"                 -> empty result (private class DTOs let
//     JsonUtility return null, which we map back to the empty result).
//   * valid json                     -> hand-mapped typed value.
//   * MALFORMED json                 -> NOT swallowed; JsonUtility throws. The
//     grounded payload is always valid json.dumps output, so a parse failure is
//     a real bug we want surfaced (mirrors ReplayBarDecoder's discipline).
//
// INTERMEDIATE STATE: this file compiles but is UNUSED until the M2 probe drains
// the JSON and calls the Decode methods. No .meta is authored here — Unity
// generates ReplayPanelDecoder.cs.meta on the next import.

using System;
using System.Collections.Generic;
using UnityEngine;

// 配列要素として JsonUtility が直接バインドするので snake_case [Serializable]
// （#10 の OhlcPoint と同型。PascalCase に改名しないこと＝silent zero-fill 化する）
[System.Serializable]
public struct PositionRow
{
    public string symbol;
    public double qty;
    public double avg_price;
}

// 以下 3 つは hand-map された consumer 向け値型 → PascalCase（#10 の ReplayBarFrame と同型）
public struct OrderRow
{
    public string Symbol;
    public string ClientOrderId;
    public string VenueOrderId;
    public string StrategyId;
    public string Side;
    public string Status;
    public double Qty;
    public double Price;
    public long TimestampMs;
}

public struct PortfolioSnapshot
{
    public double BuyingPower;
    public double Equity;
    public IReadOnlyList<PositionRow> Positions;
}

public struct RunResult
{
    public long FillsCount;
    public long EquityPoints;
    public double MaxDrawdown;
    public double Sharpe;
    public double Sortino;
}

public static class ReplayPanelDecoder
{
    // private class DTO（JsonUtility 隠蔽。class なので "null" → null を返せる）
    [System.Serializable] class OrderDto
    {
        public string symbol;
        public string client_order_id;
        public string venue_order_id;
        public string strategy_id;
        public string side;
        public string status;
        public double qty;
        public double price;
        public long timestamp_ms;
    }

    [System.Serializable] class PortfolioDto
    {
        public double buying_power;
        public double equity;
        public PositionRow[] positions;   // orders は常に [] なので宣言しない
    }

    [System.Serializable] class RunResultDto
    {
        public long fills_count;
        public long equity_points;
        public double max_drawdown;
        public double sharpe;
        public double sortino;
    }

    public static OrderRow DecodeOrder(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        var dto = JsonUtility.FromJson<OrderDto>(json);
        if (dto == null) return default;
        return new OrderRow
        {
            Symbol = dto.symbol,
            ClientOrderId = dto.client_order_id,
            VenueOrderId = dto.venue_order_id,
            StrategyId = dto.strategy_id,
            Side = dto.side,
            Status = dto.status,
            Qty = dto.qty,
            Price = dto.price,
            TimestampMs = dto.timestamp_ms,
        };
    }

    public static PortfolioSnapshot DecodePortfolio(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PortfolioSnapshot { Positions = Array.Empty<PositionRow>() };
        var dto = JsonUtility.FromJson<PortfolioDto>(json);
        if (dto == null)
            return new PortfolioSnapshot { Positions = Array.Empty<PositionRow>() };
        return new PortfolioSnapshot
        {
            BuyingPower = dto.buying_power,
            Equity = dto.equity,
            Positions = dto.positions ?? Array.Empty<PositionRow>(),
        };
    }

    public static RunResult DecodeRunResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        var dto = JsonUtility.FromJson<RunResultDto>(json);
        if (dto == null) return default;
        return new RunResult
        {
            FillsCount = dto.fills_count,
            EquityPoints = dto.equity_points,
            MaxDrawdown = dto.max_drawdown,
            Sharpe = dto.sharpe,
            Sortino = dto.sortino,
        };
    }
}
