/* Copyright 2026 Marimo. All rights reserved. */

import * as THREE from "three";
import { CellCSS2DService } from "./cell-css2d-service";

/**
 * CellDragManager
 *
 * セルのドラッグ処理を管理
 * - ドラッグ開始/終了の処理
 * - マウス移動時の位置更新
 * - CSS2D空間での座標変換（スケール考慮）
 * - ドラッグ中のセル位置の更新
 */
export class CellDragManager {
  private activeCellId: string | null = null;
  private isDragging = false;
  private dragStartX = 0;
  private dragStartY = 0;
  private cellStartPosition = new THREE.Vector3();
  private rafId: number | null = null;
  private pendingPosition: THREE.Vector3 | null = null;
  private onPositionUpdate?: (cellId: string, position: THREE.Vector3) => void;
  private css2DService?: CellCSS2DService;
  private currentScale: number = 1.0;

  /**
   * 位置更新コールバックを設定します
   */
  setPositionUpdateCallback(
    callback: (cellId: string, position: THREE.Vector3) => void,
  ): void {
    this.onPositionUpdate = callback;
  }

  /**
   * CSS2DServiceへの参照を設定します
   */
  setCSS2DService(service: CellCSS2DService): void {
    this.css2DService = service;
  }

  /**
   * ドラッグを開始します
   */
  startDrag(
    event: MouseEvent,
    cellId: string,
    currentPosition: THREE.Vector3,
    scale: number = 1.0,
  ): void {
    event.preventDefault();
    event.stopPropagation();

    this.activeCellId = cellId;
    this.isDragging = true;
    this.dragStartX = event.clientX;
    this.dragStartY = event.clientY;
    this.cellStartPosition.copy(currentPosition);
    this.pendingPosition = currentPosition.clone();
    // スケールを保存（後で使用）
    this.currentScale = scale;

    // グローバルイベントリスナーを追加
    document.addEventListener("mousemove", this.onMouseMove);
    document.addEventListener("mouseup", this.onMouseUp);
  }

  /**
   * マウス移動時の処理
   */
  private onMouseMove = (event: MouseEvent): void => {
    if (!this.isDragging || !this.activeCellId) {
      return;
    }

    // requestAnimationFrameでスムーズに更新
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
    }

    this.rafId = requestAnimationFrame(() => {
      if (!this.isDragging || !this.activeCellId) {
        return;
      }

      const deltaX = event.clientX - this.dragStartX;
      const deltaY = event.clientY - this.dragStartY;

      // スケールを動的に取得（カメラ移動時のスケール変更に対応）
      if (this.css2DService) {
        const newScale = this.css2DService.getCurrentScale();
        this.currentScale = newScale;
      }

      // スケールを考慮した位置計算
      // 画面座標のdeltaを3D空間座標に変換するため、スケールで割る
      const adjustedDeltaX = this.currentScale > 0 ? deltaX / this.currentScale : deltaX;
      const adjustedDeltaY = this.currentScale > 0 ? deltaY / this.currentScale : deltaY;

      // 新しい位置を計算
      // カメラのupベクトルが(0, 0, -1)なので、Z軸負方向が上方向
      // マウスを下に動かす（deltaY > 0）→ Z軸を正方向に移動（z + adjustedDeltaY）
      // マウスを上に動かす（deltaY < 0）→ Z軸を負方向に移動（z + adjustedDeltaY、負の値なので減る）
      const newPosition = new THREE.Vector3(
        this.cellStartPosition.x + adjustedDeltaX,
        this.cellStartPosition.y,
        this.cellStartPosition.z + adjustedDeltaY,
      );

      this.pendingPosition = newPosition;

      // 位置を更新（コールバック経由）
      if (this.onPositionUpdate && this.activeCellId) {
        this.onPositionUpdate(this.activeCellId, newPosition);
      }

      this.rafId = null;
    });
  };

  /**
   * マウスアップ時の処理
   */
  private onMouseUp = (): void => {
    if (!this.isDragging || !this.activeCellId) {
      return;
    }

    const finalPosition = this.pendingPosition;
    const cellId = this.activeCellId;

    this.isDragging = false;
    this.activeCellId = null;
    this.pendingPosition = null;

    // イベントリスナーを削除
    document.removeEventListener("mousemove", this.onMouseMove);
    document.removeEventListener("mouseup", this.onMouseUp);

    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }

    // 最終位置を保存
    if (finalPosition && this.onPositionUpdate) {
      this.onPositionUpdate(cellId, finalPosition);
    }
  };

  /**
   * セル位置を更新します（スケールを考慮）
   * 注意: このメソッドは現在使用されていません。
   * 位置更新はコールバック経由で行われます。
   */
  updateCellPosition(
    cellId: string,
    position: THREE.Vector3,
    css2DService: CellCSS2DService,
  ): void {
    // 位置を更新（CSS2DService経由）
    css2DService.updateCellPosition(cellId, position);
  }

  /**
   * ドラッグ中かどうかを確認します
   */
  isDraggingCell(cellId: string): boolean {
    return this.isDragging && this.activeCellId === cellId;
  }

  /**
   * ドラッグ中のセルIDを取得します
   */
  getActiveCellId(): string | null {
    return this.activeCellId;
  }

  /**
   * リソースをクリーンアップします
   */
  dispose(): void {
    if (this.isDragging) {
      document.removeEventListener("mousemove", this.onMouseMove);
      document.removeEventListener("mouseup", this.onMouseUp);
    }

    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }

    this.activeCellId = null;
    this.isDragging = false;
    this.pendingPosition = null;
    this.onPositionUpdate = undefined;
  }
}

