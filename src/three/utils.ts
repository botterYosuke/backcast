/* Copyright 2026 Marimo. All rights reserved. */

import * as THREE from "three";

/**
 * グリッド配置の設定
 */
export interface GridLayoutConfig {
  /** グリッドの列数 */
  columns: number;
  /** セル間の間隔（X方向） */
  spacingX: number;
  /** セル間の間隔（Z方向） */
  spacingZ: number;
  /** 開始位置のオフセット */
  startOffset: THREE.Vector3;
}

/**
 * デフォルトのグリッド配置設定
 */
export const DEFAULT_GRID_CONFIG: GridLayoutConfig = {
  columns: 3,
  spacingX: 400,
  spacingZ: 300,
  startOffset: new THREE.Vector3(0, 0, 0),
};

/**
 * セルをグリッド状に配置する位置を計算します
 *
 * @param index セルのインデックス（0始まり）
 * @param config グリッド配置の設定
 * @returns 3D空間での位置
 */
export function calculateGridPosition(
  index: number,
  config: GridLayoutConfig = DEFAULT_GRID_CONFIG,
): THREE.Vector3 {
  const row = Math.floor(index / config.columns);
  const col = index % config.columns;

  const x = col * config.spacingX - (config.columns - 1) * config.spacingX * 0.5;
  const z = row * config.spacingZ;

  const position = new THREE.Vector3(
    config.startOffset.x + x,
    config.startOffset.y,
    config.startOffset.z + z,
  );

  return position;
}

/**
 * セル数を基に最適なグリッド列数を計算します
 *
 * @param cellCount セルの数
 * @returns 最適な列数
 */
export function calculateOptimalColumns(cellCount: number): number {
  if (cellCount <= 0) {
    return 1;
  }
  // セル数に応じて列数を調整
  if (cellCount <= 3) {
    return cellCount;
  }
  if (cellCount <= 6) {
    return 3;
  }
  if (cellCount <= 12) {
    return 4;
  }
  // それ以上は5列
  return 5;
}
