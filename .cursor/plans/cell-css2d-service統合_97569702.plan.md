---
name: cell-css2d-service統合
overview: "`src/three/cell-css2d-service.ts`と`src/core/three/cell-css2d-service.ts`を統合し、`src/core/three/cell-css2d-service.ts`を基準に機能をマージします。カメラチェックロジックは`src/three/cell-css2d-service.ts`の有効な実装を採用し、`src/three/cell-css2d-service.ts`を削除します。"
todos:
  - id: update-calculate-scale
    content: src/core/three/cell-css2d-service.tsのcalculateScaleメソッドでカメラチェックロジックを有効化（cameraを引数で受け取るように変更）
    status: pending
  - id: extract-common-scale-logic
    content: updateContainerScaleとupdateGridContainerScaleの重複コードを共通メソッドに抽出
    status: pending
    dependencies:
      - update-calculate-scale
  - id: extract-magic-numbers
    content: マジックナンバー（-800, 1200）を定数として定義
    status: pending
  - id: verify-import-path
    content: src/core/edit-app.tsxのインポートパスを確認し、必要に応じて修正
    status: pending
  - id: verify-method-usage
    content: getContainerPositionとsetSceneメソッドの使用箇所を確認（確認済み：両方とも使用されているため維持）
    status: pending
  - id: delete-duplicate-file
    content: src/three/cell-css2d-service.tsを削除（未使用インポートSceneManagerも含む）
    status: pending
    dependencies:
      - update-calculate-scale
      - extract-common-scale-logic
      - extract-magic-numbers
      - verify-import-path
      - verify-method-usage
  - id: verify-integration
    content: 統合後の動作確認（3Dモード、グリッドコンテナ、スケール調整）
    status: pending
    dependencies:
      - delete-duplicate-file
---

# ce

ll-css2d-service統合計画

## 概要

2つの`cell-css2d-service.ts`ファイルを統合します。`src/core/three/cell-css2d-service.ts`を基準に、`src/three/cell-css2d-service.ts`の差分をマージし、重複を解消します。

## 現状分析

### ファイルの違い

1. **`src/core/three/cell-css2d-service.ts`** (569行)

- グリッドコンテナ機能あり
- `calculateScale`でカメラチェックがコメントアウト（固定値1200）
- 使用箇所: `grid-3d-renderer.tsx`, `cell-drag-manager.ts`, `cells-3d-renderer.tsx`, `cell-3d-wrapper.tsx`

2. **`src/three/cell-css2d-service.ts`** (373行)

- グリッドコンテナ機能なし
- `calculateScale`でカメラチェックが有効
- 使用箇所: `edit-app.tsx`

### インポート状況

- `src/core/edit-app.tsx`: `./three/cell-css2d-service` (相対パス)
- その他: `@/core/three/cell-css2d-service` (絶対パス)

## 統合方針

1. **基準ファイル**: `src/core/three/cell-css2d-service.ts`を維持（グリッド機能を含む）
2. **統合内容**: `calculateScale`のカメラチェックロジックを有効化
3. **リファクタリング**: コード品質レビューで指摘された改善を同時に実施

                                                - 重複コードの削減（スケール更新ロジックの共通化）
                                                - マジックナンバーの定数化
                                                - タイミング問題の解決（`calculateScale`に`camera`を引数で渡す）

4. **インポート修正**: `src/core/edit-app.tsx`のインポートパスを確認・修正
5. **削除**: `src/three/cell-css2d-service.ts`を削除

## 実装手順

### 1. 定数の定義

マジックナンバーを定数として定義します：

```typescript
// スケール計算用の設定
private baseDistance: number | null = null;
private readonly MIN_SCALE = 0.1;
private readonly MAX_SCALE = 5.0;
// 追加: 定数定義
private readonly DEFAULT_BASE_DISTANCE = 1200; // デフォルト基準距離
private readonly GRID_DISTANCE_OFFSET = 800; // グリッドコンテナの距離オフセット（グリッドをセルより手前に配置するための調整値）
```



### 2. `calculateScale`メソッドの修正

[src/core/three/cell-css2d-service.ts](src/core/three/cell-css2d-service.ts)の267-288行の`calculateScale`メソッドを修正します：

1. **カメラチェックロジックを有効化**
2. **`camera`を引数で受け取るように変更**（タイミング問題の解決）
3. **定数を使用**

**変更前**:

```typescript
private calculateScale(distance: number): number {
  if (distance <= 0) {
    return this.MAX_SCALE;
  }

  if (this.baseDistance === null) {
    // if (this.camera) {
    //   this.baseDistance = Math.abs(this.camera.position.y);
    // } else {
      this.baseDistance = 1200;
    // }
  }

  const scale = this.baseDistance / distance;
  return Math.max(this.MIN_SCALE, Math.min(this.MAX_SCALE, scale));
}
```

**変更後**:

```typescript
private calculateScale(distance: number, camera?: THREE.PerspectiveCamera): number {
  if (distance <= 0) {
    return this.MAX_SCALE;
  }

  if (this.baseDistance === null) {
    if (camera) {
      // カメラの高さ位置を基準距離として使用
      this.baseDistance = Math.abs(camera.position.y);
    } else {
      this.baseDistance = this.DEFAULT_BASE_DISTANCE;
    }
  }

  const scale = this.baseDistance / distance;
  return Math.max(this.MIN_SCALE, Math.min(this.MAX_SCALE, scale));
}
```



### 3. スケール更新ロジックの共通化

`updateContainerScale`と`updateGridContainerScale`の重複コードを共通メソッドに抽出します。**共通メソッドの追加**:

