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
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-drag-manager.ts:39',message:'setCSS2DService called',data:{hasService:!!service},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
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
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-drag-manager.ts:46',message:'startDrag called',data:{cellId,scale,hasCss2DService:!!this.css2DService},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
    // #endregion
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
      // #region agent log
      const hasService = !!this.css2DService;
      // #endregion
      if (this.css2DService) {
        const newScale = this.css2DService.getCurrentScale();
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-drag-manager.ts:92',message:'scale updated in onMouseMove',data:{oldScale:this.currentScale,newScale,hasService},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
        // #endregion
        this.currentScale = newScale;
      } else {
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-drag-manager.ts:92',message:'css2DService is undefined in onMouseMove',data:{hasService},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'B'})}).catch(()=>{});
        // #endregion
      }

      // スケールを考慮した位置計算
      // 画面座標のdeltaを3D空間座標に変換するため、スケールで割る
      const adjustedDeltaX = this.currentScale > 0 ? deltaX / this.currentScale : deltaX;
      const adjustedDeltaY = this.currentScale > 0 ? deltaY / this.currentScale : deltaY;

      // 新しい位置を計算
      const newPosition = new THREE.Vector3(
        this.cellStartPosition.x + adjustedDeltaX,
        this.cellStartPosition.y,
        this.cellStartPosition.z - adjustedDeltaY, // Y軸は逆（画面の上方向がZ軸の負方向）
      );

      this.pendingPosition = newPosition;

      // 位置を更新（コールバック経由）
      if (this.onPositionUpdate && this.activeCellId) {
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-drag-manager.ts:110',message:'position update callback called',data:{cellId:this.activeCellId,position:{x:newPosition.x,y:newPosition.y,z:newPosition.z},scale:this.currentScale},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'E'})}).catch(()=>{});
        // #endregion
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

