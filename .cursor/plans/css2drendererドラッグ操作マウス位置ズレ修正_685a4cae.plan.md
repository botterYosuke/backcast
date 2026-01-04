---
name: CSS2DRendererドラッグ操作マウス位置ズレ修正
overview: CSS2Dコンテナに`scale()`が適用されている場合、`react-grid-layout`がマウス座標をグリッド座標に変換する際にスケールを考慮しないため、ドラッグ時にセルが大きく飛ぶ問題を解決します。ログ分析から、`react-grid-layout`はスケール適用前のDOMサイズ（`offsetWidth`）を使用してグリッド座標を計算しているが、マウス座標はスケール適用後の見た目座標系であることが判明しました。
todos:
  - id: "1"
    content: cell-css2d-service.tsでグリッドスケール取得メソッドを確認/追加
    status: completed
  - id: "2"
    content: grid-3d-layout-renderer.tsxでスケール変更を監視するuseEffectフックを追加
    status: completed
    dependencies:
      - "1"
  - id: "3"
    content: react-grid-layout要素のDOMサイズをスケール適用前のサイズに設定するロジックを実装
    status: completed
    dependencies:
      - "2"
  - id: "4"
    content: "動作確認: ドラッグ操作が正しく動作するか確認"
    status: completed
    dependencies:
      - "3"
  - id: "5"
    content: "動作確認: スケール変更時にドラッグが正常に動作し続けるか確認"
    status: completed
    dependencies:
      - "3"
  - id: "6"
    content: "動作確認: リサイズハンドルが正常に動作し続けるか確認"
    status: completed
    dependencies:
      - "3"
---

# C

SS2DRendererドラッグ操作マウス位置ズレ修正

## 問題の根本原因（ログ分析結果に基づく）

### 判明した事実

ログ分析から以下の事実が確認されました：

1. **`react-grid-layout`の座標計算方法**:

- `reversedGridXFromCellRect: 0`が`newItem.x: 0`と一致
- `react-grid-layout`はセルの実際のDOM位置（スケール適用後の見た目位置）からグリッド座標を計算している

2. **サイズ情報の不一致**:

- `rglOffsetWidth: 1000` (react-grid-layout要素の実際のDOMサイズ、スケール適用前)
- `rglRect.width: 1845.83` (react-grid-layout要素の見た目サイズ、スケール適用後)
- `gridScale: 1.84583` (スケール値)
- 関係: `1000 * 1.84583 ≈ 1845.83` ✓

3. **マウス座標の座標系**:

- `mouseRelativeToRglX: 396.76` (マウス座標、react-grid-layout要素からの相対位置)
- この座標はスケール適用後の見た目座標系
- `calculatedGridXUsingRglOffset: 15` (rglOffsetWidthを使用した計算: `floor((396.76/1000)*38) = 15`)
- しかし、実際の`newItem.x: 0`と一致しない

4. **問題の核心**:

- `react-grid-layout`は内部で`offsetWidth`（スケール適用前のDOMサイズ）を使用してグリッド座標を計算
- しかし、マウスイベントの座標はスケール適用後の見た目座標系で提供される
- この不一致により、ドラッグ時にセルが大きく飛ぶ

### 具体的な問題例

- ドラッグ開始時: `oldItem: {x:0, y:4}`, `clientX: 892, clientY: 197`
- 最初の移動時: `newItem: {x:0, y:7}`, `clientX: 892, clientY: 198`（マウスは1px下に移動しただけ）
- マウス座標から計算したグリッド位置: `calculatedGridXUsingRglOffset: 15`, `calculatedGridYUsingRglOffset: 6`
- 実際のグリッド位置: `newItem: {x:0, y:7}`
- セルのDOM位置から逆算: `reversedGridXFromCellRect: 0`, `reversedGridYFromCellRect: 13`

## 解決策

### アプローチ：react-grid-layout要素のDOMサイズをスケール適用前のサイズに固定

