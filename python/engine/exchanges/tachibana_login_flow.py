"""Tachibana login tkinter dialog (Phase 8 §3.2.1 Step 2 / v4r9 ADR-0023).

Subprocess entry point: login_dialog_runner invokes run_dialog() after
try_create_tk() returns True. Returns a dict that is emitted as NDJSON
on stdout. No secrets are emitted; on success the session URLs are
persisted via tachibana_file_store.save_session() and the cred-path is
unused on the Tachibana path.

v4r9 公開鍵認証: パスワード欄は廃止。利用者は **認証ID** と **秘密鍵 PEM ファイル** を
指定する（ファイル選択ダイアログ付き）。本人性は「秘密鍵で復号できること」で証明する。
debug ビルドでは初期モード（demo/prod）に応じた env を初期値に使う（本番=無印 /
デモ=`_DEMO` サフィックス。tachibana_credentials の規約と一致）。
"""
from __future__ import annotations

import asyncio
import logging
import threading
from datetime import datetime
from typing import Any
from zoneinfo import ZoneInfo

from engine.exchanges.tachibana_login_form_state import (
    AUTH_FAILED,
    NETWORK_ERROR,
    SERVICE_OUT_OF_HOURS,
    USER_CANCELLED,
    build_form_init,
    validate_submission,
)
from engine.live._build_mode import IS_DEBUG_BUILD

log = logging.getLogger(__name__)


def _map_exception(exc: BaseException) -> str:
    """Map tachibana_auth exceptions to subprocess NDJSON error_code strings."""
    # Lazy imports so module import does not require httpx/tachibana_auth.
    try:
        from engine.exchanges.tachibana_auth import (
            ApiError,
            LoginError,
            ServiceOutOfHoursError,
            SessionExpiredError,
            UnreadNoticesError,
        )
    except Exception:  # pragma: no cover - defensive
        return AUTH_FAILED
    import httpx as _httpx

    # p_errno=9（利用時間外）は ServiceOutOfHoursError 型で識別。-62（旧・サービス時間外）は
    # 応答 frame の型に乗らない legacy code なので code 値で拾う。
    if isinstance(exc, ServiceOutOfHoursError):
        return SERVICE_OUT_OF_HOURS
    if isinstance(exc, SessionExpiredError):
        return AUTH_FAILED
    if isinstance(exc, UnreadNoticesError):
        return AUTH_FAILED
    if isinstance(exc, ApiError) and getattr(exc, "code", None) == "-62":
        return SERVICE_OUT_OF_HOURS
    if isinstance(exc, LoginError):
        code = getattr(exc, "code", "") or ""
        if code in ("-62",):
            return SERVICE_OUT_OF_HOURS
        if code in ("transport_error",):
            return NETWORK_ERROR
        return AUTH_FAILED
    if isinstance(exc, (_httpx.ConnectError, _httpx.ReadError, _httpx.TimeoutException)):
        return NETWORK_ERROR
    return AUTH_FAILED


