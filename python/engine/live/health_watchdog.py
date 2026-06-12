"""VenueHealthWatchdog — Phase 9 Step 7。venue 本体ログアウトの自動検知 (§3.5)。

責務 (Success Criteria「運用系」):
- ログイン成功後に start され、``interval_s`` 毎に ``adapter.check_health()`` を呼ぶ。
- ``check_health()`` が ``False`` (本体ログアウト = kabu の `4001007`/`4001017`) を返したら
  ``on_venue_logout(venue)`` を **1 回だけ** 呼ぶ。UI が再ログイン modal を開く起点になる。
- 復旧 (再び ``True``) で debounce フラグを解除し、次のログアウトでまた通知できるようにする。

設計判断:
- **transport / venue 非依存**: proto を import しない (account_sync / reducer_bridge と同思想)。
  「ログアウトしたか否か」は adapter が bool で返す契約にし、watchdog は venue 固有のエラー型を
  知らない。mock や push 型検知 venue (Tachibana) は ``check_health()`` を常に ``True`` を返す
  no-op として実装することで同じ契約を満たす。全 venue が ``check_health`` を持つため watchdog は
  常に起動する (Step 1〜3 で全 adapter に追加済み・_backend_impl の ``hasattr`` gate は廃止)。
- **初回 forced tick しない**: start は login 直後で venue は healthy なはず。account_sync は
  「初期ロードを必ず emit」のため forced だが、watchdog は「変化 (ログアウト) 検知」が目的なので
  最初の ``interval_s`` を待ってから ping する (login 直後の無駄打ち回避)。
- **transient 障害でループ継続**: ``check_health()`` の例外 (接続断・流量・想定外) は warning +
  ``last_error`` 記録で握り潰し、ループを止めない。1 回の一過性失敗で永久停止させない / 誤って
  再ログイン modal を出さない (account_sync と同じ best-effort 方針)。``CancelledError`` のみ終了。
- **debounce**: ログアウトは復旧するまで継続検出されるため、毎 tick 通知すると modal が連打される。
  ``_logged_out_emitted`` で「通知済み」を覚え、復旧 (healthy) を観測するまで再通知しない。
"""
from __future__ import annotations

import asyncio
import logging
from typing import Callable, Protocol

from engine.live.supervised_task import IntervalPollTask

_LOG = logging.getLogger(__name__)


class _HealthSource(Protocol):
    async def check_health(self) -> bool: ...


class VenueHealthWatchdog(IntervalPollTask):
    """venue 本体ログアウトを定期 ping で検知し、検出時に 1 回だけ通知する。"""

    def __init__(
        self,
        adapter: _HealthSource,
        *,
        venue_id: str,
        on_venue_logout: Callable[[str], None],
        interval_s: float = 30.0,
    ) -> None:
        self._adapter = adapter
        self._venue_id = venue_id
        self._on_venue_logout = on_venue_logout
        self._interval_s = interval_s

        self._logged_out_emitted = False

    async def start(self) -> None:
        # 既に走っていれば no-op。die 済み task は再起動を許可 (account_sync と同 semantics)。
        if self._task is not None and not self._task.done():
            return
        self._logged_out_emitted = False
        await super().start()


    async def _tick(self) -> None:
        try:
            healthy = await self._adapter.check_health()
        except asyncio.CancelledError:
            raise
        except BaseException as exc:  # noqa: BLE001 — best-effort: 1 回失敗で停止させない
            self._last_error = exc
            _LOG.warning(
                "VenueHealthWatchdog[%s]: check_health failed, continuing",
                self._venue_id,
                exc_info=exc,
            )
            return

        if healthy:
            # 復旧 → debounce を解除し、次のログアウトでまた通知できるようにする。
            self._logged_out_emitted = False
            return

        # ログアウト検出。復旧を観測するまでは 1 回だけ通知する (modal 連打回避)。
        if self._logged_out_emitted:
            return
        try:
            self._on_venue_logout(self._venue_id)
        except asyncio.CancelledError:
            raise
        except BaseException as exc:  # noqa: BLE001 — callback 失敗でループを止めない
            # 通知に失敗したら「通知済み」にしない (account_sync の _last_emitted と同方針)。
            # 次 tick でまだログアウトしていれば再通知を試みる。
            _LOG.warning(
                "VenueHealthWatchdog[%s]: on_venue_logout callback failed",
                self._venue_id,
                exc_info=exc,
            )
            return
        self._logged_out_emitted = True