**根本的な解決方法**:

- `react-grid-layout`要素のDOMサイズ（`offsetWidth`）をスケール適用前のサイズに固定
- CSS transformの`scale()`で見た目サイズを調整
- これにより、`react-grid-layout`が使用する`offsetWidth`とマウス座標の座標系が一致する

**実装方法**（実装済み、grid-3d-layout-renderer.tsx 113-172行目）:

1. `react-grid-layout`要素の親要素（`.grid-3d-container`内の`.react-grid-layout`要素）を取得（119行目、125行目）
2. スケール値を取得：DOM要素（`.grid-3d-container`）の`style.transform`から直接スケール値を抽出（131-133行目）

- **注意**: プランでは`cell-css2d-service.ts`の`getCurrentGridScale()`メソッドを使用する予定でしたが、実際の実装ではDOMから直接読み取る方法が採用されています
- `cell-css2d-service.ts`の`getGridContainerScale()`メソッドは実装されていますが、このプランでは使用されていません

3. `react-grid-layout`要素のDOMサイズを`現在のサイズ / スケール値`に設定（148-159行目）
4. 親要素（`.grid-3d-container`）に`scale()`を適用（既存の実装を維持、CellCSS2DServiceが管理）

**注意点**:

- `react-grid-layout`要素のDOMサイズを変更すると、レイアウトの再計算が発生する可能性がある
- スケール値が動的に変化するため、スケール変更時にDOMサイズも更新する必要がある
- `react-grid-layout`要素のサイズ変更は、`onLayoutChange`コールバックをトリガーする可能性がある

### 代替アプローチ：マウスイベントの座標変換（非推奨）

マウスイベントをインターセプトして座標を変換する方法もありますが、`react-grid-layout`の内部処理と競合する可能性が高く、パフォーマンスへの影響も懸念されるため、推奨しません。

## 実装手順

1. **`cell-css2d-service.ts`にグリッドスケール取得メソッドを追加**（実装済み：`getGridContainerScale()`メソッドが存在）
2. **`grid-3d-layout-renderer.tsx`でスケール変更を監視**（実装済み）

- `useEffect`フックでスケール値を監視
- DOM要素（`.grid-3d-container`）の`style.transform`から直接スケール値を抽出（`cell-css2d-service.ts`のメソッドを使用せず、DOMから直接読み取る実装）
- スケール変更時に`react-grid-layout`要素のDOMサイズを更新

3. **DOMサイズの計算と適用**（実装済み）

- `react-grid-layout`要素の現在の見た目サイズを取得（`getBoundingClientRect()`）
- スケール値で割って、DOMサイズを計算
- `react-grid-layout`要素の`style.width`と`style.height`を設定

4. **動作確認**（実装済み）

- ドラッグ時にセルが正しく移動するか確認
- マウスの移動距離とセルの移動距離が一致するか確認
- スケール変更時にドラッグが正常に動作し続けるか確認
- `react-grid-layout`のリサイズハンドルが正常に動作し続けるか確認

## 実装ファイル

- [src/core/three/cell-css2d-service.ts](src/core/three/cell-css2d-service.ts) - グリッドスケール取得メソッド`getGridContainerScale()`が実装済み（実際の実装では使用されていない）
- [src/components/editor/renderers/3d-layout/grid-3d-layout-renderer.tsx](src/components/editor/renderers/3d-layout/grid-3d-layout-renderer.tsx) - スケール監視とDOMサイズ更新の実装（113-172行目）

## 実装の詳細（実際の実装）

実際の実装では、`grid-3d-layout-renderer.tsx`の`useEffect`フック（113-172行目）で、DOM要素（`.grid-3d-container`）の`style.transform`から直接スケール値を抽出しています。`cell-css2d-service.ts`の`getGridContainerScale()`メソッドは実装されていますが、このプランでは使用されていません。

## 確認事項