"""engine.signals — `_stocktrading` ↔ TTWR の signals/manifest 境界契約ローダ。

Epic #118 / issue #119 (F) の**正本**実装。`_stocktrading`（頭脳）が出力する
日次シグナル JSON と manifest を読み込み・検証して、後続の戦略 (A〜E) が消費する
データ構造に変換する。schema は `docs/signals-contract.md` に定義する。

このモジュールは **nautilus_trader に依存しない**（pure-Python）。ts→日付の変換など
実行系の関心は戦略側 (`SignalDrivenDayTradeStrategy`) が持つ。

Public API:
    SignalsValidationError
    Signal / DailySignals / SignalsManifest
    load_manifest(path)            -> SignalsManifest
    load_daily_signals(path)       -> DailySignals
    validate_manifest_consistency(manifest)  -> None
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

SCHEMA_VERSION = 1
_SIDES: frozenset[str] = frozenset({"LONG", "SHORT"})
# signals_YYYY-MM-DD.json の filename から target_date を抽出する
_FILE_RE = re.compile(r"^signals_(\d{4}-\d{2}-\d{2})\.json$")
# regulation_filter のキー（情報のみ運ぶ。値域は緩く検証する）
_REGULATION_KEYS: frozenset[str] = frozenset({"brain", "replay", "live"})


# ---------------------------------------------------------------------------
# Exception
# ---------------------------------------------------------------------------


class SignalsValidationError(Exception):
    """signals/manifest JSON の schema 違反・整合性違反を表す（fail-closed）。"""


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Signal:
    """1 銘柄の売買判断。"""

    symbol: str
    side: str  # "LONG" | "SHORT"
    confidence: float  # (0, 1]


@dataclass(frozen=True)
class DailySignals:
    """signals_YYYY-MM-DD.json = 1 営業日の売買判断（正本）。"""

    schema_version: int
    target_date: str  # "YYYY-MM-DD"
    as_of: str  # "YYYY-MM-DD"
    signals: tuple[Signal, ...]

    def symbols(self) -> set[str]:
        return {s.symbol for s in self.signals}


@dataclass(frozen=True)
class SignalsManifest:
    """manifest.json = レンジ全体のメタ + 購読用和集合。"""

    schema_version: int
    start: str
    end: str
    timezone: str
    prediction_horizon: str
    retrain_policy: str
    train_window_business_days: int
    files: tuple[str, ...]
    instruments: tuple[str, ...]
    regulation_filter: dict
    base_dir: Path = field(compare=False)

    # -- date → daily-file 対応 ---------------------------------------------

    def date_to_file(self) -> dict[str, str]:
        """{"YYYY-MM-DD": "signals_YYYY-MM-DD.json"} を返す（filename 由来）。"""
        out: dict[str, str] = {}
        for name in self.files:
            m = _FILE_RE.match(name)
            if m is not None:
                out[m.group(1)] = name
        return out

    def signals_path_for_date(self, target_date: str) -> Path | None:
        """target_date に対応する日次ファイルの絶対 Path（無ければ None）。"""
        name = self.date_to_file().get(target_date)
        if name is None:
            return None
        return self.base_dir / name

    def load_signals_for_date(self, target_date: str) -> DailySignals | None:
        """target_date の DailySignals を読み込む（対応ファイル無しは None）。

        ファイルは存在するが target_date が食い違う場合は SignalsValidationError。
        """
        path = self.signals_path_for_date(target_date)
        if path is None:
            return None
        daily = load_daily_signals(path)
        if daily.target_date != target_date:
            raise SignalsValidationError(
                f"daily signals target_date {daily.target_date!r} does not match "
                f"filename date {target_date!r} ({path.name})"
            )
        return daily


# ---------------------------------------------------------------------------
# Low-level validation helpers
# ---------------------------------------------------------------------------


def _require_dict(obj: object, what: str) -> dict:
    if not isinstance(obj, dict):
        raise SignalsValidationError(f"{what} must be a JSON object, got {type(obj).__name__}")
    return obj


def _require_keys(d: dict, required: frozenset[str], what: str) -> None:
    missing = required - d.keys()
    if missing:
        raise SignalsValidationError(f"{what} missing required keys: {sorted(missing)}")


def _require_type(d: dict, key: str, expected: type, what: str):
    val = d[key]
    if isinstance(val, bool) and expected is int:
        raise SignalsValidationError(f"{what}[{key!r}] must be int, got bool")
    if not isinstance(val, expected):
        raise SignalsValidationError(
            f"{what}[{key!r}] must be {expected.__name__}, got {type(val).__name__}"
        )
    return val


def _require_schema_version(d: dict, what: str) -> int:
    sv = _require_type(d, "schema_version", int, what)
    if sv != SCHEMA_VERSION:
        raise SignalsValidationError(
            f"{what} schema_version must be {SCHEMA_VERSION}, got {sv!r}"
        )
    return sv


def _require_iso_date(value: str, what: str) -> str:
    try:
        date.fromisoformat(value)
    except (ValueError, TypeError) as exc:
        raise SignalsValidationError(f"{what} must be an ISO date (YYYY-MM-DD), got {value!r}") from exc
    return value


def _read_json(path: Path, what: str) -> dict:
    p = Path(path)
    if not p.exists():
        raise SignalsValidationError(f"{what} not found: {p}")
    try:
        raw = json.loads(p.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SignalsValidationError(f"{what} is not valid JSON: {p}: {exc}") from exc
    return _require_dict(raw, what)


# ---------------------------------------------------------------------------
# load_daily_signals
# ---------------------------------------------------------------------------

_DAILY_REQUIRED: frozenset[str] = frozenset(
    {"schema_version", "target_date", "as_of", "signals"}
)
_SIGNAL_REQUIRED: frozenset[str] = frozenset({"symbol", "side", "confidence"})


def load_daily_signals(path: str | Path) -> DailySignals:
    """signals_YYYY-MM-DD.json を読み込み・検証する。

    Raises:
        SignalsValidationError: schema 違反 / side・confidence 値域違反など。
    """
    what = "daily signals"
    doc = _read_json(Path(path), what)
    _require_keys(doc, _DAILY_REQUIRED, what)
    _require_schema_version(doc, what)
    target_date = _require_iso_date(_require_type(doc, "target_date", str, what), f"{what}['target_date']")
    as_of = _require_iso_date(_require_type(doc, "as_of", str, what), f"{what}['as_of']")

    raw_signals = _require_type(doc, "signals", list, what)
    signals: list[Signal] = []
    seen: set[str] = set()
    for i, raw in enumerate(raw_signals):
        sig_what = f"{what}['signals'][{i}]"
        s = _require_dict(raw, sig_what)
        _require_keys(s, _SIGNAL_REQUIRED, sig_what)
        symbol = _require_type(s, "symbol", str, sig_what)
        if not symbol or "." not in symbol:
            raise SignalsValidationError(
                f"{sig_what}['symbol'] must look like '<code>.<venue>', got {symbol!r}"
            )
        if symbol in seen:
            raise SignalsValidationError(f"{sig_what}['symbol'] duplicated: {symbol!r}")
        seen.add(symbol)
        side = _require_type(s, "side", str, sig_what)
        if side not in _SIDES:
            raise SignalsValidationError(
                f"{sig_what}['side'] must be one of {sorted(_SIDES)}, got {side!r}"
            )
        confidence = s["confidence"]
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)):
            raise SignalsValidationError(
                f"{sig_what}['confidence'] must be a number, got {type(confidence).__name__}"
            )
        confidence = float(confidence)
        if not (0.0 < confidence <= 1.0):
            raise SignalsValidationError(
                f"{sig_what}['confidence'] must be in (0, 1], got {confidence!r}"
            )
        signals.append(Signal(symbol=symbol, side=side, confidence=confidence))

    return DailySignals(
        schema_version=SCHEMA_VERSION,
        target_date=target_date,
        as_of=as_of,
        signals=tuple(signals),
    )


# ---------------------------------------------------------------------------
# load_manifest
# ---------------------------------------------------------------------------

_MANIFEST_REQUIRED: frozenset[str] = frozenset(
    {
        "schema_version",
        "start",
        "end",
        "timezone",
        "prediction_horizon",
        "retrain_policy",
        "train_window_business_days",
        "files",
        "instruments",
        "regulation_filter",
    }
)


def load_manifest(path: str | Path) -> SignalsManifest:
    """signals/manifest.json を読み込み・検証する。

    日次ファイル中身の整合は別途 `validate_manifest_consistency()` で確認する
    （こちらは manifest 自体の schema と filename↔date 対応のみ）。

    Raises:
        SignalsValidationError: schema 違反 / files の filename 不正 / 日付重複など。
    """
    what = "manifest"
    manifest_path = Path(path)
    doc = _read_json(manifest_path, what)
    _require_keys(doc, _MANIFEST_REQUIRED, what)
    _require_schema_version(doc, what)

    start = _require_iso_date(_require_type(doc, "start", str, what), f"{what}['start']")
    end = _require_iso_date(_require_type(doc, "end", str, what), f"{what}['end']")
    if date.fromisoformat(end) < date.fromisoformat(start):
        raise SignalsValidationError(f"{what} end {end!r} is before start {start!r}")

    timezone = _require_type(doc, "timezone", str, what)
    prediction_horizon = _require_type(doc, "prediction_horizon", str, what)
    retrain_policy = _require_type(doc, "retrain_policy", str, what)
    train_window = _require_type(doc, "train_window_business_days", int, what)
    if train_window <= 0:
        raise SignalsValidationError(
            f"{what}['train_window_business_days'] must be positive, got {train_window!r}"
        )

    raw_files = _require_type(doc, "files", list, what)
    if not raw_files:
        raise SignalsValidationError(f"{what}['files'] must not be empty")
    seen_dates: set[str] = set()
    files: list[str] = []
    for i, name in enumerate(raw_files):
        if not isinstance(name, str):
            raise SignalsValidationError(
                f"{what}['files'][{i}] must be str, got {type(name).__name__}"
            )
        m = _FILE_RE.match(name)
        if m is None:
            raise SignalsValidationError(
                f"{what}['files'][{i}] must match 'signals_YYYY-MM-DD.json', got {name!r}"
            )
        d = m.group(1)
        _require_iso_date(d, f"{what}['files'][{i}] date")
        if not (start <= d <= end):
            raise SignalsValidationError(
                f"{what}['files'][{i}] date {d} outside range [{start}, {end}]"
            )
        if d in seen_dates:
            raise SignalsValidationError(f"{what}['files'] has duplicate date: {d}")
        seen_dates.add(d)
        files.append(name)

    raw_instruments = _require_type(doc, "instruments", list, what)
    if not raw_instruments:
        raise SignalsValidationError(f"{what}['instruments'] must not be empty")
    instruments: list[str] = []
    seen_inst: set[str] = set()
    for i, sym in enumerate(raw_instruments):
        if not isinstance(sym, str) or not sym or "." not in sym:
            raise SignalsValidationError(
                f"{what}['instruments'][{i}] must look like '<code>.<venue>', got {sym!r}"
            )
        if sym in seen_inst:
            raise SignalsValidationError(f"{what}['instruments'] duplicated: {sym!r}")
        seen_inst.add(sym)
        instruments.append(sym)

    regulation_filter = _require_type(doc, "regulation_filter", dict, what)
    unknown = regulation_filter.keys() - _REGULATION_KEYS
    if unknown:
        raise SignalsValidationError(
            f"{what}['regulation_filter'] has unknown keys: {sorted(unknown)}"
        )

    return SignalsManifest(
        schema_version=SCHEMA_VERSION,
        start=start,
        end=end,
        timezone=timezone,
        prediction_horizon=prediction_horizon,
        retrain_policy=retrain_policy,
        train_window_business_days=train_window,
        files=tuple(files),
        instruments=tuple(instruments),
        regulation_filter=dict(regulation_filter),
        base_dir=manifest_path.parent,
    )


# ---------------------------------------------------------------------------
# validate_manifest_consistency
# ---------------------------------------------------------------------------


def validate_manifest_consistency(manifest: SignalsManifest) -> None:
    """manifest が指す各日次ファイルを読み、manifest との整合を検証する。

    - 各 `files[]` が実在し、ロード可能で、filename の date と target_date が一致。
    - 各日次 signals の symbol 集合が `manifest.instruments` の和集合に含まれる。

    Raises:
        SignalsValidationError: いずれかの整合性が破れた場合。
    """
    universe = set(manifest.instruments)
    for target_date, name in manifest.date_to_file().items():
        daily = load_daily_signals(manifest.base_dir / name)
        if daily.target_date != target_date:
            raise SignalsValidationError(
                f"{name}: target_date {daily.target_date!r} != filename date {target_date!r}"
            )
        unknown = daily.symbols() - universe
        if unknown:
            raise SignalsValidationError(
                f"{name}: signals reference symbols not in manifest.instruments: "
                f"{sorted(unknown)}"
            )
