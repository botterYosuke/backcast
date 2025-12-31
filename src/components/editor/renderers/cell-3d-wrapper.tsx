/* Copyright 2026 Marimo. All rights reserved. */

import { useRef, useEffect, useState, useCallback } from "react";
import { Cell } from "@/components/editor/notebook-cell";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import type { CellId } from "@/core/cells/ids";
import type { Theme } from "@/theme/useTheme";
import { CellDragManager } from "@/core/three/cell-drag-manager";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import * as THREE from "three";
import { useCellData } from "@/core/cells/cells";
import { cn } from "@/utils/cn";
import "./cell-3d-wrapper.css";

interface Cell3DWrapperProps {
  cellId: CellId;
  mode: AppMode;
  userConfig: UserConfig;
  appConfig: AppConfig;
  theme: Theme;
  dragManager: CellDragManager;
  css2DService: CellCSS2DService;
  showPlaceholder: boolean;
  canDelete: boolean;
  isCollapsed: boolean;
  collapseCount: number;
  canMoveX: boolean;
  onCellElementReady?: (cellId: CellId, element: HTMLElement) => void;
}

/**
 * Cell3DWrapper
 *
 * セルをタイトルバー付きでラップするコンポーネント
 * - タイトルバーの表示（セル名またはID）
 * - ドラッグハンドルの実装
 * - セルコンテンツの表示
 */
export const Cell3DWrapper: React.FC<Cell3DWrapperProps> = ({
  cellId,
  mode,
  userConfig,
  appConfig,
  theme,
  dragManager,
  css2DService,
  showPlaceholder,
  canDelete,
  isCollapsed,
  collapseCount,
  canMoveX,
  onCellElementReady,
}) => {
  const wrapperRef = useRef<HTMLDivElement>(null);
  const cellData = useCellData(cellId);
  // セル名が "_" の場合はセルIDを表示
  const cellName = (cellData?.name && cellData.name !== "_") ? cellData.name : cellId;
  const [isDragging, setIsDragging] = useState(false);

  // タイトルバーのドラッグ開始処理
  const handleTitleBarMouseDown = useCallback((event: React.MouseEvent) => {
    const target = event.target as HTMLElement;
    // ボタンがクリックされた場合はドラッグを開始しない
    if (target.tagName === "BUTTON" || target.closest(".titlebar-btn")) {
      return;
    }

    // セル要素のCSSスタイルから現在位置を取得
    const wrapperElement = wrapperRef.current;
    if (wrapperElement) {
      const left = parseFloat(wrapperElement.style.left) || 0;
      const top = parseFloat(wrapperElement.style.top) || 0;
      // CSS座標を3D座標に変換（コンテナ位置を基準に）
      const containerPosition =
        css2DService.getContainerPosition() || new THREE.Vector3(0, 200, 0);
      const currentPosition = new THREE.Vector3(
        containerPosition.x + left,
        containerPosition.y,
        containerPosition.z + top,
      );
      const scale = css2DService.getCurrentScale();
      dragManager.startDrag(event.nativeEvent, cellId, currentPosition, scale);
      setIsDragging(true);
    }
  }, [cellId, css2DService, dragManager]);

  // ドラッグ終了を監視
  useEffect(() => {
    if (!isDragging) {
      return;
    }

    const handleMouseUp = () => {
      setIsDragging(false);
    };

    document.addEventListener("mouseup", handleMouseUp);
    return () => {
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, [isDragging]);

  // ラッパー要素が準備できたらコールバックを呼び出す
  useEffect(() => {
    if (wrapperRef.current && onCellElementReady) {
      // wrapperElement全体を渡す（cell-3d-wrapper要素）
      onCellElementReady(cellId, wrapperRef.current);
      
      // タイトルバーにネイティブイベントリスナーを直接追加（Reactイベントが発火しない場合のフォールバック）
      const titlebar = wrapperRef.current?.querySelector('.window-titlebar');
      if (titlebar) {
        const nativeMouseDownHandler = (e: MouseEvent) => {
          // Reactイベントハンドラーを手動で呼び出す
          const syntheticEvent = {
            ...e,
            nativeEvent: e,
            currentTarget: titlebar,
            target: e.target,
            preventDefault: () => e.preventDefault(),
            stopPropagation: () => e.stopPropagation(),
          } as unknown as React.MouseEvent;
          handleTitleBarMouseDown(syntheticEvent);
        };
        titlebar.addEventListener('mousedown', nativeMouseDownHandler);
        return () => {
          titlebar.removeEventListener('mousedown', nativeMouseDownHandler);
        };
      }
    }
  }, [cellId, onCellElementReady, handleTitleBarMouseDown]);

  return (
    <div
      ref={wrapperRef}
      className={cn(
        "cell-3d-wrapper floating-window",
        isDragging && "dragging"
      )}
      data-cell-wrapper-id={cellId}
      onMouseDown={() => {}}
      style={{ pointerEvents: "all" }}
    >
      {/* タイトルバー */}
      <div
        className="window-titlebar"
        onMouseDown={handleTitleBarMouseDown}
        style={{ cursor: "grab", pointerEvents: "all" }}
      >
        <div className="titlebar-left">
          <span className="window-title">{cellName}</span>
        </div>
        <div className="titlebar-buttons">
          {/* 将来的に最小化/閉じるボタンを追加可能 */}
        </div>
      </div>

      {/* セルコンテンツ */}
      <div 
        className="window-content"
        onMouseDown={(e) => {
          e.stopPropagation();
        }}
      >
        <Cell
          cellId={cellId}
          theme={theme}
          showPlaceholder={showPlaceholder}
          canDelete={canDelete}
          mode={mode}
          userConfig={userConfig}
          isCollapsed={isCollapsed}
          collapseCount={collapseCount}
          canMoveX={canMoveX}
          disableSortable={true}
        />
      </div>
    </div>
  );
};

