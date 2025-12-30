/* Copyright 2026 Marimo. All rights reserved. */

import * as THREE from "three";

/**
 * グリッド配置の設定
 */
export interface GridLayoutConfig {
  columns: number;
  spacing: THREE.Vector3;
  startPosition: THREE.Vector3;
}

/**
 * デフォルトのグリッド配置設定
 */
export const DEFAULT_GRID_CONFIG: GridLayoutConfig = {
  columns: 3,
  spacing: new THREE.Vector3(400, 0, 400), // セル間の間隔
  startPosition: new THREE.Vector3(0, 0, 0), // 開始位置
};

/**
 * セル数に基づいて最適な列数を計算します
 * 
 * @param cellCount セルの数
 * @returns 最適な列数
 */
export function calculateOptimalColumns(cellCount: number): number {
  if (cellCount <= 0) {
    return 1;
  }
  if (cellCount <= 4) {
    return cellCount;
  }
  // セル数が多い場合は、平方根に近い列数を返す
  return Math.ceil(Math.sqrt(cellCount));
}

/**
 * インデックスに基づいてグリッド位置を計算します
 * 
 * @param index セルのインデックス（0から開始）
 * @param config グリッド配置の設定
 * @returns 3D空間での位置
 */
export function calculateGridPosition(
  index: number,
  config: GridLayoutConfig,
): THREE.Vector3 {
  const row = Math.floor(index / config.columns);
  const col = index % config.columns;

  const position = new THREE.Vector3();
  position.x =
    config.startPosition.x +
    (col - (config.columns - 1) / 2) * config.spacing.x;
  position.y = config.startPosition.y;
  position.z =
    config.startPosition.z +
    (row - (Math.ceil(config.columns / 2) - 1)) * config.spacing.z;

  return position;
}


