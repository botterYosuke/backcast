/* Copyright 2026 Marimo. All rights reserved. */

import { useRef, useEffect, useState, useCallback } from "react";
import { useAtomValue } from "jotai";
import { MoreHorizontalIcon, XIcon } from "lucide-react";
import { Cell } from "@/components/editor/notebook-cell";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import type { CellId } from "@/core/cells/ids";
import type { Theme } from "@/theme/useTheme";
import { CellDragManager } from "@/core/three/cell-drag-manager";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import * as THREE from "three";
import {
  useCellData,
  useCellIds,
  useCellRuntime,
  useCellHandle,
} from "@/core/cells/cells";
import { displayCellName } from "@/core/cells/names";
import { isOutputEmpty } from "@/core/cells/outputs";
import { connectionAtom } from "@/core/network/connection";
import { RunButton } from "@/components/editor/cell/RunButton";
import { StopButton } from "@/components/editor/cell/StopButton";
import { CellActionsDropdown } from "@/components/editor/cell/cell-actions";
import { useRunCell } from "@/components/editor/cell/useRunCells";
import { useDeleteCellCallback } from "@/components/editor/cell/useDeleteCell";
import { ToolbarItem } from "@/components/editor/cell/toolbar";
import { isAppConnected } from "@/core/websocket/connection-utils";
import { Functions } from "@/utils/functions";
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
  const cellIds = useCellIds();
  const cellIndex = cellIds.inOrderIds.indexOf(cellId);
  const cellName = displayCellName(cellData?.name ?? "_", cellIndex);
  const [isDragging, setIsDragging] = useState(false);

  // Hooks for cell runtime and actions
  const cellRuntime = useCellRuntime(cellId);
  const cellHandle = useCellHandle(cellId);
  const runCell = useRunCell(cellId);
  const deleteCell = useDeleteCellCallback();
  const connection = useAtomValue(connectionAtom);

  // Calculate values for buttons
  const disabledOrAncestorDisabled =
    cellData?.config.disabled || cellRuntime.status === "disabled-transitively";
  const needsRun =
    cellData?.edited ||
    cellRuntime.interrupted ||
    (cellRuntime.staleInputs && !disabledOrAncestorDisabled);
  const hasOutput = !isOutputEmpty(cellRuntime.output);
  const hasConsoleOutput = cellRuntime.consoleOutputs.length > 0;
  const getEditorView = useCallback(() => {
    return cellHandle.current?.editorView ?? null;
  }, []);

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
        const nativeMouseDownHandler = (e: Event) => {
          // Reactイベントハンドラーを手動で呼び出す
          const mouseEvent = e as MouseEvent;
          const syntheticEvent = {
            ...mouseEvent,
            nativeEvent: mouseEvent,
            currentTarget: titlebar,
            target: mouseEvent.target,
            preventDefault: () => mouseEvent.preventDefault(),
            stopPropagation: () => mouseEvent.stopPropagation(),
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
          <RunButton
            edited={cellData?.edited ?? false}
            onClick={isAppConnected(connection.state) ? runCell : Functions.NOOP}
            connectionState={connection.state}
            status={cellRuntime.status}
            config={cellData?.config}
            needsRun={needsRun}
          />
          <StopButton
            status={cellRuntime.status}
            connectionState={connection.state}
          />
          <CellActionsDropdown
            cellId={cellId}
            status={cellRuntime.status}
            getEditorView={getEditorView}
            name={cellData?.name}
            config={cellData?.config}
            hasOutput={hasOutput}
            hasConsoleOutput={hasConsoleOutput}
          >
            <ToolbarItem
              variant={"green"}
              tooltip={null}
              data-testid="cell-actions-button"
            >
              <MoreHorizontalIcon strokeWidth={1.5} />
            </ToolbarItem>
          </CellActionsDropdown>
          <button
            className="titlebar-btn close"
            onClick={() => deleteCell({ cellId })}
            type="button"
          >
            <XIcon size={14} />
          </button>
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

