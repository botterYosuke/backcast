"""Characterization tests for build_instruments_from_master_records (Issue #36).

These pin the CLMEventDownload → InstrumentRaw build against the **real**
master record field names from the Tachibana manual
(``mfds_json_api_ref_text.html``):

* market   ← ``CLMIssueSizyouMstKabu.sZyouzyouSizyou`` ("00"=東証)  — NOT sSizyouC
* lot_size ← ``CLMIssueMstKabu.sBaibaiTani`` ("100" default),
             overridden per-market by ``CLMIssueSizyouMstKabu.sSizyoubetuBaibaiTani``
             when non-empty                                          — NOT sBaibaiTaniNumber
* tick     ← ``CLMIssueSizyouMstKabu.sYobineTaniNumber`` → CLMYobine table

Before the #36 fix the build read sSizyouC / sBaibaiTaniNumber, which do not
exist on these records, so every row was skipped (zero instruments) and the
stored market was the raw "00" rather than the Nautilus suffix "TSE".
"""
from __future__ import annotations

from engine.exchanges.tachibana_master import build_instruments_from_master_records


def _yobine_record() -> dict:
    """Minimal CLMYobine table referenced by sYobineTaniNumber="101".

    Slot 1 is the finest tick; slot 2 is the 999999999 sentinel cap.
    resolve_min_ticksize_for_issue(snapshot_price=None) returns the first
    band's tick ⇒ 1.0.
    """
    return {
        "sCLMID": "CLMYobine",
        "sYobineTaniNumber": "101",
        "sKizunPrice_1": "3000", "sYobineTanka_1": "1", "sDecimal_1": "0",
        "sKizunPrice_2": "999999999", "sYobineTanka_2": "5", "sDecimal_2": "0",
    }


def _name_record(code: str, name: str, baibai_tani: str = "100") -> dict:
    return {
        "sCLMID": "CLMIssueMstKabu",
        "sIssueCode": code,
        "sIssueName": name,
        "sBaibaiTani": baibai_tani,
    }


def _sizyou_record(
    code: str, market: str = "00", sizyoubetu_tani: str = ""
) -> dict:
    return {
        "sCLMID": "CLMIssueSizyouMstKabu",
        "sIssueCode": code,
        "sZyouzyouSizyou": market,
        "sYobineTaniNumber": "101",
        "sSizyoubetuBaibaiTani": sizyoubetu_tani,
    }


def test_build_emits_instrument_with_manual_field_names() -> None:
    records = [
        _yobine_record(),
        _name_record("7203", "トヨタ自動車"),
        _sizyou_record("7203", market="00"),
    ]
    out = build_instruments_from_master_records(records)

    assert len(out) == 1
    inst = out[0]
    assert inst.code == "7203"
    assert inst.name == "トヨタ自動車"
    assert inst.market == "TSE"  # suffix, not raw "00"
    assert inst.lot_size == 100  # from CLMIssueMstKabu.sBaibaiTani
    assert inst.tick_size == 1.0


def test_build_per_market_lot_override() -> None:
    # sSizyoubetuBaibaiTani (per-market unit) overrides the issue default.
    records = [
        _yobine_record(),
        _name_record("1234", "オオグチ銘柄", baibai_tani="100"),
        _sizyou_record("1234", market="00", sizyoubetu_tani="1000"),
    ]
    out = build_instruments_from_master_records(records)

    assert len(out) == 1
    assert out[0].lot_size == 1000


def test_build_zero_lot_override_falls_back_to_issue_default() -> None:
    # A malformed per-market unit of the string "0" must NOT win over a valid
    # issue default (a 0 trading unit would divide-by-zero downstream).
    records = [
        _yobine_record(),
        _name_record("7203", "トヨタ自動車", baibai_tani="100"),
        _sizyou_record("7203", market="00", sizyoubetu_tani="0"),
    ]
    out = build_instruments_from_master_records(records)

    assert len(out) == 1
    assert out[0].lot_size == 100


def test_build_zero_issue_default_and_no_override_skipped() -> None:
    # No usable trading unit anywhere → row skipped, never emits lot_size=0.
    records = [
        _yobine_record(),
        _name_record("7203", "トヨタ自動車", baibai_tani="0"),
        _sizyou_record("7203", market="00", sizyoubetu_tani=""),
    ]
    out = build_instruments_from_master_records(records)

    assert out == []


def test_build_unknown_market_skipped_feed_not_stopped() -> None:
    # Undocumented market code must be skipped (fail-closed), and a known
    # market in the same stream must still be emitted (feed not stopped).
    records = [
        _yobine_record(),
        _name_record("7203", "トヨタ自動車"),
        _name_record("9999", "未知市場銘柄"),
        _sizyou_record("7203", market="00"),
        _sizyou_record("9999", market="99"),  # unknown → skip
    ]
    out = build_instruments_from_master_records(records)

    codes = {i.code for i in out}
    assert codes == {"7203"}
    assert out[0].market == "TSE"


def test_build_multiple_markets_for_same_issue_emit_multiple_rows() -> None:
    # The build iterates per-sizyou-row, so one issue listed on multiple
    # markets emits one row per *known* market. Only "00"=TSE is mapped
    # today; the second (unknown) market row is safely skipped.
    records = [
        _yobine_record(),
        _name_record("7203", "トヨタ自動車"),
        _sizyou_record("7203", market="00"),
        _sizyou_record("7203", market="02"),  # 名証 candidate, unmapped → skip
    ]
    out = build_instruments_from_master_records(records)

    assert [(i.code, i.market) for i in out] == [("7203", "TSE")]
