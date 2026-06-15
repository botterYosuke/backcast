from abc import ABC, abstractmethod
from typing import Optional, Tuple


class BaseReplayProvider(ABC):
    """リプレイデータの読み込みとイテレーションの抽象ベースクラス。

    #50 (ADR-0006) で nautilus catalog 実装 (`NautilusBarsReplayProvider`) は撤去した。
    production Replay は DuckDB→kernel が外部から `apply_replay_event` で bar を流すため
    provider を組まない。本 ABC は ctor 注入の汎用 provider seam (テスト/後方互換) として残す。
    """

    @abstractmethod
    def get_next_tick(self) -> Optional[Tuple[float, float, float, float, float]]:
        """
        次のティック (timestamp, open, high, low, close) を返す。
        データが終了した場合は None を返す。
        """
        pass

    @abstractmethod
    def is_exhausted(self) -> bool:
        """すべてのデータを読み終えたかどうかを返す"""
        pass
