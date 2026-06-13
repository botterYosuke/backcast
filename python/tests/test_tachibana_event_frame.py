"""INV-T1-FRAME / INV-T4-SJIS — 立花 EVENT フレーム codec の契約 (findings/0009)。

skill R7/R8 と event_protocol.md。本フェーズは現挙動 (characterization) を pin:
- parse_event_frame は ^A(\\x01) 項目区切り / ^B(\\x02) key-value 区切りのみ解釈。
  ^C(\\x03) を含む複数値は value 文字列にそのまま保持 (未分割)。空項目と ^B 無し
  項目は skip。^C 構造化は将来課題 (一次資料 or 実 demo payload で裏取りが要)。
- deserialize_tachibana_list は "" / None を [] に正規化 (R8)。
- decode_response_body は Shift-JIS strict が既定。errors="ignore" は禁止 (R7)。
"""
from __future__ import annotations

import pytest

from engine.exchanges.tachibana_codec import (
    decode_response_body,
    deserialize_tachibana_list,
    parse_event_frame,
)


def test_parse_event_frame_basic_a_b_split() -> None:
    frame = "p_no\x02123\x01p_cmd\x02ST"
    assert parse_event_frame(frame) == [("p_no", "123"), ("p_cmd", "ST")]


def test_parse_event_frame_keeps_caret_c_raw() -> None:
    """^C を含む複数値は分割せず value にそのまま保持する (現挙動 pin)。"""
    frame = "p_GBP\x02100\x03101\x03102"
    assert parse_event_frame(frame) == [("p_GBP", "100\x03101\x03102")]


def test_parse_event_frame_skips_empty_and_keyless_items() -> None:
    """空項目と ^B を含まない項目 (keepalive 等の単独トークン) は無視する。"""
    frame = "\x01keepalive\x01p_no\x0242"
    assert parse_event_frame(frame) == [("p_no", "42")]


def test_parse_event_frame_first_b_only() -> None:
    """value 内に ^B があっても最初の ^B でのみ key/value 分割する。"""
    assert parse_event_frame("k\x02a\x02b") == [("k", "a\x02b")]


def test_deserialize_list_normalizes_empty() -> None:
    assert deserialize_tachibana_list("") == []
    assert deserialize_tachibana_list(None) == []
    assert deserialize_tachibana_list([{"x": 1}]) == [{"x": 1}]
    with pytest.raises(ValueError):
        deserialize_tachibana_list("not-a-list")


def test_decode_response_body_shift_jis_strict_default() -> None:
    raw = "新日鐵住金".encode("shift_jis")
    assert decode_response_body(raw) == "新日鐵住金"


def test_decode_response_body_rejects_ignore() -> None:
    """errors='ignore' は R7 で禁止 — API として受け付けない。"""
    with pytest.raises(ValueError):
        decode_response_body(b"abc", errors="ignore")  # type: ignore[arg-type]


def test_decode_response_body_strict_raises_on_bad_bytes() -> None:
    """strict は不正バイトでサイレント破損せず例外 (R7)。"""
    with pytest.raises(UnicodeDecodeError):
        decode_response_body(b"\xff\xfe", errors="strict")


def test_decode_response_body_replace_allowed_for_logs() -> None:
    """errors='replace' はログ等の破損許容経路でのみ可。"""
    assert decode_response_body(b"\xff", errors="replace") == "�"
