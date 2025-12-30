/* Copyright 2026 Marimo. All rights reserved. */

import { useRef, useEffect } from "react";
import { Cell } from "@/components/editor/notebook-cell";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import type { CellId } from "@/core/cells/ids";
import type { Theme } from "@/theme/useTheme";
import { CellDragManager } from "@/core/three/cell-drag-manager";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import * as THREE from "three";
import { useCellData } from "@/core/cells/cells";
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
  const cellName = cellData?.name || cellId;

  // タイトルバーのドラッグ開始処理
  const handleTitleBarMouseDown = (event: React.MouseEvent) => {
    const target = event.target as HTMLElement;
    // ボタンがクリックされた場合はドラッグを開始しない
    if (target.tagName === "BUTTON" || target.closest(".titlebar-btn")) {
      return;
    }

    // 現在のセル位置を取得
    const css2DObject = css2DService.getCellCSS2DObject(cellId);
    if (css2DObject) {
      const currentPosition = css2DObject.position.clone();
      const scale = css2DService.getCurrentScale();
      dragManager.startDrag(event.nativeEvent, cellId, currentPosition, scale);
    }
  };

  // ラッパー要素が準備できたらコールバックを呼び出す
  useEffect(() => {
    if (wrapperRef.current && onCellElementReady) {
      // wrapperElement全体を渡す（cell-3d-wrapper要素）
      onCellElementReady(cellId, wrapperRef.current);
    }
  }, [cellId, onCellElementReady]);

  return (
    <div
      ref={wrapperRef}
      className="cell-3d-wrapper floating-window"
      data-cell-wrapper-id={cellId}
    >
      {/* タイトルバー */}
      <div
        className="window-titlebar"
        onMouseDown={handleTitleBarMouseDown}
        style={{ cursor: "grab" }}
      >
        <div className="titlebar-left">
          <span className="window-title">{cellName}</span>
        </div>
        <div className="titlebar-buttons">
          {/* 将来的に最小化/閉じるボタンを追加可能 */}
        </div>
      </div>

      {/* セルコンテンツ */}
      <div className="window-content">
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

