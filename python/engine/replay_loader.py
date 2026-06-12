"""
ReplayLoader — pure data-loading helper extracted from DataEngine.load_replay_data().

Responsibilities:
  - Build NautilusBarsReplayProvider dict from a nautilus catalog (load_catalog_providers)
  - Build a single NautilusBarsReplayProvider via J-Quants  (load_jquants_provider)

Out of scope (stays in DataEngine):
  - _replay_state transitions
  - _prime_provider_locked()
  - threading / locking

This class holds no mutable state; construct a new instance per DataEngine.
"""

from __future__ import annotations

from typing import Optional

from .jquants_to_catalog import ensure_jquants_catalog, instrument_id_to_bar_type
from .nautilus_catalog_loader import (
    CatalogPrecisionMismatchError,
    _assert_catalog_writable_for_precision,
)
from .replay import NautilusBarsReplayProvider

_SUPPORTED_GRANULARITIES = ("Daily", "Minute")


class ReplayLoader:
    """Builds replay providers from catalog or J-Quants data.

    Does NOT mutate DataEngine state; callers (DataEngine.load_replay_data)
    are responsible for priming and state transitions.
    """

    def __init__(
        self,
        nautilus_catalog_path: Optional[str] = None,
        jquants_loader=None,
        jquants_catalog_path: Optional[str] = None,
    ) -> None:
        self._nautilus_catalog_path = nautilus_catalog_path
        self._jquants_loader = jquants_loader
        self._jquants_catalog_path = jquants_catalog_path

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def load_catalog_providers(
        self,
        instrument_ids: list[str],
        start_date: str = "",
        end_date: str = "",
        granularity: str = "Daily",
        catalog_path: Optional[str] = None,
    ) -> dict[str, NautilusBarsReplayProvider]:
        """Load NautilusBarsReplayProviders for each instrument from a Parquet catalog.

        Returns:
            {instrument_id: NautilusBarsReplayProvider}

        Raises:
            ValueError: on unsupported granularity, empty instrument_ids, or data not found.
            FileNotFoundError: when the catalog directory is missing.
            CatalogPrecisionMismatchError: on a precision build mismatch (GH #34).
        """
        if not instrument_ids:
            raise ValueError("At least one instrument_id is required")

        if granularity not in _SUPPORTED_GRANULARITIES:
            raise ValueError(
                f"Unsupported granularity for nautilus catalog: {granularity!r}. "
                f"Supported: {_SUPPORTED_GRANULARITIES}"
            )

        effective_path = catalog_path or self._nautilus_catalog_path
        if effective_path is None:
            raise ValueError("nautilus_catalog_path is not configured")

        providers: dict[str, NautilusBarsReplayProvider] = {}
        for iid in instrument_ids:
            bar_type = instrument_id_to_bar_type(iid, granularity)
            try:
                providers[iid] = NautilusBarsReplayProvider(
                    catalog_path=effective_path,
                    bar_type=bar_type,
                    start=start_date or None,
                    end=end_date or None,
                )
            except CatalogPrecisionMismatchError:
                # GH #34: never swallow a precision mismatch — propagate untouched.
                raise
            except (ValueError, FileNotFoundError):
                # Lazy: only resolve base_dir when we actually need the fallback.
                jquants_base_dir: Optional[str] = (
                    str(self._jquants_loader.base_dir) if self._jquants_loader else None
                )
                if not (jquants_base_dir and start_date and end_date):
                    raise
                # Catalog miss + jquants fallback: write & retry.
                _assert_catalog_writable_for_precision(effective_path)
                ensure_jquants_catalog(
                    base_dir=jquants_base_dir,
                    catalog_path=effective_path,
                    instrument_id=iid,
                    start_date=start_date,
                    end_date=end_date,
                    granularity=granularity,
                )
                providers[iid] = NautilusBarsReplayProvider(
                    catalog_path=effective_path,
                    bar_type=bar_type,
                    start=start_date or None,
                    end=end_date or None,
                )

        return providers

    def load_jquants_provider(
        self,
        instrument_id: str,
        start_date: str = "",
        end_date: str = "",
        granularity: str = "Daily",
    ) -> tuple[NautilusBarsReplayProvider, str]:
        """Load a single NautilusBarsReplayProvider via J-Quants.

        Returns:
            (provider, catalog_path_used)

        Raises:
            ValueError: on unsupported granularity or missing catalog path.
            FileNotFoundError: when J-Quants data is unavailable.
        """
        if granularity not in _SUPPORTED_GRANULARITIES:
            raise ValueError(
                f"Unsupported granularity for replay: {granularity!r}. "
                f"Supported: {_SUPPORTED_GRANULARITIES}"
            )

        if not self._jquants_catalog_path:
            raise ValueError("J-Quants catalog path is not configured")

        if self._jquants_loader is None:
            raise ValueError("jquants_loader is not configured")

        _assert_catalog_writable_for_precision(self._jquants_catalog_path)

        result = ensure_jquants_catalog(
            base_dir=self._jquants_loader.base_dir,
            catalog_path=self._jquants_catalog_path,
            instrument_id=instrument_id,
            start_date=start_date,
            end_date=end_date,
            granularity=granularity,
        )
        provider = NautilusBarsReplayProvider(
            catalog_path=result.catalog_path,
            bar_type=result.bar_type,
            start=None,
            end=None,
        )
        return provider, result.catalog_path
