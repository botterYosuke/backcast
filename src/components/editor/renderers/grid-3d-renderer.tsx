/* Copyright 2026 Marimo. All rights reserved. */

import { createPortal } from "react-dom";
import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { CSS2DObject } from "three/examples/jsm/renderers/CSS2DRenderer.js";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import { SceneManager } from "@/core/three/scene-manager";
import { GridLayoutRenderer } from "./grid-layout/grid-layout";
import type { GridLayout } from "./grid-layout/types";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import type { CellData, CellRuntimeState } from "@/core/cells/types";

interface Grid3DRendererProps {
  mode: AppMode;
  userConfig: UserConfig;
  appConfig: AppConfig;
  sceneManager: SceneManager;
  css2DService: CellCSS2DService;
  layout: GridLayout;
  setLayout: (layout: GridLayout) => void;
  cells: (CellRuntimeState & CellData)[];
}

/**
 * Grid3DRenderer
 *
 * Gridレイアウトを3D空間に配置するコンポーネント
 * - GridLayoutRendererをCSS2DRendererで表示
 * - 1つのCSS2DObjectとしてGrid全体を表示
 * - セルを追加するロジックは含めない（GridLayoutRendererが内部で処理）
 */
export const Grid3DRenderer: React.FC<Grid3DRendererProps> = ({
  mode,
  appConfig,
  sceneManager,
  css2DService,
  layout,
  setLayout,
  cells,
}) => {
  const [gridContainer, setGridContainer] = useState<HTMLDivElement | null>(null);
  const gridCSS2DObjectRef = useRef<CSS2DObject | null>(null);

  // Gridコンテナを作成
  useEffect(() => {
    const container = document.createElement("div");
    container.className = "grid-3d-container";
    container.style.position = "absolute";
    container.style.top = "0";
    container.style.left = "0";
    container.style.width = "100%";
    container.style.height = "100%";
    container.style.pointerEvents = "none";

    // 子要素のpointer-eventsを有効化
    const style = document.createElement("style");
    style.textContent = `
      .grid-3d-container > * {
        pointer-events: all;
      }
    `;
    document.head.appendChild(style);

    setGridContainer(container);

    return () => {
      if (style.parentElement) {
        style.parentElement.removeChild(style);
      }
    };
  }, []);

  // Gridコンテナを3D空間に配置
  useEffect(() => {
    if (!gridContainer) {
      return;
    }

    const scene = sceneManager.getScene();
    if (!scene) {
      return;
    }

    // 既存のCSS2DObjectを削除
    if (gridCSS2DObjectRef.current && gridCSS2DObjectRef.current.parent) {
      scene.remove(gridCSS2DObjectRef.current);
    }

    // CSS2DObjectを作成
    const css2DObject = new CSS2DObject(gridContainer);
    css2DObject.position.set(0, 0, 0);
    css2DObject.scale.set(1, 1, 1);

    // シーンに追加
    scene.add(css2DObject);
    gridCSS2DObjectRef.current = css2DObject;

    // レンダリングをマーク
    sceneManager.markNeedsRender();
    css2DService.markNeedsRender();

    // クリーンアップ
    return () => {
      if (gridCSS2DObjectRef.current && gridCSS2DObjectRef.current.parent) {
        scene.remove(gridCSS2DObjectRef.current);
        gridCSS2DObjectRef.current = null;
      }
    };
  }, [gridContainer, sceneManager, css2DService]);

  // Gridコンテナが準備できていない場合は何も表示しない
  if (!gridContainer) {
    return null;
  }

  // GridLayoutRendererをCSS2Dコンテナ内にレンダリング
  return createPortal(
    <GridLayoutRenderer
      appConfig={appConfig}
      mode={mode}
      cells={cells}
      layout={layout}
      setLayout={setLayout}
    />,
    gridContainer,
  );
};

