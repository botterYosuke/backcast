/* Copyright 2026 Marimo. All rights reserved. */

import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

/**
 * SceneManager
 *
 * Three.jsシーン、カメラ、OrbitControlsの管理を担当
 * - シーン、カメラ、レンダラーの初期化
 * - OrbitControlsの設定
 * - アニメーションループ
 */
export class SceneManager {
  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private controls?: OrbitControls;
  private animationId?: number;
  private resizeHandler?: () => void;
  private hostElement?: HTMLDivElement;
  private lastRenderTime = 0;
  private needsRender = true;
  private isAnimating = false;
  private readonly MIN_FRAME_INTERVAL = 16; // 約60FPS
  private css2DRenderCallback?: (
    scene: THREE.Scene,
    camera: THREE.PerspectiveCamera,
  ) => void;

  /**
   * Three.jsシーンを初期化します
   */
  initialize(hostElement: HTMLDivElement): void {
    this.dispose();

    this.hostElement = hostElement;
    const width = hostElement.clientWidth;
    const height = hostElement.clientHeight;

    this.scene = new THREE.Scene();
    this.scene.background = null;

    this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 200000);
    this.camera.position.set(0, 1200, 0); // XZ平面を俯瞰するため上空に配置
    this.camera.lookAt(0, 0, 0); // カメラを原点（XZ平面）に向ける
    this.camera.up.set(0, 0, -1); // Z軸負方向を上として設定

    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(window.devicePixelRatio || 1);
    this.renderer.setSize(width, height);
    this.renderer.domElement.style.position = "absolute";
    this.renderer.domElement.style.top = "0";
    this.renderer.domElement.style.left = "0";
    this.renderer.domElement.style.zIndex = "0"; // WebGLRendererを最下層に配置
    hostElement.appendChild(this.renderer.domElement);

    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = false;
    this.controls.enableRotate = true; // 回転を有効化
    // 左クリックでパン
    this.controls.mouseButtons.LEFT = THREE.MOUSE.PAN;
    // ズーム制限を設定
    this.controls.minDistance = 100;
    this.controls.maxDistance = 50000;

    const ambientLight = new THREE.AmbientLight(0xffffff, 0.5);
    this.scene.add(ambientLight);

    const directionalLight = new THREE.DirectionalLight(0xffffff, 1);
    directionalLight.position.set(10, 20, 10);
    this.scene.add(directionalLight);

    this.resizeHandler = () => {
      if (!this.camera || !this.renderer || !this.hostElement) {
        return;
      }
      const { clientWidth, clientHeight } = this.hostElement;
      if (clientWidth === 0 || clientHeight === 0) {
        return;
      }
      this.camera.aspect = clientWidth / clientHeight;
      this.camera.updateProjectionMatrix();
      this.renderer.setSize(clientWidth, clientHeight);
    };

    window.addEventListener("resize", this.resizeHandler);

    this.startAnimationLoop();
  }

  /**
   * Three.jsのシーンを取得します
   */
  getScene(): THREE.Scene | undefined {
    return this.scene;
  }

  /**
   * カメラを取得します
   */
  getCamera(): THREE.PerspectiveCamera | undefined {
    return this.camera;
  }

  /**
   * OrbitControlsを取得します
   */
  getControls(): OrbitControls | undefined {
    return this.controls;
  }

  /**
   * ホストエレメントを取得します
   */
  getHostElement(): HTMLDivElement | undefined {
    return this.hostElement;
  }

  /**
   * CSS2Dレンダリングのコールバックを設定します
   */
  setCSS2DRenderCallback(
    callback: (scene: THREE.Scene, camera: THREE.PerspectiveCamera) => void,
  ): void {
    this.css2DRenderCallback = callback;
  }

  /**
   * レンダリングが必要であることをマークします
   */
  markNeedsRender(): void {
    this.needsRender = true;
  }

  /**
   * 初期化されているかチェックします
   */
  isReady(): boolean {
    return !!(this.renderer && this.scene && this.camera);
  }

  /**
   * アニメーションループを開始します
   */
  private startAnimationLoop(): void {
    const animate = (currentTime: number) => {
      this.animationId = requestAnimationFrame(animate);

      // OrbitControlsを更新
      if (this.controls) {
        this.controls.update();
      }

      // レンダリングの最適化：変更がある場合、アニメーション中の場合のみレンダリング
      const shouldRender =
        this.needsRender || this.isAnimating || this.controls?.enabled;

      if (shouldRender && this.renderer && this.scene && this.camera) {
        const elapsed = currentTime - this.lastRenderTime;

        // 最小フレーム間隔のチェック（約60FPS）
        if (elapsed > this.MIN_FRAME_INTERVAL) {
          this.renderer.render(this.scene, this.camera);

          // CSS2D レンダリング（WebGL レンダリングの後）
          if (this.css2DRenderCallback) {
            this.css2DRenderCallback(this.scene, this.camera);
          }

          this.lastRenderTime = currentTime;
          this.needsRender = false;
        }
      }
    };

    // OrbitControlsのイベント監視
    if (this.controls) {
      // startイベント: レンダリングフラグを設定
      this.controls.addEventListener("start", () => {
        this.needsRender = true;
        this.isAnimating = true;
      });

      // changeイベント: レンダリングフラグを設定
      this.controls.addEventListener("change", () => {
        this.needsRender = true;
        this.isAnimating = true;
      });

      // endイベント: アニメーションフラグをリセット（1000ms後）
      this.controls.addEventListener("end", () => {
        setTimeout(() => {
          this.isAnimating = false;
        }, 1000);
      });
    }

    animate(0);
  }

  /**
   * リソースをクリーンアップします
   */
  dispose(): void {
    const canvas = this.renderer?.domElement;
    if (canvas && canvas.parentElement) {
      canvas.parentElement.removeChild(canvas);
    }

    if (this.animationId) {
      cancelAnimationFrame(this.animationId);
      this.animationId = undefined;
    }

    if (this.scene) {
      // シーン内のすべてのオブジェクトを適切に破棄
      this.disposeScene(this.scene);
    }

    if (this.controls) {
      this.controls.dispose();
      this.controls = undefined;
    }

    if (this.renderer) {
      this.renderer.forceContextLoss();
      this.renderer.dispose();
      this.renderer = undefined;
    }

    if (this.resizeHandler) {
      window.removeEventListener("resize", this.resizeHandler);
      this.resizeHandler = undefined;
    }

    this.scene = undefined;
    this.camera = undefined;
    this.hostElement = undefined;
    this.css2DRenderCallback = undefined;
  }

  /**
   * シーン内のすべてのオブジェクトを適切に破棄します
   */
  private disposeScene(scene: THREE.Scene): void {
    scene.traverse((object) => {
      if (object instanceof THREE.Mesh) {
        // ジオメトリを破棄
        if (object.geometry) {
          object.geometry.dispose();
        }

        // マテリアルを破棄
        if (object.material) {
          if (Array.isArray(object.material)) {
            object.material.forEach((material) => this.disposeMaterial(material));
          } else {
            this.disposeMaterial(object.material);
          }
        }
      }
    });

    // シーンをクリア
    while (scene.children.length > 0) {
      scene.remove(scene.children[0]);
    }
  }

  /**
   * マテリアルとそのテクスチャを破棄します
   */
  private disposeMaterial(material: THREE.Material): void {
    material.dispose();

    // テクスチャを破棄
    Object.values(material).forEach((value) => {
      if (value instanceof THREE.Texture) {
        value.dispose();
      }
    });
  }
}
