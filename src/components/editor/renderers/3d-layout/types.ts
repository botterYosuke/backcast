/* Copyright 2026 Marimo. All rights reserved. */

/**
 * 3Dモード用のグリッド設定
 * 既存のGridControlsの設定項目を全て含み、さらに3Dモード専用の設定項目を追加
 */
export interface Grid3DConfig {
  // 既存の設定項目（GridLayoutから）
  /** 列数 */
  columns: number;
  /** 行数 */
  rows?: number;
  /** 行の高さ（px） */
  rowHeight: number;
  /** 最大幅（px） */
  maxWidth?: number;
  /** ボーダー表示 */
  bordered: boolean;
  /** グリッドをロックするか */
  isLocked: boolean;

  // 3Dモード専用の追加設定項目
  /** X方向の間隔 */
  spacingX: number;
  /** Z方向の間隔（Grid Depth） */
  spacingZ: number;
  /** Y方向の高さ（Grid Height） */
  spacingY: number;
  /** グリッド平面の透明度（0-1） */
  gridOpacity: number;
  /** グリッドにスナップするか */
  snapToGrid: boolean;
}

/**
 * デフォルトの3Dグリッド設定
 */
export const DEFAULT_GRID_3D_CONFIG: Grid3DConfig = {
  // 既存の設定項目のデフォルト値
  columns: 12,
  rows: undefined,
  rowHeight: 20,
  maxWidth: undefined,
  bordered: false,
  isLocked: false,

  // 3Dモード専用の設定項目のデフォルト値
  spacingX: 400,
  spacingZ: 300,
  spacingY: 0,
  gridOpacity: 0.3,
  snapToGrid: false,
};

