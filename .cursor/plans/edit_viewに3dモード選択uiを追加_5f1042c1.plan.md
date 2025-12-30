---
name: ""
overview: ""
todos: []
---

#Edit Viewに3Dモード選択UIを追加

## 概要

Edit Viewの右上に、App Viewの`LayoutSelect`と同様のドロップダウンを追加し、"Vertical"（2D表示）と"3D"の2つのオプションから選択できるようにします。選択した値に応じて、3Dモードのオン/オフを切り替えます。

## 変更内容

### 1. 3Dモードの状態管理用atomを作成

[`src/core/edit-app.tsx`](src/core/edit-app.tsx)または新しいファイルで、3Dモードの状態を管理するatomを作成します：

- `is3DModeAtom`: boolean型のatomで、3Dモードが有効かどうかを管理
- 初期値は`false`（デフォルトで2D表示）

### 2. Edit View用の選択UIコンポーネントを作成

[`src/components/editor/renderers/edit-view-mode-select.tsx`](src/components/editor/renderers/edit-view-mode-select.tsx)を新規作成：

- `LayoutSelect`と似た構造のコンポーネント
- 選択肢は "Vertical"（2D）と "3D" の2つ
- "Vertical"のアイコンは`ListIcon`（`LayoutSelect`と同じ）
- "3D"のアイコンは適切なアイコン（例：`BoxIcon`や`CubeIcon`など）
- 選択値に応じて`is3DModeAtom`を更新

### 3. Controlsコンポーネントに選択UIを追加

[`src/components/editor/controls/Controls.tsx`](src/components/editor/controls/Controls.tsx)を修正：

- `presenting && <LayoutSelect />`の近くに、`isEditing && !presenting && <EditViewModeSelect />`を追加
- 右上のコントロールエリア（`topRightControls`）に表示

### 4. edit-app.tsxでatomを使用

[`src/core/edit-app.tsx`](src/core/edit-app.tsx)を修正：

- `const is3DMode = viewState.mode === "edit";` を削除
- `is3DModeAtom`から値を取得して使用：`const is3DMode = useAtomValue(is3DModeAtom) && viewState.mode === "edit";`
- これにより、Edit Viewの時のみatomの値に従って3Dモードを制御

### 5. 必要なアイコンの確認と追加

3Dモード用のアイコンが`lucide-react`にあるか確認し、必要に応じて適切なアイコンを選択します。

## ファイル構造

```javascript
src/
  core/
    edit-app.tsx (修正)
    three/ (既存の3D関連コード)
  components/
    editor/
      controls/
        Controls.tsx (修正)
      renderers/
        edit-view-mode-select.tsx (新規作成)
        layout-select.tsx (参考)




```