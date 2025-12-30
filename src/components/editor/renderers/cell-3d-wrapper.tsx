/* Copyright 2026 Marimo. All rights reserved. */

import { useRef, useEffect, useState } from "react";
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
  const cellName = cellData?.name || cellId;
  // #region agent log
  fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:57',message:'cellName calculation',data:{cellId,hasCellData:!!cellData,cellDataName:cellData?.name,cellName},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'F'})}).catch(()=>{});
  // #endregion
  const [isDragging, setIsDragging] = useState(false);

  // タイトルバーのドラッグ開始処理
  const handleTitleBarMouseDown = (event: React.MouseEvent) => {
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:61',message:'handleTitleBarMouseDown called',data:{cellId,targetTag:event.target?.tagName},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
    // #endregion
    const target = event.target as HTMLElement;
    // ボタンがクリックされた場合はドラッグを開始しない
    if (target.tagName === "BUTTON" || target.closest(".titlebar-btn")) {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:64',message:'drag prevented by button click',data:{cellId},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
      // #endregion
      return;
    }

    // 現在のセル位置を取得
    const css2DObject = css2DService.getCellCSS2DObject(cellId);
    if (css2DObject) {
      const currentPosition = css2DObject.position.clone();
      const scale = css2DService.getCurrentScale();
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:72',message:'calling dragManager.startDrag',data:{cellId,scale,hasCss2DObject:!!css2DObject},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
      // #endregion
      dragManager.startDrag(event.nativeEvent, cellId, currentPosition, scale);
      setIsDragging(true);
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:74',message:'setIsDragging(true) called',data:{cellId},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
      // #endregion
    } else {
      // #region agent log
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:69',message:'css2DObject not found',data:{cellId},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
      // #endregion
    }
  };

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
      // #region agent log
      const titlebar = wrapperRef.current?.querySelector('.window-titlebar');
      const computedStyle = titlebar ? window.getComputedStyle(titlebar) : null;
      const parentComputedStyle = wrapperRef.current ? window.getComputedStyle(wrapperRef.current) : null;
      const containerInner = wrapperRef.current?.closest('.cells-3d-container-inner');
      const container = wrapperRef.current?.closest('.cells-3d-container');
      fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:99',message:'wrapper element ready - detailed pointer-events check',data:{cellId,hasWrapper:!!wrapperRef.current,hasTitlebar:!!titlebar,titlebarPointerEvents:computedStyle?.pointerEvents || 'not set',wrapperPointerEvents:parentComputedStyle?.pointerEvents || 'not set',hasContainerInner:!!containerInner,hasContainer:!!container,containerPointerEvents:container ? window.getComputedStyle(container).pointerEvents : 'not found'},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
      // #endregion
    }
  }, [cellId, onCellElementReady]);

  // #region agent log
  const classNameResult = cn(
    "cell-3d-wrapper floating-window",
    isDragging && "dragging"
  );
  fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:105',message:'rendering with className',data:{cellId,isDragging,className:classNameResult,hasDraggingClass:classNameResult.includes('dragging')},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'C'})}).catch(()=>{});
  // #endregion
  return (
    <div
      ref={wrapperRef}
      className={classNameResult}
      data-cell-wrapper-id={cellId}
      onMouseDown={(e) => {
        // #region agent log
        fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:110',message:'wrapper onMouseDown event fired',data:{cellId,target: e.target?.tagName,currentTarget: e.currentTarget?.tagName},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
        // #endregion
      }}
      style={{ pointerEvents: "all" }}
    >
      {/* タイトルバー */}
      <div
        className="window-titlebar"
        onMouseDown={(e) => {
          // #region agent log
          fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:112',message:'titlebar onMouseDown event fired',data:{cellId,target: e.target?.tagName,currentTarget: e.currentTarget?.tagName,pointerEvents:window.getComputedStyle(e.currentTarget as HTMLElement).pointerEvents},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
          // #endregion
          handleTitleBarMouseDown(e);
        }}
        onClick={(e) => {
          // #region agent log
          fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:115',message:'titlebar onClick event fired',data:{cellId},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
          // #endregion
        }}
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
          // #region agent log
          fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'cell-3d-wrapper.tsx:126',message:'window-content onMouseDown event fired',data:{cellId},timestamp:Date.now(),sessionId:'debug-session',runId:'run1',hypothesisId:'D'})}).catch(()=>{});
          // #endregion
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

