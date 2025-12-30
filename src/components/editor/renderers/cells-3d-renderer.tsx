/* Copyright 2026 Marimo. All rights reserved. */

import { createPortal } from "react-dom";
import { useEffect, useRef, useState, useMemo } from "react";
import * as THREE from "three";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import { SceneManager } from "@/core/three/scene-manager";
import { CellDragManager } from "@/core/three/cell-drag-manager";
import { Cell3DWrapper } from "./cell-3d-wrapper";
import {
  calculateGridPosition,
  calculateOptimalColumns,
  DEFAULT_GRID_CONFIG,
  type GridLayoutConfig,
} from "@/core/three/utils";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import { useCellIds } from "@/core/cells/cells";
import { useTheme } from "@/theme/useTheme";
import { SETUP_CELL_ID, type CellId } from "@/core/cells/ids";
import { SortableCellsProvider } from "@/components/sort/SortableCellsProvider";

interface Cells3DRendererProps {
  mode: AppMode;
  userConfig: UserConfig;
  appConfig: AppConfig;
  sceneManager: SceneManager;
  css2DService: CellCSS2DService;
}

/**
 * Cells3DRenderer
 *
 * セルを3D空間に配置するコンポーネント
 * - 各セルを個別のCSS2DObjectとして3D空間に配置
 * - グリッド配置アルゴリズム（初期配置のみ）
 * - セルの追加/削除時の位置更新
 * - ドラッグ機能の統合
 */
