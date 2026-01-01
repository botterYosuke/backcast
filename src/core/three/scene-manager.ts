/* Copyright 2026 Marimo. All rights reserved. */

import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

/**
 * SceneManager
 * 
 * Three.jsシーン、カメラ、OrbitControlsの管理を担当
 */
export class SceneManager {
  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private controls?: OrbitControls;
  private animationId?: number;
  private resizeHandler?: () => void;
  private hostElement?: HTMLDivElement;
  private needsRender = true;
  private readonly MIN_FRAME_INTERVAL = 16; // 約60FPS
  private lastRenderTime = 0;
  private css2DRenderCallback?: (
    scene: THREE.Scene,
    camera: THREE.PerspectiveCamera,
  ) => void;

  /**
   * Three.jsシーンを初期化します
   * 
   * @param hostElement レンダラーを配置する親要素
   */
  initialize(hostElement: HTMLDivElement): void {
    this.dispose();

    this.hostElement = hostElement;
    const width = hostElement.clientWidth;
    const height = hostElement.clientHeight;

    // シーンの作成
    this.scene = new THREE.Scene();
    this.scene.background = null;

    // カメラの作成
    this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 200000);
    this.camera.position.set(0, 1200, 0); // XZ平面を俯瞰するため上空に配置
    this.camera.lookAt(0, 0, 0); // カメラを原点（XZ平面）に向ける
    this.camera.up.set(0, 0, -1); // Z軸負方向を上として設定

    // レンダラーの作成
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(window.devicePixelRatio || 1);
    this.renderer.setSize(width, height);
    this.renderer.domElement.style.position = "absolute";
    this.renderer.domElement.style.top = "0";
    this.renderer.domElement.style.left = "0";
    this.renderer.domElement.style.zIndex = "0";
    hostElement.appendChild(this.renderer.domElement);

    // OrbitControlsの作成
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = false;
    this.controls.enableRotate = false;
    // 左クリックにも pan を割り当て
    this.controls.mouseButtons.LEFT = THREE.MOUSE.PAN;
    // ズーム制限を設定
    this.controls.minDistance = 100;
    this.controls.maxDistance = 50000;
    // パン制限を設定
    this.controls.enablePan = true;

    // ライトの追加
    const ambientLight = new THREE.AmbientLight(0xffffff, 0.5);
    this.scene.add(ambientLight);

    const directionalLight = new THREE.DirectionalLight(0xffffff, 1);
    directionalLight.position.set(10, 20, 10);
    this.scene.add(directionalLight);

    // リサイズハンドラーの設定
    this.resizeHandler = () => {
      if (
        !this.camera ||
        !this.renderer ||
        !this.hostElement
      ) {
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

    // アニメーションループの開始
    this.startAnimationLoop();
  }

  /**
   * アニメーションループを開始します
   */
  private startAnimationLoop(): void {
    const animate = (currentTime: number) => {
      this.animationId = requestAnimationFrame(animate);

      // フレームレート制限
      const elapsed = currentTime - this.lastRenderTime;
      if (elapsed < this.MIN_FRAME_INTERVAL && !this.needsRender) {
        return;
      }
      this.lastRenderTime = currentTime;

      if (!this.scene || !this.camera || !this.renderer) {
        return;
      }

      // OrbitControlsの更新
      if (this.controls) {
        this.controls.update();
        // 操作中はレンダリングが必要
        // ダンピングが有効な場合、update()が内部で動きを検知するため
        // 常にレンダリングが必要になる可能性がある
        if (this.controls.enabled) {
          this.needsRender = true;
        }
      }

      // レンダリング
      if (this.needsRender) {
        this.renderer.render(this.scene, this.camera);
        // CSS2Dレンダリングのコールバックを実行
        if (this.css2DRenderCallback) {
          this.css2DRenderCallback(this.scene, this.camera);
        }
        this.needsRender = false;
      }
    };

    animate(0);
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
   * レンダラーを取得します
   */
  getRenderer(): THREE.WebGLRenderer | undefined {
    return this.renderer;
  }

  /**
   * OrbitControlsを取得します
   */
  getControls(): OrbitControls | undefined {
    return this.controls;
  }

  /**
   * カメラの視点を設定します
   * 
   * @param position カメラの位置
   * @param target OrbitControlsのtarget（カメラが向いている方向）
   */
  setCameraView(position: THREE.Vector3, target: THREE.Vector3): void {
    if (!this.camera || !this.controls) {
      return;
    }

    this.camera.position.copy(position);
    this.controls.target.copy(target);
    this.controls.update();
    this.markNeedsRender();
  }

  /**
   * レンダリングが必要であることをマークします
   */
  markNeedsRender(): void {
    this.needsRender = true;
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
   * リソースをクリーンアップします
   */
  dispose(): void {
    if (this.animationId !== undefined) {
      cancelAnimationFrame(this.animationId);
      this.animationId = undefined;
    }

    if (this.resizeHandler) {
      window.removeEventListener("resize", this.resizeHandler);
      this.resizeHandler = undefined;
    }

    if (this.controls) {
      this.controls.dispose();
      this.controls = undefined;
    }

    if (this.renderer) {
      if (this.renderer.domElement.parentElement) {
        this.renderer.domElement.parentElement.removeChild(
          this.renderer.domElement,
        );
      }
      this.renderer.dispose();
      this.renderer = undefined;
    }

    if (this.scene) {
      // シーン内のオブジェクトをクリーンアップ
      this.scene.traverse((object) => {
        if (object instanceof THREE.Mesh) {
          if (object.geometry) {
            object.geometry.dispose();
          }
          if (object.material) {
            if (Array.isArray(object.material)) {
              object.material.forEach((material) => {
                material.dispose();
              });
            } else {
              object.material.dispose();
            }
          }
        }
      });
      this.scene = undefined;
    }

    this.camera = undefined;
    this.hostElement = undefined;
    this.needsRender = true;
    this.lastRenderTime = 0;
  }
}



