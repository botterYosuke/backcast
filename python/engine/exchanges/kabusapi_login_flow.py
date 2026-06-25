"""kabuStation login tkinter dialog (Phase 8 §3.2.1 Step 3).

In-process entry point (#122): ``LiveLoopManager._handle_prompt_login`` runs
run_dialog() on a dedicated thread after ``_try_create_tk()`` returns True. The
bearer token is returned in the result dict (``{"success", "error_code",
"token"}``) and kept in the embedded Python's memory — never written to a
cred-path file, never emitted to stdout.
"""
from __future__ import annotations

import asyncio
import gc
import logging
import threading
from typing import Any, Optional

from engine.exchanges.kabusapi_login_form_state import (
    AUTH_FAILED,
    KABU_API_DISABLED,
    KABU_AUTH_REJECTED,
    KABU_STATION_NOT_LOGGED_IN,
    KABU_STATION_NOT_RUNNING,
    USER_CANCELLED,
    auth_failure_view,
    build_form_init,
    probe_station,
    validate_submission,
)
from engine.exchanges._login_dialog import apply_cancel_timeout, teardown_tk

log = logging.getLogger(__name__)


def _map_exception(exc: BaseException) -> str:
    try:
        from engine.exchanges.kabusapi_auth import (
            KabuApiError,
            KabuConnectionError,
            KabuTokenExpiredError,
        )
    except Exception:  # pragma: no cover
        return AUTH_FAILED

    if isinstance(exc, KabuTokenExpiredError):
        # この flow の唯一の auth 呼び出しは /token (fetch_token)。その HTTP 401 は
        # 失効トークンではなく、body code で意味が分かれる (findings 0109 / D1):
        #   STATION_LOGGED_OUT_CODES → 本体が口座へ未ログイン (アプリ起動済みでも未ログイン・早朝強制ログアウト)
        #   4001013 ほか             → ログイン済みだが API パスワード不正
        # いずれも旧実装は一律「トークン期限切れ」と誤表示していた。
        from engine.exchanges.kabusapi_auth import STATION_LOGGED_OUT_CODES

        body_code = getattr(exc, "body_code", None)
        if body_code in STATION_LOGGED_OUT_CODES:
            return KABU_STATION_NOT_LOGGED_IN
        return KABU_AUTH_REJECTED
    if isinstance(exc, KabuConnectionError):
        return KABU_STATION_NOT_RUNNING
    if isinstance(exc, KabuApiError):
        code = getattr(exc, "code", None)
        if code in (4001003, "4001003"):
            return KABU_API_DISABLED
        # 4001005 = パラメータ変換エラー (R7:231)。AUTH_FAILED に落とす。HTTP 401 は
        # KabuTokenExpiredError として上の isinstance 分岐で KABU_AUTH_REJECTED に
        # 処理される (login flow の 401 = /token のパスワード不正・findings/0009, 0106)。
        return AUTH_FAILED
    return AUTH_FAILED


def run_dialog(env_hint: str, cancel_event: threading.Event | None = None) -> dict:
    """Open the kabuStation login dialog and return {"success", "error_code", "token"}.

    On success the bearer token is returned in ``token`` (in-memory, #122). On
    failure/cancel ``token`` is None.

    #133: every tkinter object lives inside ``_run_dialog_impl``'s frame, which is
    released the instant it returns. The trailing ``gc.collect()`` is the *decisive*
    same-thread sweep (see ``_login_dialog.teardown_tk``): it finalizes the now-unreachable
    reference-cycle garbage (Tk root / widgets / StringVars) HERE, on the creating
    thread — never later on a ``PickerInstrumentFetch`` worker, which would trip
    ``Tcl_AsyncDelete`` and crash Unity. The result dict holds only plain strings, so
    nothing tkinter-shaped escapes this function.
    """
    result = _run_dialog_impl(env_hint, cancel_event)
    gc.collect()  # load-bearing: frame is popped, so this reclaims the Tk cycle (#133)
    return result


