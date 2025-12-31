/* Copyright 2026 Marimo. All rights reserved. */

import { CSS2DRenderer, CSS2DObject } from "three/examples/jsm/renderers/CSS2DRenderer.js";
import * as THREE from "three";

/**
 * CellCSS2DService
 *
 * CSS2DRendererの初期化と管理を担当
 * - CSS2DRendererの初期化
 * - セルコンテナDOM要素の作成と管理
 * - 個別セルのCSS2DObject管理
 * - レンダリングループの管理
 */
export class CellCSS2DService {
  private css2DRenderer?: CSS2DRenderer;
  private hostElement?: HTMLElement;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private cellContainer?: HTMLDivElement;
  private isContainerVisible = true;

  // セルごとのCSS2DObject管理
  private cellCSS2DObjects = new Map<string, CSS2DObject>();

  // レンダリング最適化用
  private needsRender = false;
  private isInteracting = false;
  private lastCameraPosition = new THREE.Vector3();
  private readonly CAMERA_MOVE_THRESHOLD = 0.1; // 最小移動量

  /**
   * CSS2DRendererとセルコンテナを初期化します
   */
  initializeRenderer(hostElement: HTMLElement, width: number, height: number): CSS2DRenderer {
    this.dispose();

    this.hostElement = hostElement;
    this.css2DRenderer = new CSS2DRenderer();
    this.css2DRenderer.setSize(width, height);

    // CSS2DRendererのスタイル設定
    const rendererElement = this.css2DRenderer.domElement;
    rendererElement.style.position = "absolute";
    rendererElement.style.top = "0";
    rendererElement.style.left = "0";
    rendererElement.style.pointerEvents = "none";
    rendererElement.style.zIndex = "10";

    hostElement.appendChild(rendererElement);

    // セルコンテナを作成
    this.createCellContainer();
    this.applyContainerVisibility();

    return this.css2DRenderer;
  }

  /**
   * セルコンテナDOM要素を作成します
   */
  private createCellContainer(): HTMLDivElement {
    if (this.cellContainer) {
      this.applyContainerVisibility();
      return this.cellContainer;
    }

    const container = document.createElement("div");
    container.className = "cells-3d-container";

    // CSSスタイルをインラインで設定
    container.style.position = "absolute";
    container.style.top = "0";
    container.style.left = "0";
    container.style.width = "0";
    container.style.height = "0";
    container.style.pointerEvents = "none";
    container.style.zIndex = "100";

    // 子要素のpointer-eventsを有効化するためのスタイルを追加
    const style = document.createElement("style");
    style.textContent = `
      .cells-3d-container > * {
        pointer-events: all;
      }
    `;
    document.head.appendChild(style);

    this.cellContainer = container;
    this.applyContainerVisibility();

    return container;
  }

  /**
   * セルコンテナを取得します
   */
  getCellContainer(): HTMLDivElement | undefined {
    return this.cellContainer;
  }

  /**
   * シーンを設定します
   */
  setScene(scene: THREE.Scene): void {
    this.scene = scene;
  }

  /**
   * CSS2Dシーンをレンダリングします
   */
  render(
    scene: THREE.Scene,
    camera: THREE.PerspectiveCamera,
  ): void {
    if (!this.css2DRenderer) {
      return;
    }

    this.scene = scene;
    this.camera = camera;

    // カメラが移動した場合も再レンダリング
    const cameraMoved =
      camera.position.distanceTo(this.lastCameraPosition) >
      this.CAMERA_MOVE_THRESHOLD;

    // レンダリング条件：変更がある、操作中、またはカメラが移動した場合のみ
    if (!this.needsRender && !this.isInteracting && !cameraMoved) {
      return; // スキップ
    }

    // CSS2DRendererのrender()を実行
    this.css2DRenderer.render(scene, camera);

    this.lastCameraPosition.copy(camera.position);
    this.needsRender = false;
  }

  /**
   * レンダリングが必要であることをマークします
   */
  markNeedsRender(): void {
    this.needsRender = true;
  }

  /**
   * インタラクション状態を設定します
   */
  setInteracting(isInteracting: boolean): void {
    this.isInteracting = isInteracting;
    if (isInteracting) {
      this.needsRender = true;
    }
  }

