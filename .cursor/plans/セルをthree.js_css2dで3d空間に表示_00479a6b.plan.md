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