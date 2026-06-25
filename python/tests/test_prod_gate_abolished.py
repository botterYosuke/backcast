"""ADR-0027 — prod 解禁の env ゲート廃止の回帰ゲート (#130 / #131)。

固定する不変条件（delete-the-production-logic litmus で RED→GREEN が立つ）:

- PRODGATE-02: kabusapi_url.base_url("prod") は env フラグ無しでも raise せず prod URL を返す。
- PRODGATE-03: kabu の build_form_init("prod") は env フラグ無しで本体ポート 18080 を返し、
  廃止された解禁フラグ系フィールド (allow_prod / dev_* / is_debug_build) を持たない。
- PRODGATE-04: tachibana の build_form_init("prod") は env フラグ無しで初期モード "prod" を返し、
  廃止された解禁フラグ系フィールド (allow_prod / dev_* / is_debug_build) を持たない。
- PRODGATE-08: login ダイアログ flow (run_dialog) は process env を一切読まない。
- ゲート撤去後も未知 env は INVALID_ENV のまま弾く (environment_hint の _ENV_PER_VENUE 検証は残る)。

PRODGATE-01 (dispatcher の prod front-stop 撤去) は test_inproc_prompt_login.py が持つ。

注: 旧 PRODGATE-05/06 (「DEV_* prefill 廃止」=ダイアログは空欄で開く) は **ADR-0033 が D3 を
supersede** したため撤去。demo/verify の prefill 復活を assert する新ゲート PREFILL-01..05 が
test_login_prefill.py にある。PRODGATE-08 は ADR-0033 でも維持される (env 読みは presenter に閉じる)。
"""
from __future__ import annotations

import pytest

from engine.exchanges import kabusapi_url
from engine.exchanges import kabusapi_login_form_state as kabu_form
from engine.exchanges import tachibana_login_form_state as tachi_form


# --- PRODGATE-02: kabu URL builder は prod を env フラグ無しで通す -----------------


@pytest.mark.scenario("PRODGATE-02")
def test_kabu_base_url_prod_without_allow_flag(monkeypatch):
    monkeypatch.delenv("KABU_ALLOW_PROD", raising=False)
    assert kabusapi_url.base_url("prod") == "http://localhost:18080/kabusapi/"
    assert kabusapi_url.base_url("verify") == "http://localhost:18081/kabusapi/"
    # ws_url も prod を素通しする (base_url 経由)。
    assert kabusapi_url.ws_url("prod").startswith("ws://localhost:18080/")
    # 未知 env は従来どおり弾く。
    with pytest.raises(ValueError):
        kabusapi_url.base_url("bogus")  # type: ignore[arg-type]


# --- PRODGATE-03: kabu form-state は prod ポートを env 無しで決める -----------------


@pytest.mark.scenario("PRODGATE-03")
def test_kabu_form_init_prod_port_without_allow_flag(monkeypatch):
    monkeypatch.delenv("KABU_ALLOW_PROD", raising=False)
    prod = kabu_form.build_form_init("prod")
    verify = kabu_form.build_form_init("verify")
    assert prod.station_port == 18080
    assert verify.station_port == 18081
    # ADR-0027: 解禁フラグ / prefill 由来のフィールドは存在しない。
    fields = set(vars(prod))
    assert "allow_prod" not in fields
    assert "is_debug_build" not in fields
    assert not any(f.startswith("dev_") for f in fields)


# --- PRODGATE-04: tachibana form-state は prod モードを env 無しで決める -------------


@pytest.mark.scenario("PRODGATE-04")
def test_tachibana_form_init_prod_mode_without_allow_flag(monkeypatch):
    monkeypatch.delenv("TACHIBANA_ALLOW_PROD", raising=False)
    prod = tachi_form.build_form_init("prod")
    demo = tachi_form.build_form_init("demo")
    assert prod.initial_mode == "prod"
    assert demo.initial_mode == "demo"
    fields = set(vars(prod))
    assert "allow_prod" not in fields
    assert "is_debug_build" not in fields
    assert not any(f.startswith("dev_") for f in fields)


# --- PRODGATE-08: login ダイアログ widget は process-env を読まない -------------------
# ADR-0033 でも維持: prefill は復活したが env 読みは presenter (build_form_init) に閉じ、
# run_dialog (tkinter widget) は os.environ を直接読まない。歴史的に prefill が実在したのは
# run_dialog の credential StringVar 側で、そこに
#   pw_var = tk.StringVar(value=os.environ.get("DEV_KABU_API_PASSWORD", ""))
# を書くと presenter テストを素通りしてしまう。flow モジュールが environ/getenv を持たないことを
# source-scan で固定する（ADR-0033 D4: env 読みは presenter に閉じる。run_dialog に戻すと RED）。


@pytest.mark.scenario("PRODGATE-08")
def test_login_dialog_modules_read_no_process_env():
    import pathlib

    exchanges = pathlib.Path(kabusapi_url.__file__).resolve().parent
    offenders: list[str] = []
    for name in ("kabusapi_login_flow.py", "tachibana_login_flow.py"):
        text = (exchanges / name).read_text(encoding="utf-8")
        for lineno, line in enumerate(text.splitlines(), 1):
            if "environ" in line or "getenv" in line:
                offenders.append(f"{name}:{lineno}: {line.strip()!r}")
    assert not offenders, (
        "ADR-0027 D3 違反: login ダイアログが process env を読んでいる "
        "(資格情報は常にユーザー入力・env prefill 禁止):\n" + "\n".join(offenders)
    )