def run_dialog(env_hint: str) -> dict:
    """Open the Tachibana login dialog and return {"success", "error_code"}."""
    import tkinter as tk
    from tkinter import filedialog

    init = build_form_init(env_hint=env_hint, is_debug_build=IS_DEBUG_BUILD)
    if env_hint == "prod" and not init.allow_prod:
        # Defensive: login_dialog_runner front-stops this, but if a caller
        # bypasses the gate we still refuse without prompting the user.
        return {"success": False, "error_code": "PROD_NOT_ALLOWED"}

    from engine.exchanges.tachibana_auth import PNoCounter

    result: dict[str, Any] = {"success": False, "error_code": USER_CANCELLED}
    p_no_counter = PNoCounter()

    root = tk.Tk()
    root.title("Tachibana ログイン (公開鍵認証)")
    root.resizable(False, False)

    auth_id_var = tk.StringVar(value=init.dev_auth_id or "")
    key_path_var = tk.StringVar(value=init.dev_private_key_path or "")
    mode_var = tk.StringVar(value=init.initial_mode)
    status_var = tk.StringVar(value="")

    frm = tk.Frame(root, padx=12, pady=10)
    frm.grid(row=0, column=0)

    tk.Label(frm, text="認証 ID").grid(row=0, column=0, sticky="w")
    auth_id_entry = tk.Entry(frm, textvariable=auth_id_var, width=32)
    auth_id_entry.grid(row=0, column=1, columnspan=2, pady=2, sticky="we")

    tk.Label(frm, text="秘密鍵ファイル").grid(row=1, column=0, sticky="w")
    key_entry = tk.Entry(frm, textvariable=key_path_var, width=24)
    key_entry.grid(row=1, column=1, pady=2, sticky="we")
    browse_btn = tk.Button(frm, text="参照…", width=6)
    browse_btn.grid(row=1, column=2, padx=(4, 0))

    demo_radio = tk.Radiobutton(frm, text="Demo", variable=mode_var, value="demo")
    prod_radio = tk.Radiobutton(frm, text="Prod", variable=mode_var, value="prod")
    demo_radio.grid(row=2, column=0, sticky="w")
    prod_radio.grid(row=2, column=1, sticky="w")
    if not init.allow_prod:
        prod_radio.config(state="disabled")

    status_lbl = tk.Label(frm, textvariable=status_var, fg="#a00")
    status_lbl.grid(row=3, column=0, columnspan=3, sticky="w", pady=(4, 0))

    btn_frm = tk.Frame(frm)
    btn_frm.grid(row=4, column=0, columnspan=3, pady=(8, 0))
    ok_btn = tk.Button(btn_frm, text="OK", width=10)
    cancel_btn = tk.Button(btn_frm, text="Cancel", width=10)
    ok_btn.grid(row=0, column=0, padx=4)
    cancel_btn.grid(row=0, column=1, padx=4)

    def _on_browse(*_args: Any) -> None:
        path = filedialog.askopenfilename(
            title="秘密鍵 (PEM) を選択",
            filetypes=[("PEM private key", "*.pem"), ("All files", "*.*")],
        )
        if path:
            key_path_var.set(path)

    def _set_busy(busy: bool) -> None:
        state = "disabled" if busy else "normal"
        ok_btn.config(state=state)
        cancel_btn.config(state=state)
        auth_id_entry.config(state=state)
        key_entry.config(state=state)
        browse_btn.config(state=state)
        demo_radio.config(state=state)
        if init.allow_prod:
            prod_radio.config(state=state)

    def _on_cancel(*_args: Any) -> None:
        result["success"] = False
        result["error_code"] = USER_CANCELLED
        root.destroy()

    def _on_auth_done(payload: tuple[str, Any]) -> None:
        kind, value = payload
        if kind == "ok":
            session = value
            try:
                from engine.exchanges.tachibana_file_store import save_session
                save_session(
                    {
                        "url_request": str(session.url_request),
                        "url_master": str(session.url_master),
                        "url_price": str(session.url_price),
                        "url_event": str(session.url_event),
                        "url_event_ws": session.url_event_ws,
                        "zyoutoeki_kazei_c": session.zyoutoeki_kazei_c,
                        "last_p_no": p_no_counter.peek(),
                        "issued_jst_date": datetime.now(
                            ZoneInfo("Asia/Tokyo")
                        ).date().isoformat(),
                    }
                )
            except Exception:
                log.exception("tachibana_login_flow: save_session failed")
                result["success"] = False
                result["error_code"] = AUTH_FAILED
                root.destroy()
                return
            result["success"] = True
            result["error_code"] = ""
            root.destroy()
        else:
            exc = value
            log.error("tachibana login failed: %r", exc)
            result["success"] = False
            result["error_code"] = _map_exception(exc)

            # F-Banner1: raise_for_login_error を使って日本語メッセージを取得
            try:
                from engine.exchanges.tachibana_login_messages import raise_for_login_error
                raise_for_login_error(exc)
            except Exception as banner_exc:
                msg = getattr(banner_exc, "message", f"ログイン失敗: {result['error_code']}")
                status_var.set(msg)

            _set_busy(False)

    def _on_ok(*_args: Any) -> None:
        auth_id = auth_id_var.get().strip()
        key_path = key_path_var.get().strip()
        mode = mode_var.get()
        err = validate_submission(auth_id, key_path, mode)
        if err:
            status_var.set("認証 ID と秘密鍵ファイルを指定してください")
            return
        if mode == "prod" and not init.allow_prod:
            status_var.set("Prod は TACHIBANA_ALLOW_PROD=1 が必要")
            return

        status_var.set("Authenticating...")
        _set_busy(True)

        def _run_auth() -> None:
            from engine.exchanges.tachibana_auth import login as _auth_login
            from engine.exchanges.tachibana_pubkey import load_private_key_from_file
            try:
                # 秘密鍵 PEM を読み込み RSA 鍵オブジェクト化 (utf-8-sig で BOM 対応・
                # missing-file→PubkeyCryptoError は credentials 経路と共通の helper)。
                private_key = load_private_key_from_file(key_path)
                session = asyncio.run(
                    _auth_login(
                        auth_id,
                        private_key,
                        is_demo=(mode == "demo"),
                        p_no_counter=p_no_counter,
                    )
                )
                root.after(0, _on_auth_done, ("ok", session))
            except BaseException as exc:  # noqa: BLE001 - propagate via callback
                root.after(0, _on_auth_done, ("err", exc))

        threading.Thread(target=_run_auth, daemon=True).start()

    ok_btn.config(command=_on_ok)
    cancel_btn.config(command=_on_cancel)
    browse_btn.config(command=_on_browse)
    root.protocol("WM_DELETE_WINDOW", _on_cancel)
    root.bind("<Return>", _on_ok)
    root.bind("<Escape>", _on_cancel)

    # 認証 ID が既に入っていれば秘密鍵欄へ、無ければ認証 ID 欄へフォーカス。
    if init.is_debug_build and auth_id_var.get():
        key_entry.focus_set()
    else:
        auth_id_entry.focus_set()

    root.mainloop()
    return {"success": bool(result["success"]), "error_code": str(result["error_code"])}
