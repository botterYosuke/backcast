/* Copyright 2026 Marimo. All rights reserved. */

import { createPortal } from "react-dom";
import { useEffect, useRef, useState, useMemo, useCallback } from "react";
import * as THREE from "three";
import { useAtomValue, useSetAtom } from "jotai";
import type { CellCSS2DService } from "@/core/three/cell-css2d-service";
import type { SceneManager } from "@/core/three/scene-manager";
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
import {
  cell3DPositionsAtom,
  type Cell3DPosition,
} from "@/core/three/cell-3d-positions";

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
 * - コンテナ全体を1つのCSS2DObjectとして3D空間に配置
 * - 個別セルはコンテナ内にCSS座標で配置
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
  const cell3DPositions = useAtomValue(cell3DPositionsAtom);
  const cell3DPositionsRef = useRef(cell3DPositions);
  const setCell3DPositions = useSetAtom(cell3DPositionsAtom);

  // atomが更新されたらrefも更新
  useEffect(() => {
    cell3DPositionsRef.current = cell3DPositions;
  }, [cell3DPositions]);

  // セルIDのリストを取得（フラット化、SETUP_CELL_IDを除外）
  const allCellIds = useMemo(() => {
    return cellIds.inOrderIds.filter((id) => id !== SETUP_CELL_ID);
  }, [cellIds]);

  // CellDragManagerの初期化
  useEffect(() => {
    const dragManager = new CellDragManager();
    dragManager.setPositionUpdateCallback((cellId, position) => {
      // 位置を保存
      cellPositionsRef.current.set(cellId, position);

      // atomにも保存
      setCell3DPositions((prev) => {
        const next = new Map(prev);
        next.set(cellId as CellId, {
          x: position.x,
          y: position.y,
          z: position.z,
        });
        return next;
      });

      // セル要素のCSSスタイルを更新
      const wrapperElement = cellWrapperElementsRef.current.get(cellId);
      if (wrapperElement) {
        const containerPosition =
          css2DService.getContainerPosition() ||
          new THREE.Vector3(0, 200, 0);
        const relativePosition = new THREE.Vector3(
          position.x - containerPosition.x,
          position.y - containerPosition.y,
          position.z - containerPosition.z,
        );
        wrapperElement.style.left = `${relativePosition.x}px`;
        wrapperElement.style.top = `${relativePosition.z}px`;
      }

      // レンダリングをマーク
      sceneManager.markNeedsRender();
      css2DService.markNeedsRender();
    });
    dragManager.setCSS2DService(css2DService);
    dragManagerRef.current = dragManager;

    return () => {
      dragManager.dispose();
    };
  }, [css2DService, sceneManager, setCell3DPositions]);

  // セルコンテナを取得
  useEffect(() => {
    const existingContainer = css2DService.getCellContainer();
    if (existingContainer) {
      setCellContainer(existingContainer);
    } else {
      console.warn(
        "Cell container is not available. Make sure initializeRenderer() is called first.",
      );
    }
  }, [css2DService]);

  // コンテナをシーンにアタッチ
  useEffect(() => {
    if (!cellContainer) {
      return;
    }

    const scene = sceneManager.getScene();
    if (!scene) {
      return;
    }

    // 既にアタッチされている場合はスキップ
    if (!css2DService.getCSS2DObject()) {
      css2DService.attachCellContainerToScene(scene, new THREE.Vector3(0, 200, 0));
    }
  }, [cellContainer, sceneManager, css2DService]);

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

    // シーンを設定
    css2DService.setScene(scene);

    // グリッド配置の設定
    const cellCount = allCellIds.length;
    const columns = calculateOptimalColumns(cellCount);
    const gridConfig: GridLayoutConfig = {
      ...DEFAULT_GRID_CONFIG,
      columns,
    };

    // コンテナの3D位置を取得
    const containerPosition =
      css2DService.getContainerPosition() || new THREE.Vector3(0, 200, 0);

    // 各セルのラッパー要素を取得してCSS座標で配置
    const updatePositions = () => {
      allCellIds.forEach((cellId, index) => {
        // ラッパー要素を検索
        const wrapperElement = cellContainer.querySelector(
          `[data-cell-wrapper-id="${cellId}"]`,
        ) as HTMLElement;

        if (!wrapperElement) {
          return; // ラッパー要素が見つからない場合はスキップ
        }

        // 既存の位置を取得、またはatomから復元、またはグリッド位置を計算
        let position = cellPositionsRef.current.get(cellId);
        if (!position) {
          // atomから位置情報を復元を試みる（最新値を参照）
          const savedPosition = cell3DPositionsRef.current.get(cellId);
          if (savedPosition) {
            // atomに位置があれば、THREE.Vector3に変換して使用
            position = new THREE.Vector3(
              savedPosition.x,
              savedPosition.y,
              savedPosition.z,
            );
            cellPositionsRef.current.set(cellId, position);
          } else {
            // 初期配置：グリッド位置を計算
            position = calculateGridPosition(index, gridConfig);
            position.y = 200; // Y座標を200に設定
            cellPositionsRef.current.set(cellId, position);
          }
        } else {
          // 既存の位置のY座標を200に設定（atomから復元した場合は変更しない）
          position.y = 200;
        }

        // コンテナ位置を基準に相対位置を計算
        const relativePosition = new THREE.Vector3(
          position.x - containerPosition.x,
          position.y - containerPosition.y,
          position.z - containerPosition.z,
        );

        // CSS座標で位置を設定
        wrapperElement.style.left = `${relativePosition.x}px`;
        wrapperElement.style.top = `${relativePosition.z}px`;

        cellWrapperElementsRef.current.set(cellId, wrapperElement);
      });

      // 削除されたセルの位置情報をクリーンアップ
      const currentCellIds = new Set(allCellIds);
      const deletedCellIds: CellId[] = [];
      cellPositionsRef.current.forEach((_, cellId) => {
        if (!currentCellIds.has(cellId as CellId)) {
          cellPositionsRef.current.delete(cellId);
          cellWrapperElementsRef.current.delete(cellId);
          deletedCellIds.push(cellId as CellId);
        }
      });
      // atomからも削除
      if (deletedCellIds.length > 0) {
        setCell3DPositions((prev) => {
          const next = new Map(prev);
          deletedCellIds.forEach((cellId) => {
            next.delete(cellId);
          });
          return next;
        });
      }

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
      cellPositionsRef.current.clear();
      cellWrapperElementsRef.current.clear();
    };
  }, [allCellIds, cellContainer, sceneManager, css2DService, setCell3DPositions]);

  // セルラッパー要素が準備できたときのコールバック
  const handleCellElementReady = useCallback(
    (cellId: CellId, element: HTMLElement) => {
      // 要素が準備できたことを記録
      cellWrapperElementsRef.current.set(cellId, element);

      // 位置が設定されていない場合は、atomから復元を試みる、またはグリッド位置を計算
      if (!cellPositionsRef.current.has(cellId)) {
        // atomから位置情報を復元を試みる
        const savedPosition = cell3DPositions.get(cellId);
        let position: THREE.Vector3;

        if (savedPosition) {
          // atomに位置があれば、THREE.Vector3に変換して使用
          position = new THREE.Vector3(
            savedPosition.x,
            savedPosition.y,
            savedPosition.z,
          );
          cellPositionsRef.current.set(cellId, position);
        } else {
          // 初期配置：グリッド位置を計算
          const index = allCellIds.indexOf(cellId);
          if (index >= 0) {
            const cellCount = allCellIds.length;
            const columns = calculateOptimalColumns(cellCount);
            const gridConfig: GridLayoutConfig = {
              ...DEFAULT_GRID_CONFIG,
              columns,
            };
            position = calculateGridPosition(index, gridConfig);
            position.y = 200; // Y座標を200に設定
            cellPositionsRef.current.set(cellId, position);
          } else {
            return;
          }
        }

        // コンテナ位置を基準に相対位置を計算
        const containerPosition =
          css2DService.getContainerPosition() || new THREE.Vector3(0, 200, 0);
        const relativePosition = new THREE.Vector3(
          position.x - containerPosition.x,
          position.y - containerPosition.y,
          position.z - containerPosition.z,
        );

        // CSS座標で位置を設定
        element.style.left = `${relativePosition.x}px`;
        element.style.top = `${relativePosition.z}px`;

        sceneManager.markNeedsRender();
        css2DService.markNeedsRender();
      }
    },
    [cell3DPositions, allCellIds, css2DService, sceneManager],
  );

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
