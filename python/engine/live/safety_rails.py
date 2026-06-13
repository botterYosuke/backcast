"""engine.live.safety_rails — Live 自動戦略の Safety Rails (Phase 10 §2.4 / Step 4)。

計画書 §0.6 / §2.4 の通り、Safety Rails は **ネイティブで賄える項目** と **独自ロジックが
要る項目** に分ける:

| Rail | 実装手段 |
| --- | --- |
| `max_order_value_jpy`    | ネイティブ `LiveRiskEngineConfig.max_notional_per_order`（pre-trade） |
| `max_orders_per_minute`  | ネイティブ `LiveRiskEngineConfig.max_order_submit_rate`（pre-trade rate） |
| `max_position_size_jpy`  | 独自 pre-trade（既存ポジション金額 + 新規注文金額 ≤ 上限） |
| `allowed_instruments`    | 独自 pre-trade（ホワイトリスト照合） |
| `max_daily_loss_jpy`     | 独自 post-trade（当日 P&L が上限割れで run を ERROR に） |

この module は **transport / engine / Nautilus 非依存の純粋ロジック**:
- `check_pre_trade()` / `check_post_trade()` は `RailViolation | None` を返すだけで、
  `OrderDenied` 生成・`SafetyRailViolation` push・run 停止は呼び出し側（exec client /
  controller / gRPC handler）の責務。
- **Nautilus を import してはならない**（import すると Rust core `nautilus_pyo3` が載り、
  Backcast Execution Kernel の Mono プロセス無 Rust-core 不変条件を壊す。ADR-0004 案 C /
  findings 0008 §1.1）。ネイティブ rail を Nautilus `LiveRiskEngineConfig` に変換する
  `build_live_risk_engine_config()` は `engine.live.nautilus_risk_config` 側に分離した。
  import 純度は `tests/test_gate_import_purity.py` が gate する。

**0 = 無効（その rail を課さない）**: proto `SafetyLimits` の int は未指定で 0 になる。
backend は「0 はその rail 無効」と解釈し、実際の default 値（50万 / 5 / 100万 / 10万）は
Bevy 側 UI が供給する（§0.6「Default 値は Bevy 側で設定 UI を提供」= 構造的 bypass 不可は
backend が責任を持つが、数値ポリシーは UI 由来）。
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class SafetyLimits:
    """proto `SafetyLimits` の transport 非依存ミラー。gRPC handler が proto から組む。"""

    max_position_size_jpy: int = 0
    max_order_value_jpy: int = 0
    max_daily_loss_jpy: int = 0
    max_orders_per_minute: int = 0
    allowed_instruments: tuple[str, ...] = ()


@dataclass(frozen=True)
class RailViolation:
    """Safety Rail 違反。`kind` は proto `SafetyRailViolation.kind` にそのまま載る。"""

    kind: str   # MAX_POSITION_SIZE / ALLOWED_INSTRUMENTS / MAX_DAILY_LOSS
    detail: str


# kind 定数（stringly-typed を避ける）。proto SafetyRailViolation.kind / UI トースト分類に使う。
KIND_MAX_POSITION_SIZE = "MAX_POSITION_SIZE"
KIND_ALLOWED_INSTRUMENTS = "ALLOWED_INSTRUMENTS"
KIND_MAX_DAILY_LOSS = "MAX_DAILY_LOSS"
KIND_BUYING_POWER = "BUYING_POWER"
KIND_REGULATION = "REGULATION"


def order_increases_exposure(
    net_signed_qty: float, *, is_buy: bool, order_qty: float
) -> bool:
    """注文が信用エクスポージャを**増やす**（新規建て／建て増し）か判定する。

    信用規制は建て（opening/increasing）を止めるが**返済（reducing）は常に許す**。
    規制チェックはこの関数が True のときだけ適用する（E #124 codex 指摘）。

    - net_signed_qty: 当該銘柄の現在の符号付き建玉（long>0 / short<0 / flat=0）。
    - flat からの注文は新規建て → 増加。
    - 同方向（long に BUY / short に SELL）は建て増し → 増加。
    - 逆方向（返済）は減少 → 非増加。ただし建玉量を超える逆注文は flat を突き抜けて
      反対側を新規に建てるため増加扱い（保守側）。
    """
    if net_signed_qty == 0.0:
        return True
    net_positive = net_signed_qty > 0
    if net_positive == is_buy:
        return True
    return order_qty > abs(net_signed_qty)


def check_regulation(
    *, instrument_id: str, regulated_instruments
) -> RailViolation | None:
    """信用規制銘柄の pre-trade 判定（E #124、旧 `library.examine_regulation` 相当）。

    `instrument_id` が規制対象集合に含まれていたら `RailViolation`（発注しない）。
    規制データは **口座/venue 由来の動的情報** なので `SafetyLimits` ではなく
    `check_buying_power` と同じ純関数として持つ。Replay は規制データが無いため
    呼び出し側が本関数を呼ばない（`manifest.regulation_filter.replay = not_available`）。
    """
    if instrument_id in set(regulated_instruments):
        return RailViolation(
            KIND_REGULATION,
            f"{instrument_id} is under margin-trading regulation; order suppressed",
        )
    return None


def check_buying_power(
    *, order_notional_jpy: float, buying_power_jpy: float
) -> RailViolation | None:
    """余力超過の pre-trade 判定（S4 #107 / ADR 0008 D2）。違反なら `RailViolation`。

    config rail（`SafetyLimits`）ではなく **口座由来の動的判定** なので `SafetyRails`
    のメソッドではなく純関数として持つ。SafetyLimits の「0=その rail 無効」慣習は
    **適用しない**: `buying_power_jpy=0` は「資金ゼロ」を意味し、あらゆる正の注文を弾く。

    注文金額が余力を **超える**（`>`）ときのみ違反。ぴったり（`==`）は許可する。
    mock では証明できない「実際に拒否される」判定そのもの（D2）を backend に置く
    入口で、約定前に venue へ送らせない（呼び出し側 = facade が enforcement する）。
    """
    if order_notional_jpy > buying_power_jpy:
        return RailViolation(
            KIND_BUYING_POWER,
            f"order {order_notional_jpy:.0f} JPY exceeds buying power "
            f"{buying_power_jpy:.0f} JPY",
        )
    return None


class SafetyRails:
    """1 つの Live run の Safety Rails 評価器（純粋ロジック）。"""

    def __init__(self, limits: SafetyLimits) -> None:
        self._limits = limits

    @property
    def limits(self) -> SafetyLimits:
        return self._limits

    def check_pre_trade(
        self,
        *,
        instrument_id: str,
        order_notional_jpy: float,
        current_position_value_jpy: float,
    ) -> RailViolation | None:
        """独自 pre-trade rail を評価する。違反なら `RailViolation`、OK なら `None`。

        呼び出し側（exec client）は違反時に `generate_order_denied()` + `SafetyRailViolation`
        push を行い、venue には送らない。

        - `allowed_instruments`: 空なら制限なし（起動時 instrument のみは gRPC 層が別途強制）。
          非空かつ instrument_id が含まれなければ違反。
        - `max_position_size_jpy`: |既存ポジション金額| + 新規注文金額 が上限超過なら違反
          （0 は無効）。建玉を増やす方向の保守的評価（§8 Open Risk 2、保守側に倒す）。
        """
        allowed = self._limits.allowed_instruments
        if allowed and instrument_id not in allowed:
            return RailViolation(
                KIND_ALLOWED_INSTRUMENTS,
                f"{instrument_id} not in allowed_instruments {list(allowed)}",
            )

        cap = self._limits.max_position_size_jpy
        if cap > 0:
            projected = abs(current_position_value_jpy) + abs(order_notional_jpy)
            if projected > cap:
                return RailViolation(
                    KIND_MAX_POSITION_SIZE,
                    f"projected position {projected:.0f} JPY exceeds cap {cap} JPY",
                )
        return None

    def check_post_trade(self, *, daily_pnl_jpy: float) -> RailViolation | None:
        """独自 post-trade rail（`max_daily_loss_jpy`）を評価する。

        当日の realized + unrealized P&L が `-max_daily_loss_jpy` を下回ったら違反。
        呼び出し側は run を `LiveStrategyStateMachine.error("MAX_DAILY_LOSS_EXCEEDED")` に
        遷移させ in-flight order を cancel する（§1.3）。0 は無効。
        """
        cap = self._limits.max_daily_loss_jpy
        if cap > 0 and daily_pnl_jpy < -cap:
            return RailViolation(
                KIND_MAX_DAILY_LOSS,
                f"daily P&L {daily_pnl_jpy:.0f} JPY breached loss limit -{cap} JPY",
            )
        return None
