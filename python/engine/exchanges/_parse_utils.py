"""共通パースユーティリティ (各 venue が個別に保持していた変換関数を集約)。

設計方針:
- JST: module-level 定数 (timezone(timedelta(hours=9)))
- jst_yyyymmddhhmmss_to_epoch_ms: 入力が不正な場合は ValueError を送出する。
  0 フォールバックは呼び出し側 (venue ラッパー) の責務。
- parse_float: kabu/tachibana 共通の数値文字列変換。None/空/"*" は 0.0。
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

JST = timezone(timedelta(hours=9))


def jst_yyyymmddhhmmss_to_epoch_ms(s: str) -> int:
    """JST の "yyyyMMddHHmmss" 文字列 (14 桁) を UTC エポック ms に変換する。

    Args:
        s: 先頭 14 文字が "yyyyMMddHHmmss" 形式の文字列。

    Returns:
        UTC エポックミリ秒 (int)。

    Raises:
        ValueError: 文字列長が 14 未満、または日時として不正な場合。
    """
    if len(s) < 14:
        raise ValueError(f"jst_yyyymmddhhmmss_to_epoch_ms: too short: {s!r}")
    dt = datetime.strptime(s[:14], "%Y%m%d%H%M%S").replace(tzinfo=JST)
    return int(dt.timestamp() * 1000)


def parse_float(value: object) -> float:
    """取引所の数値文字列を float に変換する。None, 空, "*" は 0.0 とする。

    kabu/tachibana 共通の数量（Qty）などの数値フィールド用。
    価格（Price）フィールドで "0" を None (価格なし) として扱う必要がある場合は
    tachibana_orders._parse_price_or_none 等の専用パーサを使うこと。
    パース不能（TypeError, ValueError）な場合も 0.0 を返す。
    """
    try:
        if value in (None, "", "*"):
            return 0.0
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return 0.0
