#!/usr/bin/env bash
# stage_fixtures.sh — copy the S0 spike fixture bars out of the shared artifacts
# catalog into python/spike/fixtures/ (issue #2).
#
# The shared catalog is already standard-precision (8-byte) parquet, so this is a
# plain rsync — no precision conversion. Idempotent: rerun any time, only changed
# files are recopied. The destination is git-ignored.
#
# Override the source root with ARTIFACTS_PATH if your artifacts live elsewhere.
set -euo pipefail

ARTIFACTS_PATH="${ARTIFACTS_PATH:-/Users/sasac/SynologyDrive/StockData/artifacts}"
SRC_BAR_DIR="${ARTIFACTS_PATH}/jquants-catalog/data/bar"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DST_BAR_DIR="${SCRIPT_DIR}/fixtures/jquants-catalog/data/bar"

SYMBOLS=(8918 6740 3823)
GRANULARITY="TSE-1-DAY-LAST-EXTERNAL"

mkdir -p "${DST_BAR_DIR}"

for sym in "${SYMBOLS[@]}"; do
  src="${SRC_BAR_DIR}/${sym}.${GRANULARITY}"
  dst="${DST_BAR_DIR}/${sym}.${GRANULARITY}"
  if [[ ! -d "${src}" ]]; then
    echo "ERROR: source fixture not found: ${src}" >&2
    exit 1
  fi

  src_count=$(find "${src}" -name '*.parquet' -type f | wc -l | tr -d ' ')
  if [[ "${src_count}" -eq 0 ]]; then
    echo "ERROR: no parquet under source fixture: ${src} (unmounted volume / typo?)" >&2
    exit 1
  fi

  mkdir -p "${dst}"
  rsync -a "${src}/" "${dst}/"

  dst_count=$(find "${dst}" -name '*.parquet' -type f | wc -l | tr -d ' ')
  if [[ "${dst_count}" -eq 0 ]]; then
    echo "ERROR: no parquet copied to ${dst}" >&2
    exit 1
  fi

  echo "staged ${sym}.${GRANULARITY} (${dst_count} parquet)"
done

echo "S0 fixtures staged under ${DST_BAR_DIR}"
