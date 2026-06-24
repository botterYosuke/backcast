"""kabuStation login tkinter dialog (Phase 8 §3.2.1 Step 3).

In-process entry point (#122): ``LiveLoopManager._handle_prompt_login`` runs
run_dialog() on a dedicated thread after ``_try_create_tk()`` returns True. The
bearer token is returned in the result dict (``{"success", "error_code",
"token"}``) and kept in the embedded Python's memory — never written to a
cred-path file, never emitted to stdout.
"""
from __future__ import annotations

import asyncio
import logging
import threading
from typing import Any, Optional

from engine.exchanges.kabusapi_login_form_state import (
    AUTH_FAILED,
    KABU_API_DISABLED,
    KABU_STATION_NOT_RUNNING,
    KABU_TOKEN_EXPIRED,
    USER_CANCELLED,
    auth_failure_view,
    build_form_init,
    probe_station,
    validate_submission,
)
from engine.exchanges._login_dialog import apply_cancel_timeout
from engine.live._build_mode import IS_DEBUG_BUILD

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
        return KABU_TOKEN_EXPIRED
    if isinstance(exc, KabuConnectionError):
        return KABU_STATION_NOT_RUNNING
    if isinstance(exc, KabuApiError):
        code = getattr(exc, "code", None)
        if code in (4001003, "4001003"):
            return KABU_API_DISABLED
        # 4001005 = パラメータ変換エラー (R7:231)。token-expired ではないので
        # AUTH_FAILED に落とす (再認証を誘発しない)。token 失効は HTTP 401 →
        # KabuTokenExpiredError として上の isinstance 分岐で処理 (findings/0009)。
        return AUTH_FAILED
    return AUTH_FAILED


def run_dialog(env_hint: str, cancel_event: threading.Event | None = None) -> dict:
    """Open the kabuStation login dialog and return {"success", "error_code", "token"}.

    On success the bearer token is returned in ``token`` (in-memory, #122). On
    failure/cancel ``token`` is None.
    """
    import tkinter as tk

    init = build_form_init(env_hint=env_hint, is_debug_build=IS_DEBUG_BUILD)
    if env_hint == "prod" and not init.allow_prod:
        return {"success": False, "error_code": "PROD_NOT_ALLOWED", "token": None}

    result: dict[str, Any] = {"success": False, "error_code": USER_CANCELLED, "token": None}

    root = tk.Tk()
    root.title("kabuStation ログイン")
    root.resizable(False, False)

    pw_var = tk.StringVar(value=init.dev_api_password or "")
    mode_var = tk.StringVar(value="prod" if (env_hint == "prod" and init.allow_prod) else "verify")
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
    if not init.allow_prod:
        prod_radio.config(state="disabled")

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
        return 18080 if (mode_var.get() == "prod" and init.allow_prod) else 18081

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
        if init.allow_prod:
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
        if mode == "prod" and not init.allow_prod:
            status_var.set("Prod は KABU_ALLOW_PROD=1 が必要")
            return
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

    if cancel_event is not None:
        root.after(200, _poll_cancel)
    root.mainloop()
    return {
        "success": bool(result["success"]),
        "error_code": str(result["error_code"]),
        "token": result["token"],
    }
