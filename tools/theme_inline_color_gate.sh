#!/usr/bin/env bash
# theme_inline_color_gate.sh — issue #44 "theme（配色）システム" (static AC#3 gate)
#
# Fails if any themed surface still hard-codes an RGBA color literal (`new Color(...)` /
# `new Color32(...)`) instead of reading ThemeService.Current. Complements the runtime
# ThemeProbe, which can only catch surfaces it actually builds (findings 0020 Q9).
#
# SCOPE (findings 0020):
#   TARGET   = 層1 production builders + 層2 theme-consuming chart/ladder harnesses (below).
#   ALLOWED  = theme/scale definition files (Assets/Scripts/Theme/* — the literal home) and
#              層3 harness debug chrome (other harnesses, out of scope) — simply not listed.
#
# Usage:  bash tools/theme_inline_color_gate.sh   # exit 0 = clean, 1 = inline literal found

set -u
cd "$(dirname "$0")/.." || exit 2

TARGETS=(
  "Assets/Scripts/ScenarioStartup/ScenarioStartupTile.cs"
  "Assets/Scripts/StrategyEditor/PythonSyntaxMeshEffect.cs"
  "Assets/Scripts/StrategyEditor/StrategyEditorContentBuilder.cs"
  "Assets/Scripts/FloatingWindow/FloatingWindowCatalog.cs"
  "Assets/Scripts/ScenarioStartup/ScenarioStartupHitlHarness.cs"
  "Assets/Scripts/ReplayChart/ReplayChartHarness.cs"
  "Assets/Scripts/ReplayChart/ReplayPanelsHarness.cs"
  "Assets/Scripts/LiveSpike/DepthLadderHitlHarness.cs"
)

# RGBA color literals: `new Color(` and `new Color32(`. Named statics (Color.clear/gray) are
# not palette smells and are left out.
PATTERN='new[[:space:]]+Color(32)?[[:space:]]*\('

found=0
for f in "${TARGETS[@]}"; do
  if [ ! -f "$f" ]; then
    echo "GATE ERROR: target missing: $f"
    found=1
    continue
  fi
  hits=$(grep -nE "$PATTERN" "$f")
  if [ -n "$hits" ]; then
    echo "INLINE COLOR LITERAL in $f:"
    echo "$hits"
    found=1
  fi
done

if [ "$found" -eq 0 ]; then
  echo "[THEME GATE PASS] no inline color literals in themed surfaces"
  exit 0
else
  echo "[THEME GATE FAIL] themed surfaces must read ThemeService.Current (issue #44 AC#3)"
  exit 1
fi
