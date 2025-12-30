/* Copyright 2026 Marimo. All rights reserved. */

import { createPortal } from "react-dom";
import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { CSS2DObject } from "three/examples/jsm/renderers/CSS2DRenderer.js";
import { CellArray } from "./cell-array";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import { SceneManager } from "@/core/three/scene-manager";
import {
  calculateGridPosition,
  calculateOptimalColumns,
  DEFAULT_GRID_CONFIG,
  type GridLayoutConfig,
} from "@/core/three/utils";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import { useCellIds } from "@/core/cells/cells";

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
 * - セル要素をCSS2DObjectとして3D空間に配置
 * - グリッド配置アルゴリズム
 * - セルの追加/削除時の位置更新
 */
export const Cells3DRenderer: React.FC<Cells3DRendererProps> = ({
  mode,
  userConfig,
  appConfig,
  sceneManager,
  css2DService,
}) => {
  const cellIds = useCellIds();
  const [cellContainer, setCellContainer] = useState<HTMLDivElement | null>(null);
  const cellElementsRef = useRef<Map<string, HTMLElement>>(new Map());
  const css2DObjectsRef = useRef<Map<string, CSS2DObject>>(new Map());

  // セルIDのリストを取得（フラット化）
  const allCellIds = cellIds.inOrderIds;

  // セルコンテナを取得
  useEffect(() => {
    const container = css2DService.getCellContainer();
    if (container) {
      setCellContainer(container);
    }
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

    // グリッド配置の設定
    const cellCount = allCellIds.length;
    const columns = calculateOptimalColumns(cellCount);
    const gridConfig: GridLayoutConfig = {
      ...DEFAULT_GRID_CONFIG,
      columns,
    };

    // 既存のCSS2DObjectを削除
    css2DObjectsRef.current.forEach((obj) => {
      if (obj.parent) {
        obj.parent.remove(obj);
      }
    });
    css2DObjectsRef.current.clear();

    // 各セルのDOM要素を取得してCSS2DObjectとして配置
    const updatePositions = () => {
      allCellIds.forEach((cellId, index) => {
        // セル要素を検索
        const element = cellContainer.querySelector(
          `[data-cell-id="${cellId}"]`,
        ) as HTMLElement;

        if (!element) {
          return; // セル要素が見つからない場合はスキップ
        }

        // 既存のCSS2DObjectがある場合は削除
        const existingObj = css2DObjectsRef.current.get(cellId);
        if (existingObj && existingObj.parent) {
          existingObj.parent.remove(existingObj);
        }

        // グリッド位置を計算
        const position = calculateGridPosition(index, gridConfig);

        // CSS2DObjectを作成
        const css2DObject = new CSS2DObject(element);
        css2DObject.position.copy(position);

        // シーンに追加
        scene.add(css2DObject);
        css2DObjectsRef.current.set(cellId, css2DObject);
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
      css2DObjectsRef.current.forEach((obj) => {
        if (obj.parent) {
          obj.parent.remove(obj);
        }
      });
      css2DObjectsRef.current.clear();
    };
  }, [allCellIds, cellContainer, sceneManager, css2DService]);

  // セルをCSS2Dコンテナ内にレンダリング
  if (!cellContainer) {
    return null;
  }

  return createPortal(
    <CellArray mode={mode} userConfig={userConfig} appConfig={appConfig} />,
    cellContainer,
  );
};