def _run_dialog_impl(env_hint: str, cancel_event: threading.Event | None = None) -> dict:
    import tkinter as tk

    # ADR-0027: prod 解禁 env ゲートは廃止。env_hint をそのまま初期モードにし、Prod ラジオは
    # 常時選択可能。API パスワードは prefill せず常にユーザーが入力する (空欄で開く)。
    init = build_form_init(env_hint=env_hint)

    result: dict[str, Any] = {"success": False, "error_code": USER_CANCELLED, "token": None}

    root = tk.Tk()
    root.title("kabuStation ログイン")
    root.resizable(False, False)

    pw_var = tk.StringVar(value="")
    mode_var = tk.StringVar(value="prod" if env_hint == "prod" else "verify")
    status_var = tk.StringVar(value="")
    port_var = tk.StringVar(value=str(init.station_port))

    frm = tk.Frame(root, padx=12, pady=10)
    frm.grid(row=0, column=0)

    tk.Label(frm, text="API パスワード").grid(row=0, column=0, sticky="w")
    pw_entry = tk.Entry(frm, textvariable=pw_var, show="*", width=24)
    pw_entry.grid(row=0, column=1, pady=2)

    verify_radio = tk.Radiobutton(frm, text="Verify", variable=mode_var, value="verify")
    prod_radio = tk.Radiobutton(frm, text="Prod", variable=mode_var, value="prod")
    verify_radio.grid(row=1, column=0, sticky="w")
    prod_radio.grid(row=1, column=1, sticky="w")

    tk.Label(frm, text="本体ポート:").grid(row=2, column=0, sticky="w")
    tk.Label(frm, textvariable=port_var).grid(row=2, column=1, sticky="w")

    status_lbl = tk.Label(frm, textvariable=status_var, fg="#a00")
    status_lbl.grid(row=3, column=0, columnspan=3, sticky="w", pady=(4, 0))

    btn_frm = tk.Frame(frm)
    btn_frm.grid(row=4, column=0, columnspan=3, pady=(8, 0))
    ok_btn = tk.Button(btn_frm, text="OK", width=10)
    recheck_btn = tk.Button(btn_frm, text="再確認", width=10)
    cancel_btn = tk.Button(btn_frm, text="Cancel", width=10)
    ok_btn.grid(row=0, column=0, padx=4)
    recheck_btn.grid(row=0, column=1, padx=4)
    cancel_btn.grid(row=0, column=2, padx=4)

    def _current_port() -> int:
        return 18080 if mode_var.get() == "prod" else 18081

    def _refresh_station_status() -> None:
        port = _current_port()
        port_var.set(str(port))
        if probe_station(port=port):
            status_var.set("")
            ok_btn.config(state="normal")
        else:
            status_var.set(f"{KABU_STATION_NOT_RUNNING}: port {port}")
            ok_btn.config(state="disabled")

    def _set_busy(busy: bool, *, ok_btn_active: Optional[bool] = None) -> None:
        state = "disabled" if busy else "normal"
        ok_state = state if ok_btn_active is None else ("normal" if ok_btn_active else "disabled")
        ok_btn.config(state=ok_state)
        cancel_btn.config(state=state)
        recheck_btn.config(state=state)
        pw_entry.config(state=state)
        verify_radio.config(state=state)
        prod_radio.config(state=state)
        if not busy and ok_btn_active is None:
            _refresh_station_status()

    def _on_cancel(*_args: Any) -> None:
        result["success"] = False
        result["error_code"] = USER_CANCELLED
        root.destroy()

    def _on_auth_done(payload: tuple[str, Any]) -> None:
        kind, value = payload
        if kind == "ok":
            result["success"] = True
            result["error_code"] = ""
            result["token"] = value  # bearer token, returned in-memory (#122)
            root.destroy()
        else:
            exc = value
            log.error("kabu login failed: %r", exc)
            ec = _map_exception(exc)
            view = auth_failure_view(ec)
            result["success"] = False
            result["error_code"] = ec
            status_var.set(view.status_text)
            _set_busy(False, ok_btn_active=view.allow_retry)

    def _on_ok(*_args: Any) -> None:
        api_password = pw_var.get()
        err = validate_submission(api_password)
        if err:
            status_var.set("API パスワードを入力してください")
            return
        mode = mode_var.get()
        port = _current_port()
        if not probe_station(port=port):
            status_var.set(f"{KABU_STATION_NOT_RUNNING}: port {port}")
            return

        status_var.set("Authenticating...")
        _set_busy(True)

        env_arg = "prod" if mode == "prod" else "verify"

        def _run_auth() -> None:
            from engine.exchanges.kabusapi_auth import fetch_token
            try:
                token = asyncio.run(fetch_token(api_password, env=env_arg))
                payload: tuple[str, Any] = ("ok", token)
            except BaseException as exc:  # noqa: BLE001
                payload = ("err", exc)
            try:
                root.after(0, _on_auth_done, payload)
            except Exception:
                # The dialog was already torn down (user cancel / timeout cancel):
                # the auth result is moot, so drop it instead of letting this
                # daemon thread die with a TclError on the destroyed root.
                pass

        threading.Thread(target=_run_auth, daemon=True).start()

    ok_btn.config(command=_on_ok)
    cancel_btn.config(command=_on_cancel)
    recheck_btn.config(command=_refresh_station_status)
    root.protocol("WM_DELETE_WINDOW", _on_cancel)
    root.bind("<Return>", _on_ok)
    root.bind("<Escape>", _on_cancel)

    def _poll_cancel() -> None:
        # Tk-thread-safe close on an external timeout (#122 in-proc login).
        if cancel_event is not None and cancel_event.is_set():
            apply_cancel_timeout(result)
            root.destroy()
            return
        root.after(200, _poll_cancel)

    _refresh_station_status()
    pw_entry.focus_set()

    try:
        if cancel_event is not None:
            root.after(200, _poll_cancel)
        root.mainloop()
        return {
            "success": bool(result["success"]),
            "error_code": str(result["error_code"]),
            "token": result["token"],
        }
    finally:
        # #133: destroy on the creating thread (idempotent — the dialog callbacks
        # already called root.destroy()). The decisive same-thread gc.collect() runs
        # in the run_dialog wrapper, once this frame is popped and the Tk cycle is
        # finally unreachable — so no Tk/Tcl object survives for a cross-thread
        # finalize (Tcl_AsyncDelete).
        teardown_tk(root)
