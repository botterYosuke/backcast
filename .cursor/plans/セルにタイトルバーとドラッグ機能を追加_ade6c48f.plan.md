---
name: セルにタイトルバーとドラッグ機能を追加
overview: サンプルコード（BackcastPro-Steam）を参考に、3D空間に表示されるセルにタイトルバーを追加し、ドラッグ可能にします。各セルを個別のCSS2DObjectとして管理し、タイトルバーをドラッグして3D空間内で移動できるようにします。
todos:
  - id: create-drag-manager
    content: CellDragManagerサービスを作成（ドラッグ開始/終了、マウス移動処理、スケール考慮）
    status: completed
  - id: extend-css2d-service
    content: CellCSS2DServiceにセルごとのCSS2DObject管理を追加（Map<cellId, CSS2DObject>、位置更新メソッド）
    status: completed
  - id: create-cell-wrapper
    content: Cell3DWrapperコンポーネントを作成（タイトルバー、ドラッグハンドル、セルコンテンツのラップ）
    status: completed
    dependencies:
      - create-drag-manager
  - id: modify-cells-3d-renderer
    content: Cells3DRendererを修正して各セルを個別のCSS2DObjectとして管理し、Cell3DWrapperを使用
    status: completed
    dependencies:
      - extend-css2d-service
      - create-cell-wrapper
  - id: add-styles
    content: タイトルバーのスタイルを追加（サンプルコードのfloating-window-base.styles.scssを参考）
    status: completed
    dependencies:
      - create-cell-wrapper
  - id: test-dragging
    content: ドラッグ機能の動作確認とパフォーマンス調整
    status: completed
    dependencies:
      - modify-cells-3d-renderer
      - add-styles
---

# セルにタイトルバーとド

ラッグ機能を追加

## 概要

現在の実装では、すべてのセルが1つのコンテナ内に配置されています。サンプルコード（BackcastPro-Steam）を参考に、各セルにタイトルバーを追加し、個別にドラッグ可能にします。

## 実装ファイル

### 1. セルドラッグ管理サービス（新規作成）

- `src/core/three/cell-drag-manager.ts` - セルのドラッグ処理を管理
- ドラッグ開始/終了の処理
- マウス移動時の位置更新
- CSS2D空間での座標変換（スケール考慮）
- ドラッグ中のセル位置の更新

### 2. セル3Dレンダラーの修正

- `src/components/editor/renderers/cells-3d-renderer.tsx` - セルごとにタイトルバーを追加
- 各セルを個別のCSS2DObjectとして管理（現在は1つのコンテナにまとめている）
- セル要素をラップするタイトルバー付きコンテナを作成
- タイトルバーのドラッグイベントハンドラーを設定
- セルごとの位置管理

### 3. セルラッパーコンポーネント（新規作成）

- `src/components/editor/renderers/cell-3d-wrapper.tsx` - セルをタイトルバー付きでラップ
- タイトルバーの表示（セル名またはID）
- ドラッグハンドルの実装
- セルコンテンツの表示

### 4. CSS2Dサービスの拡張

- `src/core/three/cell-css2d-service.ts` - セルごとのCSS2DObject管理を追加
- セルごとのCSS2DObjectの管理（Map<cellId, CSS2DObject>）
- セル位置の更新メソッド
- セルの追加/削除時の処理

## 実装詳細

### セルドラッグ管理サービスの設計

```typescript
class CellDragManager {
  private activeCellId: string | null = null;
  private isDragging = false;
  private dragStartX = 0;
  private dragStartY = 0;
  private cellStartPosition = new THREE.Vector3();
  
  startDrag(event: MouseEvent, cellId: string, currentPosition: THREE.Vector3): void
  onMouseMove(event: MouseEvent, css2DService: CellCSS2DService): void
  onMouseUp(): void
  updateCellPosition(cellId: string, position: THREE.Vector3): void
}
```



### セルラッパーの構造

各セルを以下の構造でラップ：

- タイトルバー（ドラッグ可能）
- セルコンテンツ（既存のCellコンポーネント）

### ドラッグ処理の流れ

1. タイトルバーでmousedown → `CellDragManager.startDrag()`
2. mousemove → スケールを考慮して位置を計算 → CSS2DObjectの位置を更新
3. mouseup → ドラッグ終了、最終位置を保存

### スケール考慮

サンプルコードと同様に、CSS2D空間でのスケールを考慮してドラッグ距離を調整：

```typescript
const scale = css2DService.getCurrentScale();
const adjustedDeltaX = scale > 0 ? deltaX / scale : deltaX;
const adjustedDeltaY = scale > 0 ? deltaY / scale : deltaY;
```



## 技術的な考慮事項

1. **パフォーマンス**

- ドラッグ中はrequestAnimationFrameを使用してスムーズに更新
- ドラッグ終了時のみ最終位置を保存

2. **既存機能の維持**

- セルの編集、実行、削除などの機能を維持
- グリッド配置アルゴリズムは初期配置のみに使用

3. **座標系の変換**

- 画面座標（clientX, clientY）から3D空間座標への変換
- CSS2D空間でのスケールを考慮した位置計算

## 実装順序

1. `CellDragManager`サービスの作成
2. `CellCSS2DService`にセルごとのCSS2DObject管理を追加