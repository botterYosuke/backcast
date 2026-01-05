/* Copyright 2026 Marimo. All rights reserved. */

import type { Grid3DConfig } from "./types";
import type { GridLayout } from "../grid-layout/types";

/**
 * Grid3DConfigからGridLayoutへの変換ヘルパー関数
 * 
 * セル配置情報（cells, scrollableCells, cellSide）は既存のGridLayoutから取得し、
 * 設定値（columns, rowHeight, maxWidth, bordered）はGrid3DConfigから取得してマージします。
 * 
 * @param config Grid3DConfig
 * @param baseLayout 既存のGridLayout（セル配置情報を含む）。省略時は空のセル配置情報を使用
 * @returns マージされたGridLayout
 */
export function convertGrid3DConfigToLayout(
  config: Grid3DConfig,
  baseLayout?: GridLayout,
): GridLayout {
  // ベースとなるGridLayoutを取得（セル配置情報を含む）
  const base = baseLayout || {
    columns: config.columns,
    rowHeight: config.rowHeight,
    maxWidth: config.maxWidth,
    bordered: config.bordered,
    cells: [],
    scrollableCells: new Set(),
    cellSide: new Map(),
  };

  // Grid3DConfigの設定値を適用し、セル配置情報は保持
  return {
    ...base,
    columns: config.columns,
    rowHeight: config.rowHeight,
    maxWidth: config.maxWidth,
    bordered: config.bordered,
    // セル配置情報は既存のものを保持
    cells: base.cells,
    scrollableCells: base.scrollableCells,
    cellSide: base.cellSide,
  };
}