  /**
   * レンダラーのサイズを変更します
   */
  setSize(width: number, height: number): void {
    if (this.css2DRenderer) {
      this.css2DRenderer.setSize(width, height);
    }
  }

  /**
   * CSS2DRendererを取得します
   */
  getRenderer(): CSS2DRenderer | undefined {
    return this.css2DRenderer;
  }

  /**
   * 現在のスケール値を取得します
   * 注意: 個別セルにはスケール調整が適用されないため、常に1.0を返します
   */
  getCurrentScale(): number {
    return 1.0;
  }

  /**
   * セルコンテナを非表示にします
   */
  hideCellContainer(): void {
    this.isContainerVisible = false;
    this.applyContainerVisibility();
  }

  /**
   * セルコンテナを表示します
   */
  showCellContainer(): void {
    this.isContainerVisible = true;
    this.applyContainerVisibility();
  }

  /**
   * セルコンテナの表示状態を反映します
   */
  private applyContainerVisibility(): void {
    const displayValue = this.isContainerVisible ? "" : "none";
    if (this.cellContainer) {
      this.cellContainer.style.display = displayValue;
    }
  }

  /**
   * リソースをクリーンアップします
   */
  dispose(): void {
    // すべてのセルCSS2DObjectを削除
    this.cellCSS2DObjects.forEach((obj, cellId) => {
      if (obj.parent) {
        obj.parent.remove(obj);
      }
    });
    this.cellCSS2DObjects.clear();

    // セルコンテナを削除
    if (this.cellContainer) {
      // コンテナ内の子要素をすべて削除
      while (this.cellContainer.firstChild) {
        this.cellContainer.removeChild(this.cellContainer.firstChild);
      }
      // コンテナ自体を削除
      if (this.cellContainer.parentElement) {
        this.cellContainer.parentElement.removeChild(this.cellContainer);
      }
      this.cellContainer = undefined;
    }

    // CSS2DRendererのDOMを削除
    if (this.css2DRenderer) {
      const element = this.css2DRenderer.domElement;
      if (element && element.parentElement) {
        element.parentElement.removeChild(element);
      }
      this.css2DRenderer = undefined;
    }

    this.hostElement = undefined;
    this.scene = undefined;
    this.camera = undefined;

    // レンダリング状態をリセット
    this.needsRender = false;
    this.isInteracting = false;
    this.lastCameraPosition = new THREE.Vector3();
  }

  /**
   * CSS2Dレンダリングが初期化されているかチェックします
   */
  isInitialized(): boolean {
    return !!this.css2DRenderer;
  }

  /**
   * セルごとのCSS2DObjectを追加します
   */
  addCellCSS2DObject(cellId: string, element: HTMLElement, position: THREE.Vector3): CSS2DObject | null {
    if (!this.scene) {
      console.warn("Scene is not set. Call setScene() first.");
      return null;
    }

    // 既存のオブジェクトを削除
    this.removeCellCSS2DObject(cellId);

    // CSS2DObjectを作成
    const css2DObject = new CSS2DObject(element);
    css2DObject.position.copy(position);
    css2DObject.scale.set(1, 1, 1);

    // シーンに追加
    this.scene.add(css2DObject);
    this.cellCSS2DObjects.set(cellId, css2DObject);

    return css2DObject;
  }

  /**
   * セルごとのCSS2DObjectを削除します
   */
  removeCellCSS2DObject(cellId: string): void {
    const css2DObject = this.cellCSS2DObjects.get(cellId);
    if (css2DObject && css2DObject.parent) {
      css2DObject.parent.remove(css2DObject);
    }
    this.cellCSS2DObjects.delete(cellId);
  }

  /**
   * セルごとのCSS2DObjectを取得します
   */
  getCellCSS2DObject(cellId: string): CSS2DObject | undefined {
    return this.cellCSS2DObjects.get(cellId);
  }

  /**
   * セル位置を更新します
   */
  updateCellPosition(cellId: string, position: THREE.Vector3): void {
    const css2DObject = this.cellCSS2DObjects.get(cellId);
    if (css2DObject) {
      css2DObject.position.copy(position);
      this.markNeedsRender();
    }
  }

  /**
   * すべてのセルCSS2DObjectを取得します
   */
  getAllCellCSS2DObjects(): Map<string, CSS2DObject> {
    return this.cellCSS2DObjects;
  }
}

