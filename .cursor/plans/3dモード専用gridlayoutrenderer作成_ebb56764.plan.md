# 3D

モード専用GridLayoutRenderer作成計画

## 概要

`GridLayoutRenderer`は通常のグリッドモードと3Dモードの両方で使用されていますが、3Dモードでは`GridControls`が不要です（`Grid3DControls`が`edit-app.tsx`で別途表示されるため）。3Dモード専用の`Grid3DLayoutRenderer`を作成し、`GridControls`を表示しないバージョンを実装します。

## 現在の構造

- **通常のグリッドモード**: `GridLayoutPlugin` → `GridLayoutRenderer`（`GridControls`を含む）
- **3Dモード**: `Grid3DRenderer` → `GridLayoutRenderer`（`GridControls`が不要だが表示されてしまう）

## 実装内容

### 1. Grid3DLayoutRendererコンポーネントの作成

**ファイル**: `src/components/editor/renderers/3d-layout/grid-3d-layout-renderer.tsx`

- `GridLayoutRenderer`をベースに複製
- `GridControls`の表示を削除（305-312行目の`GridControls`コンポーネントを削除）
- `GridControls`のインポートを削除（使用しないため）
- `isLocked`と`setIsLocked`のstateは保持（`enableInteractions`の計算で使用されるため）
- ただし、`GridControls`を削除すると`isLocked`を変更するUIがなくなるため、現時点では常に`false`のまま
- 将来的に`Grid3DControls`に「Lock Grid」機能を追加する可能性がある
- その他の機能（グリッドレイアウト、セル表示、Outputsパネルなど）は全て保持
- Props型は`ICellRendererProps<GridLayout>`を継承

### 2. Grid3DRendererの更新

**ファイル**: `src/components/editor/renderers/grid-3d-renderer.tsx`

- `GridLayoutRenderer`のインポートを削除
- `Grid3DLayoutRenderer`をインポート（`./3d-layout/grid-3d-layout-renderer`から）
- 80行目の`GridLayoutRenderer`を`Grid3DLayoutRenderer`に変更

### 3. テストファイルの作成

**ファイル**: `src/components/editor/renderers/3d-layout/__tests__/grid-3d-layout-renderer.test.tsx`

- `Grid3DLayoutRenderer`のテストを作成
- `GridControls`が表示されないことを確認するテストを追加
- 既存の`GridLayoutRenderer`のテストを参考にする

### 4. 共有コンポーネントの確認

`GridLayoutRenderer`内の以下のコンポーネントは`Grid3DLayoutRenderer`でも使用可能：

- `GridCell`（374-418行目）
- `EditableGridCell`（514-574行目）
- `GridHoverActions`（591-666行目）
- `isSidebarCell`関数（668-674行目）
- `SIDE_TO_ICON`定数（676-682行目）

これらは`Grid3DLayoutRenderer`内でも同じように定義するか、共通化を検討します。今回は実装の簡潔さのため、`Grid3DLayoutRenderer`内に同じコードをコピーします。

## ファイル構成

````javascript
src/components/editor/renderers/
├── grid-layout/
│   ├── grid-layout.tsx (既存 - 通常モード用)
│   └── ...
└── 3d-layout/
    ├── grid-3d-controls.tsx (既存)
    ├── grid-3d-layout-renderer.tsx (新規)
    └── types.ts (既存)
```

## 実装の詳細

### grid-3d-layout-renderer.tsxの構造

- `GridLayoutRenderer`の全コードをコピー
- コンポーネント名を`Grid3DLayoutRenderer`に変更
- 305-312行目の`GridControls`コンポーネントの呼び出しを削除
- 307行目の`<>`と312行目の`</>`を削除し、直接`<div>`を返すように変更

### 変更箇所の詳細

**削除する部分**（305-312行目）:

```typescript
return (
  <>
    <GridControls
      layout={layout}
      setLayout={setLayout}
      isLocked={isLocked}
      setIsLocked={setIsLocked}
    />
    <div className={cn("relative flex z-10 flex-1 overflow-hidden")}>
      ...
    </div>
  </>
);
```

**変更後**:

```typescript
return (
  <div className={cn("relative flex z-10 flex-1 overflow-hidden")}>
    ...
  </div>
);
```

## 注意事項

### isLockedの扱い

- `isLocked`と`setIsLocked`のstateは`enableInteractions`の計算で使用されるため、`Grid3DLayoutRenderer`でも保持する必要があります
- ただし、`GridControls`を削除すると`isLocked`を変更するUIがなくなるため、現時点では常に`false`のままです
- 将来的に`Grid3DControls`に「Lock Grid」機能を追加する可能性があります

### インポートの確認

- `GridLayoutRenderer`と同様のインポート一式が必要です
- `GridControls`のインポートは削除してください（使用しないため）
- 共有コンポーネント（`GridCell`、`EditableGridCell`、`GridHoverActions`など）は同じファイル内に定義します

### その他

- `Grid3DLayoutRenderer`は`GridLayoutRenderer`の機能をほぼ完全に継承しますが、`GridControls`のみを除外します
- 通常のグリッドモードでは引き続き`GridLayoutRenderer`が使用されます
- 3Dモードでは`Grid3DControls`が`edit-app.tsx`で表示されるため、`Grid3DLayoutRenderer`内では表示しません
- 将来的に共通部分を抽出してリファクタリングする可能性がありますが、今回は実装の簡潔さを優先します


````