```typescript
/**
    * コンテナのスケールを更新する共通ロジック
 */
private updateContainerScaleInternal(
  container: HTMLDivElement,
  css2DObject: CSS2DObject,
  camera: THREE.PerspectiveCamera,
  distanceOffset: number = 0
): void {
  // カメラとCSS2Dオブジェクトの3D空間での位置を取得
  const cameraPosition = camera.position;
  const objectPosition = new THREE.Vector3();
  css2DObject.getWorldPosition(objectPosition);

  // 距離を計算（y方向のみ）+ オフセット
  const distance = Math.abs(cameraPosition.y - objectPosition.y) + distanceOffset;

  // スケールを計算（cameraを引数で渡す）
  const scale = this.calculateScale(distance, camera);

  // CSS2DRendererが設定した既存のtransformを取得
  const existingTransform = container.style.transform || "";

  // 既存のtransformからscale()を削除（既に存在する場合）
  let cleanedTransform = existingTransform.replace(/\s*scale\([^)]*\)/gi, "");

  // 既存のtransformにscale()を追加
  const newTransform = cleanedTransform.trim()
    ? `${cleanedTransform.trim()} scale(${scale})`
    : `scale(${scale})`;

  // DOM要素のtransformスタイルを更新
  container.style.transform = newTransform;
  container.style.transformOrigin = "center center";
}
```

**既存メソッドの簡略化**:

```typescript
private updateContainerScale(camera: THREE.PerspectiveCamera): void {
  if (!this.cellContainer || !this.css2DObject) {
    return;
  }
  this.updateContainerScaleInternal(this.cellContainer, this.css2DObject, camera);
}

private updateGridContainerScale(camera: THREE.PerspectiveCamera): void {
  if (!this.gridContainer || !this.gridCSS2DObject) {
    return;
  }
  // グリッドコンテナは負のオフセット（手前に配置）
  this.updateContainerScaleInternal(
    this.gridContainer,
    this.gridCSS2DObject,
    camera,
    -this.GRID_DISTANCE_OFFSET
  );
}
```



### 4. インポートパスの確認

[src/core/edit-app.tsx](src/core/edit-app.tsx)の50行目のインポートパスを確認します。現在は`./three/cell-css2d-service`となっていますが、これは`src/core/three/cell-css2d-service.ts`を指しているはずです。もし`src/three/cell-css2d-service.ts`を参照している場合は、`@/core/three/cell-css2d-service`に変更します。**確認結果**: `./three/cell-css2d-service`は`src/core/three/cell-css2d-service.ts`を正しく指しているため、変更不要です。

### 5. メソッドの使用状況確認

以下のメソッドが使用されていることを確認済みです：

- `getContainerPosition()`: 
                                                                                                                                - `cells-3d-renderer.tsx`で3箇所使用（95行目、176行目、316行目）
                                                                                                                                - `cell-3d-wrapper.tsx`で1箇所使用（115行目）
                                                                                                                                - **維持が必要**
- `setScene()`:
                                                                                                                                - `edit-app.tsx`の184行目で使用
                                                                                                                                - `cells-3d-renderer.tsx`の164行目で使用
                                                                                                                                - **維持が必要**

これらのメソッドは`src/core/three/cell-css2d-service.ts`に既に存在するため、統合時に問題はありません。

### 6. `src/three/cell-css2d-service.ts`の削除

統合が完了したら、[src/three/cell-css2d-service.ts](src/three/cell-css2d-service.ts)を削除します。**注意**: このファイルの5行目に未使用のインポート`import type { SceneManager } from "./scene-manager";`がありますが、ファイル削除と同時に解消されます。

### 7. 動作確認

統合後、以下を確認します：

- 3Dモードでのセル表示が正常に動作するか
- グリッドコンテナが正常に動作するか（`src/core/three/cell-css2d-service.ts`のグリッド機能が維持されることを確認）
- カメラ距離に基づくスケール調整が正常に動作するか（カメラチェックロジック有効化後の動作確認）
- `getContainerPosition()`と`setScene()`が正常に動作するか

## リファクタリング内容

統合と同時に以下のコード品質改善を実施します：

### 1. 重複コードの削減

- `updateContainerScale`と`updateGridContainerScale`の重複ロジックを`updateContainerScaleInternal`に抽出
- DRY原則に従い、メンテナンス性を向上

### 2. マジックナンバーの定数化

- `1200` → `DEFAULT_BASE_DISTANCE`定数
- `-800` → `GRID_DISTANCE_OFFSET`定数（負の値として使用）
- 定数の意味をコメントで明記

### 3. タイミング問題の解決

- `calculateScale`に`camera`を引数で渡すように変更
- `this.camera`への依存を減らし、より安全な実装に

### 4. コードの可読性向上

- 共通ロジックの抽出により、コードの意図が明確に
- 定数の使用により、マジックナンバーの意味が明確に

## 注意事項

- `calculateScale`のカメラチェックロジックを有効化することで、カメラ位置に基づく動的な基準距離設定が可能になります
- グリッドコンテナ機能は`src/core/three/cell-css2d-service.ts`にのみ存在するため、統合後も維持されます
- `getContainerPosition()`と`setScene()`メソッドは複数箇所で使用されているため、統合後も維持されます
- `src/three/cell-css2d-service.ts`の未使用インポート（`SceneManager`）は、ファイル削除と同時に解消されます
- `src/three/`ディレクトリに他のファイル（`scene-manager.ts`, `utils.ts`）が存在する場合は、それらの統合は別途検討が必要です
- リファクタリングにより、既存の動作に影響がないことを確認する必要があります

## 確認済み事項

### メソッドの使用状況

- ✅ `getContainerPosition()`: 4箇所で使用（維持が必要）
- ✅ `setScene()`: 2箇所で使用（維持が必要）
- ✅ グリッドコンテナ機能: `src/core/three/cell-css2d-service.ts`に存在し、統合後も維持される

### インポートパス