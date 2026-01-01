---
name: Grid3DRendererコンポーネント作成
overview: 3Dモード時にCSS2DRendererでGridも表示する
todos:
  - id: create-grid-3d-renderer
    content: grid-3d-renderer.tsxを作成。cells-3d-renderer.tsxを参考に、GridLayoutRendererをCSS2Dコンテナにレンダリングする実装を追加（セル追加ロジックは含めない）
    status: pending
  - id: update-edit-app
    content: edit-app.tsxを修正。GridLayoutRendererのオーバーレイ表示を削除し、Grid3DRendererを3Dコンテナ内に配置
    status: pending
    dependencies:
      - create-grid-3d-renderer
  - id: add-imports
    content: 必要なインポートを追加（Grid3DRenderer、GridLayout型など）
    status: pending
    dependencies:
      - create-grid-3d-renderer
---

# Grid3DRendererコンポーネントの作成

## 概要

3Dモード時にCSS2DRendererでGridも表示します。現在、GridLayoutRendererは通常のDOM要素として3D空間の上にオーバーレイ表示されていますが、これをCSS2DRendererで表示するように変更します。

## 実装内容

### 1. `grid-3d-renderer.tsx`の作成

[src/components/editor/renderers/grid-3d-renderer.tsx](src/components/editor/renderers/grid-3d-renderer.tsx)を新規作成します。

- `cells-3d-renderer.tsx`を参考に、GridLayoutRendererをCSS2DRendererで表示するコンポーネントを作成します
- セルを追加するロジックは含めません（GridLayoutRendererが内部で処理）
- `GridLayoutRenderer`を`createPortal`でCSS2Dコンテナにレンダリング
- 1つのCSS2DObjectとしてGrid全体を表示
- Propsは`Cells3DRenderer`と同様に：
  ```typescript
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
  ```




- 実装の流れ：

1. CSS2DServiceからセルコンテナを取得
2. Gridコンテナ用のdiv要素を作成
3. そのdiv要素をCSS2DObjectとして3D空間に配置
4. `createPortal`で`GridLayoutRenderer`をそのdiv要素内にレンダリング

### 2. `edit-app.tsx`の修正

[src/core/edit-app.tsx](src/core/edit-app.tsx)を修正します。

- 298-317行目のGridLayoutRendererのオーバーレイ表示を削除
- 代わりに、`Grid3DRenderer`を`Cells3DRenderer`と同様に3Dコンテナ内に配置
- `Grid3DRenderer`に必要なprops（layout、setLayout、cells）を渡す

### 3. インポートの追加

- `edit-app.tsx`に`Grid3DRenderer`のインポートを追加
- `grid-3d-renderer.tsx`に必要な依存関係（`GridLayoutRenderer`、`GridLayout`型など）をインポート

## 技術的な詳細

### CSS2DObjectの配置

- Grid全体を1つのCSS2DObjectとして表示
- 位置は3D空間の適切な位置（例：`new THREE.Vector3(0, 0, 0)`）に配置
- カメラ距離に応じたスケール調整は`CellCSS2DService`が自動的に処理

### createPortalの使用

- `GridLayoutRenderer`を通常のDOM要素としてレンダリング
- `createPortal`でCSS2Dコンテナ内に配置することで、3D空間上に表示

## 注意点

- セルを追加するロジックは含めない（GridLayoutRendererが内部で処理）