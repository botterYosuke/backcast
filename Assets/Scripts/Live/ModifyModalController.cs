// ModifyModalController.cs — issue #34 注文訂正 UI（modify modal）の頭脳（DURABLE tier）
//
// resting 注文の price/qty 訂正フォームの **検証ロジック**を持つ plain C# クラス（MonoBehaviour 非依存・
// Python 非依存）。OrderTicketView の resting 行 [訂正] から root が OpenFor(...) で開き、uGUI の
// ModifyModalOverlay が入力面、本クラスが「Confirm 可能か」の唯一の判定（findings 0101 D2/D5）。
//
// 検証ポリシー（findings 0101 D2・楽天MS 準拠の「数量は減数のみ」）:
//   - 空欄 = その項目は「変更しない」。
//   - 数量を入れるなら filled ≤ newQty < original（**減数のみ**・増数/同値は拒否）。
//   - 価格を入れるなら >0 かつ ≠ original（原注文と同値は拒否）。
//   - qty/price とも空欄なら「変更なし」で拒否。
//   - venue が cancel+replace（kabu・findings 0101 D5）なら ack チェックを Confirm の前提にする。
// broker 側にも new_qty<filled の最終防壁があるが（order_facade/broker）、減数のみ は UI ポリシー
// （venue 不変条件ではない・増数は cancel+new で技術的に可）なので本クラス一点に置く。

using System.Globalization;

public sealed class ModifyModalController
{
    public bool Open { get; private set; }
    public string OrderId { get; private set; } = "";
    public double OriginalQty { get; private set; }
    public double? OriginalPrice { get; private set; }
    public double FilledQty { get; private set; }
    // venue の訂正が cancel+replace（非 atomic）か。true のとき ack を Confirm の前提にする。
    public bool RequiresCancelReplaceAck { get; private set; }

    // 入力バッファ（overlay の InputField/ack トグルと root が同期する。本クラスが canonical）。
    public string NewQtyBuf = "";
    public string NewPriceBuf = "";
    public bool AckCancelReplace;

    public void OpenFor(string orderId, double originalQty, double? originalPrice, double filledQty,
                        bool requiresCancelReplaceAck)
    {
        Open = true;
        OrderId = orderId ?? "";
        OriginalQty = originalQty;
        OriginalPrice = originalPrice;
        FilledQty = filledQty;
        RequiresCancelReplaceAck = requiresCancelReplaceAck;
        NewQtyBuf = "";
        NewPriceBuf = "";
        AckCancelReplace = false;
    }

    public void Close()
    {
        Open = false;
        OrderId = "";
        NewQtyBuf = "";
        NewPriceBuf = "";
        AckCancelReplace = false;
        RequiresCancelReplaceAck = false;
    }

    // 空欄/空白/非有限/<=0 は null（= 変更しない / 不正）。InvariantCulture で parse（place 経路と同流儀）。
    public static double? ParseBuf(string buf)
    {
        if (string.IsNullOrWhiteSpace(buf)) return null;
        if (double.TryParse(buf.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0)
            return v;
        return null;
    }

    public (double? qty, double? price) Parsed() => (ParseBuf(NewQtyBuf), ParseBuf(NewPriceBuf));

    // 拒否理由（status 表示用）。null なら Confirm 可。
    public string ValidationError()
    {
        if (!Open) return "modal not open";
        bool qtyGiven = !string.IsNullOrWhiteSpace(NewQtyBuf);
        bool priceGiven = !string.IsNullOrWhiteSpace(NewPriceBuf);
        if (!qtyGiven && !priceGiven) return "変更がありません";

        if (qtyGiven)
        {
            double? q = ParseBuf(NewQtyBuf);
            if (q == null) return "数量が不正です";
            if (q.Value >= OriginalQty) return "数量は減数のみ（原数量未満）";   // 増数・同値を拒否
            if (q.Value < FilledQty) return "数量が約定済みを下回ります";
        }
        if (priceGiven)
        {
            double? p = ParseBuf(NewPriceBuf);
            if (p == null) return "価格が不正です";
            if (OriginalPrice.HasValue && p.Value == OriginalPrice.Value) return "価格が原注文と同値です";
        }
        if (RequiresCancelReplaceAck && !AckCancelReplace) return "訂正の確認（ack）が必要です";
        return null;
    }

    public bool CanConfirm() => ValidationError() == null;
}