export const Cells3DRenderer: React.FC<Cells3DRendererProps> = ({
  mode,
  userConfig,
  appConfig,
  sceneManager,
  css2DService,
}) => {
  const cellIds = useCellIds();
  const { theme } = useTheme();
  const [cellContainer, setCellContainer] = useState<HTMLDivElement | null>(null);
  const cellWrapperElementsRef = useRef<Map<string, HTMLElement>>(new Map());
  const dragManagerRef = useRef<CellDragManager | null>(null);
  const cellPositionsRef = useRef<Map<string, THREE.Vector3>>(new Map());

  // セルIDのリストを取得（フラット化、SETUP_CELL_IDを除外）
  const allCellIds = useMemo(() => {
    return cellIds.inOrderIds.filter((id) => id !== SETUP_CELL_ID);
  }, [cellIds]);

  // CellDragManagerの初期化
  useEffect(() => {
    const dragManager = new CellDragManager();
    dragManager.setPositionUpdateCallback((cellId, position) => {
      // 位置を更新（スケールはCellDragManager内で既に考慮されている）
      css2DService.updateCellPosition(cellId, position);
      // 位置を保存
      cellPositionsRef.current.set(cellId, position);
    });
    dragManager.setCSS2DService(css2DService);
    dragManagerRef.current = dragManager;

    return () => {
      dragManager.dispose();
    };
  }, [css2DService]);

  // セルコンテナを作成
  useEffect(() => {
    // CSS2DServiceの既存コンテナを使用
    const existingContainer = css2DService.getCellContainer();
    if (existingContainer) {
      setCellContainer(existingContainer);
      return;
    }

    // コンテナが存在しない場合は新規作成
    const container = document.createElement("div");
    container.className = "cells-3d-container";
    container.style.position = "absolute";
    container.style.top = "0";
    container.style.left = "0";
    container.style.width = "0";
    container.style.height = "0";
    container.style.pointerEvents = "none";
    container.style.zIndex = "100";

    // 子要素のpointer-eventsを有効化
    const style = document.createElement("style");
    style.textContent = `
      .cells-3d-container > * {
        pointer-events: all;
      }
    `;
    document.head.appendChild(style);

    setCellContainer(container);

    return () => {
      if (style.parentElement) {
        style.parentElement.removeChild(style);
      }
    };
  }, [css2DService]);

  // セルを3D空間に配置
  useEffect(() => {
    if (!cellContainer) {
      return;
    }

    const scene = sceneManager.getScene();
    if (!scene) {
      return;
    }

    const dragManager = dragManagerRef.current;
    if (!dragManager) {
      return;
    }

    // シーンにコンテナを接続（既存のメソッドを使用）
    css2DService.attachCellContainerToScene(scene, new THREE.Vector3(0, 0, 0));

    // グリッド配置の設定
    const cellCount = allCellIds.length;
    const columns = calculateOptimalColumns(cellCount);
    const gridConfig: GridLayoutConfig = {
      ...DEFAULT_GRID_CONFIG,
      columns,
    };

    // 各セルのラッパー要素を取得してCSS2DObjectとして配置
    const updatePositions = () => {
      allCellIds.forEach((cellId, index) => {
        // ラッパー要素を検索
        const wrapperElement = cellContainer.querySelector(
          `[data-cell-wrapper-id="${cellId}"]`,
        ) as HTMLElement;

        if (!wrapperElement) {
          return; // ラッパー要素が見つからない場合はスキップ
        }

        // 既存の位置を取得、またはグリッド位置を計算
        let position = cellPositionsRef.current.get(cellId);
        if (!position) {
          // 初期配置：グリッド位置を計算
          position = calculateGridPosition(index, gridConfig);
          cellPositionsRef.current.set(cellId, position);
        }

        // CSS2DObjectを作成または更新
        const existingObj = css2DService.getCellCSS2DObject(cellId);
        if (existingObj) {
          // 既存のオブジェクトの位置を更新（ドラッグで移動した場合）
          existingObj.position.copy(position);
        } else {
          // 新しいCSS2DObjectを作成
          css2DService.addCellCSS2DObject(cellId, wrapperElement, position);
        }

        cellWrapperElementsRef.current.set(cellId, wrapperElement);
      });

      // 削除されたセルのCSS2DObjectを削除
      const currentCellIds = new Set(allCellIds);
      const allCellCSS2DObjects = css2DService.getAllCellCSS2DObjects();
      allCellCSS2DObjects.forEach((_, cellId) => {
        if (!currentCellIds.has(cellId as CellId)) {
          css2DService.removeCellCSS2DObject(cellId);
          cellPositionsRef.current.delete(cellId);
          cellWrapperElementsRef.current.delete(cellId);
        }
      });

      // レンダリングをマーク
      sceneManager.markNeedsRender();
      css2DService.markNeedsRender();
    };

    // 初期配置
    updatePositions();

    // MutationObserverを使用してセル要素の変更を監視
    const observer = new MutationObserver(() => {
      // 少し遅延させてDOMの更新を待つ
      setTimeout(updatePositions, 0);
    });

    observer.observe(cellContainer, {
      childList: true,
      subtree: true,
    });

    // クリーンアップ
    return () => {
      observer.disconnect();
      // すべてのCSS2DObjectを削除
      allCellIds.forEach((cellId) => {
        css2DService.removeCellCSS2DObject(cellId);
      });
      cellPositionsRef.current.clear();
      cellWrapperElementsRef.current.clear();
    };
  }, [allCellIds, cellContainer, sceneManager, css2DService]);

  // セルラッパー要素が準備できたときのコールバック
  const handleCellElementReady = (cellId: CellId, element: HTMLElement) => {
    // 要素が準備できたことを記録
    cellWrapperElementsRef.current.set(cellId, element);

    // 位置が設定されていない場合は、グリッド位置を計算
    if (!cellPositionsRef.current.has(cellId)) {
      const index = allCellIds.indexOf(cellId);
      if (index >= 0) {
        const cellCount = allCellIds.length;
        const columns = calculateOptimalColumns(cellCount);
        const gridConfig: GridLayoutConfig = {
          ...DEFAULT_GRID_CONFIG,
          columns,
        };
        const position = calculateGridPosition(index, gridConfig);
        cellPositionsRef.current.set(cellId, position);

        // CSS2DObjectを作成
        const scene = sceneManager.getScene();
        if (scene) {
          css2DService.addCellCSS2DObject(cellId, element, position);
          sceneManager.markNeedsRender();
          css2DService.markNeedsRender();
        }
      }
    }
  };

  // セルをCSS2Dコンテナ内にレンダリング
  if (!cellContainer) {
    return null;
  }

  const dragManager = dragManagerRef.current;
  if (!dragManager) {
    return null;
  }

  // セルの列情報を取得
  const hasOnlyOneCell = cellIds.hasOnlyOneId();

  return createPortal(
    <SortableCellsProvider multiColumn={appConfig.width === "columns"}>
      <div className="cells-3d-container-inner">
        {allCellIds.map((cellId) => {
          const column = cellIds.findWithId(cellId);
          const isCollapsed = column ? column.isCollapsed(cellId) : false;
          const collapseCount = column ? column.getCount(cellId) : 0;

          return (
            <Cell3DWrapper
              key={cellId}
              cellId={cellId}
              mode={mode}
              userConfig={userConfig}
              appConfig={appConfig}
              theme={theme}
              dragManager={dragManager}
              css2DService={css2DService}
              showPlaceholder={hasOnlyOneCell}
              canDelete={!hasOnlyOneCell}
              isCollapsed={isCollapsed}
              collapseCount={collapseCount}
              canMoveX={appConfig.width === "columns"}
              onCellElementReady={handleCellElementReady}
            />
          );
        })}
      </div>
    </SortableCellsProvider>,
    cellContainer,
  );
};
