---
name: セルをThree.js CSS2Dで3D空間に表示
overview: セルをThree.jsのCSS2DRendererを使用して3D空間に浮かせて表示する機能を実装します。BackcastPro-Steamのサンプルコードを参考に、React/TypeScript環境に適した実装を行います。
todos:
  - id: install-deps
    content: package.jsonにthreeと@types/threeを追加
    status: pending
  - id: create-scene-manager
    content: Three.jsシーン、カメラ、OrbitControlsを管理するSceneManagerを作成
    status: pending
    dependencies:
      - install-deps
  - id: create-css2d-service
    content: CSS2DRendererを管理するCellCSS2DServiceを作成（サンプルコードのFloatingWindowCSS2DServiceを参考）
    status: pending
    dependencies:
      - create-scene-manager
  - id: create-cells-3d-renderer
    content: セルを3D空間に配置するCells3DRendererコンポーネントを作成
    status: pending
    dependencies:
      - create-css2d-service
  - id: integrate-edit-app
    content: EditAppに3D表示モードを統合し、Cells3DRendererを使用
    status: pending
    dependencies:
      - create-cells-3d-renderer
  - id: implement-grid-layout
    content: セルをグリッド状に配置するアルゴリズムを実装
    status: pending
    dependencies:
      - create-cells-3d-renderer
  - id: add-orbit-controls
    content: OrbitControlsを統合してマウス操作で3D空間を操作可能にする
    status: pending
    dependencies:
      - create-scene-manager
  - id: test-and-adjust
    content: 動作確認とパフォーマンス調整
    status: pending
    dependencies:
      - integrate-edit-app
      - implement-grid-layout
      - add-orbit-controls
---

# セルをThr

ee.js CSS2Dで3D空間に表示する実装計画

## 概要

`backcast`プロジェクトのセル（JupyterライクなUI）をThree.jsのCSS2DRendererを使用して3D空間に浮かせて表示します。サンプルコード（BackcastPro-Steam）のアプローチを参考に、React/TypeScript環境に適した実装を行います。

## 実装ファイル

### 1. 依存関係の追加

- `package.json`に`three`と`@types/three`を追加

### 2. CSS2Dサービス（新規作成）

- `src/core/three/cell-css2d-service.ts` - CSS2DRendererの初期化と管理
- CSS2DRendererの初期化
- セルコンテナの作成と3D空間への配置
- カメラ距離に基づくスケール調整
- レンダリングループの管理

### 3. Three.jsシーン管理（新規作成）

- `src/core/three/scene-manager.ts` - Three.jsシーン、カメラ、OrbitControlsの管理
- シーン、カメラ、レンダラーの初期化
- OrbitControlsの設定
- アニメーションループ

### 4. セル3Dレンダラー（新規作成）

- `src/components/editor/renderers/cells-3d-renderer.tsx` - セルを3D空間に配置するコンポーネント
- セル要素をCSS2DObjectとして3D空間に配置
- グリッド配置アルゴリズム
- セルの追加/削除時の位置更新

### 5. EditAppの統合

- `src/core/edit-app.tsx` - 3D表示モードの追加
- 3D表示の有効/無効切り替え
- Cells3DRendererの統合

### 6. 設定とユーティリティ

- `src/core/three/utils.ts` - 3D関連のユーティリティ関数
- グリッド配置計算
- 座標変換

## 実装詳細

### CSS2Dサービスの設計

- `CellCSS2DService`クラスを作成
- `initializeRenderer()` - CSS2DRendererの初期化
- `attachCellContainerToScene()` - セルコンテナを3D空間に配置
- `updateCellScale()` - カメラ距離に基づくスケール調整
- `render()` - レンダリングループ

### セル配置アルゴリズム

- グリッド配置：セルを規則的な格子状に配置
- 各セルのDOM要素を取得してCSS2DObjectとして配置
- セルの追加/削除時に位置を再計算

### OrbitControlsの統合

- マウスで回転・ズーム・パン操作
- 操作中のレンダリング最適化

### 既存機能との統合

- セルの編集、実行、削除などの既存機能を維持
- 3D表示モードと通常表示モードの切り替え（将来的に設定で制御可能）

## 技術的な考慮事項

1. **パフォーマンス**

- セル数が多い場合の最適化
- レンダリングループの最適化（変更時のみレンダリング）

2. **既存機能の維持**

- セルの編集、実行、削除などの機能が正常に動作することを確認
- セルのドラッグ&ドロップ機能との統合

3. **レスポンシブ対応**

- ウィンドウリサイズ時の対応
- カメラ距離に基づくスケール調整

## 実装順序

1. 依存関係の追加（three, @types/three）
2. Three.jsシーン管理の実装
3. CSS2Dサービスの実装