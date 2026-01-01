---
name: 3Dモードのfloating-window位置を永続化
overview: 3Dモードのセル（floating-window）の位置情報をLocalStorageに保存し、3Dモードを切り替えても位置が保持されるようにする
todos:
  - id: create-position-atom
    content: 3Dモードのセル位置情報を管理するatomを作成（src/core/three/cell-3d-positions.ts）。LocalStorageキー名は"marimo:3d:cellPositions:v1"とする
    status: pending
  - id: integrate-position-restore
    content: Cells3DRendererのupdatePositions関数内で、atomから位置情報を復元（167行目前に処理を追加）
    status: pending
    dependencies:
      - create-position-atom
  - id: integrate-position-save
    content: Cells3DRendererのdragManager.setPositionUpdateCallback内で、位置更新時にatomも更新
    status: pending
    dependencies:
      - create-position-atom
  - id: cleanup-positions
    content: 既存のクリーンアップ処理（192-199行目）に、atomからの位置情報削除も追加
    status: pending
    dependencies:
      - create-position-atom
---

# 3Dモードのfloating-window位置を永続化

## 問題

現在、3Dモードのセル（floating-window）の位置情報は`Cells3DRenderer`コンポーネント内の`cellPositionsRef`（Map）としてのみ管理されており、3Dモードから通常モードに切り替えると失われてしまいます。再度3Dモードに戻ると、位置情報がデフォルト位置（グリッド位置）に戻ってしまいます。

## 解決策

セルの位置情報をJotaiのatomで管理し、LocalStorageに永続化します。

## 実装手順

### 1. 位置情報を管理するatomを作成

[src/core/three/cell-3d-positions.ts](src/core/three/cell-3d-positions.ts)を新規作成し、以下を実装：

- `Cell3DPosition`型を定義（`{x: number, y: number, z: number}`）
- LocalStorageキー名を定義：`"marimo:3d:cellPositions:v1"`（既存パターンに合わせる）
- `cell3DPositionsAtom`を作成（`Map<CellId, Cell3DPosition>`）
- `adaptForLocalStorage`を使ってLocalStorageに永続化
- Mapを配列のタプル`[CellId, Cell3DPosition][]`に変換してシリアライズ
- デシリアライズ時にMapに復元

### 2. Cells3DRendererで位置情報を復元・保存

[src/components/editor/renderers/cells-3d-renderer.tsx](src/components/editor/renderers/cells-3d-renderer.tsx)を修正：

- `cell3DPositionsAtom`をインポート
- **位置復元**：`updatePositions`関数内（155-190行目）で、atomから位置情報を読み込む
- 既存の`cellPositionsRef.current.get(cellId)`チェック（167行目）の前に、atomから復元を試みる
- atomに位置があれば`cellPositionsRef`に設定（THREE.Vector3に変換）
- atomに位置がなければ、グリッド配置を計算（既存のロジック）
- **位置保存**：`dragManager.setPositionUpdateCallback`内（63-85行目）で、位置が更新されたときにatomも更新する
- ドラッグ中の各フレームで呼ばれる可能性があるため、必要に応じて最適化を検討（現在はそのまま更新）
- **削除時のクリーンアップ**：既存のクリーンアップ処理（192-199行目）に、atomからの削除も追加

### 3. ファイル構成

- 新規ファイル：`src/core/three/cell-3d-positions.ts` - 位置情報を管理するatomと型定義

## 技術的な詳細

### シリアライゼーション

THREE.Vector3は直接シリアライズできないため、`{x: number, y: number, z: number}`の形式に変換します。

### データ構造

- 保存形式：`Map<CellId, {x: number, y: number, z: number}>`
- シリアライズ形式：`[CellId, {x: number, y: number, z: number}][]`（配列のタプル）

### 既存コードへの影響

- `cellPositionsRef`の使用方法は変更なし（内部的にatomと同期）
- 既存の位置計算ロジックは変更なし
- グリッド配置は、atomに位置情報がない場合のみ適用される（既存動作を維持）

## 実装の詳細

### LocalStorageキー名

既存パターン（`"marimo:ai:chatState:v5"`など）に合わせて、`"marimo:3d:cellPositions:v1"`を使用します。

### 位置復元のタイミング

`updatePositions`関数内（155-190行目）で、以下の順序で処理します：

1. atomから位置情報を取得（`useAtomValue`で取得したatomの値を参照）
2. atomに位置があれば、`cellPositionsRef`に設定（THREE.Vector3に変換）
3. atomに位置がなければ、グリッド配置を計算（既存の168-172行目のロジック）

これにより、コンポーネントのマウント/アンマウントに関わらず、常に最新の位置情報を使用できます。

### ドラッグ中の更新頻度

`dragManager.setPositionUpdateCallback`はドラッグ中の各フレームで呼ばれる可能性があります。現在の実装では、各更新時にatomを更新しますが、LocalStorageへの書き込みはJotaiが適切に管理するため、過度な負荷は発生しません。将来的に最適化が必要な場合は、ドラッグ終了時にのみatomを更新する方式も検討可能です。

### 型定義の場所

`Cell3DPosition`型は`cell-3d-positions.ts`に定義します（atomと一緒で管理しやすい）。THREE.Vector3との変換は、`cells-3d-renderer.tsx`内で行います。