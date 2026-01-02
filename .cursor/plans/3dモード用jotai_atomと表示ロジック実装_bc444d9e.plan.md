---
name: 3Dモード用Jotai atomと表示ロジック実装
overview: 3Dモード用の設定を管理するJotai atomを作成し、EditAppで3Dモード時にGrid3DControlsを表示するロジックを実装します。設定は永続化され、3Dモード時にコントロールUIが表示されるようにします。また、testing_guide.mdに従ったテストも実装します。
todos:
  - id: create-atom
    content: src/core/three/grid-3d-config.tsを作成し、grid3DConfigAtomを実装
    status: pending
  - id: integrate-controls
    content: EditAppで3Dモード時にGrid3DControlsを表示するロジックを実装
    status: pending
    dependencies:
      - create-atom
  - id: test-atom
    content: src/core/three/__tests__/grid-3d-config.test.tsを作成し、grid3DConfigAtomのユニットテストを実装
    status: pending
    dependencies:
      - create-atom
  - id: test-controls
    content: src/components/editor/renderers/3d-layout/__tests__/grid-3d-controls.test.tsxを作成し、Grid3DControlsのコンポーネントテストを実装
    status: pending
  - id: test-integration
    content: EditAppでのGrid3DControls統合テストを実装
    status: pending
    dependencies:
      - integrate-controls
---

# 3Dモード用Jotai atomと表示ロジック実装計画

## 概要

3Dモード用の設定を管理するJotai atomを作成し、`EditApp`で3Dモード時に`Grid3DControls`を表示するロジックを実装します。設定は永続化され、3Dモード時にコントロールUIが表示されるようにします。また、`testing_guide.md`に従ったテストも実装します。

## 実装内容

### 1. 3Dモード用設定のJotai atom作成

- `src/core/three/grid-3d-config.ts`を作成
- `atomWithStorage`を使用して`Grid3DConfig`を管理するatomを作成
- 既存の`cell3DPositionsAtom`（`src/core/three/cell-3d-positions.ts`）を参考に実装
- ストレージキー: `"marimo:3d:gridConfig:v1"`
- デフォルト値: `DEFAULT_GRID_3D_CONFIG`を使用
- **注意**: `Grid3DConfig`はオブジェクトなので、`jotaiJsonStorage`を直接使用（`adaptForLocalStorage`は不要）

### 2. EditAppでのGrid3DControls統合

- `src/core/edit-app.tsx`を編集
- `grid3DConfigAtom`をインポート
- `Grid3DControls`をインポート
- `is3DModeAtom`を使用して3Dモード判定
- 3Dモード時に`Grid3DControls`を表示
- `Grid3DControls`の配置は、既存の`GridControls`と同じ位置（`absolute pl-5 top-8`）に配置
- 3Dモード表示部分（338-385行目）に`Grid3DControls`を追加
- `useAtomValue`と`useSetAtom`を使用してatomと連携

### 3. 必要なインポートの追加

- `EditApp`に`Grid3DControls`をインポート
- `grid3DConfigAtom`をインポート
- `DEFAULT_GRID_3D_CONFIG`をインポート（必要に応じて）

## テスト実装

### 4.1 ユニットテスト: grid-3d-config.ts

- `src/core/three/__tests__/grid-3d-config.test.ts`を作成
- `grid3DConfigAtom`のテスト：
  - デフォルト値が正しく設定されること
  - ストレージから値を読み込めること
  - ストレージに値を保存できること
  - ストレージが空の場合、デフォルト値が使用されること
  - 不正な値がストレージにある場合、デフォルト値にフォールバックすること

### 4.2 コンポーネントテスト: Grid3DControls

- `src/components/editor/renderers/3d-layout/__tests__/grid-3d-controls.test.tsx`を作成
- `Grid3DControls`コンポーネントのテスト：
  - すべてのコントロールが正しくレンダリングされること
  - 各入力フィールドが正しく動作すること（正常系）
  - 各入力フィールドのバリデーションが正しく動作すること（異常系・エッジケース）
  - 設定変更時に`setConfig`が正しく呼ばれること
  - 各コントロールの`data-testid`が正しく設定されていること

### 4.3 統合テスト: EditAppでのGrid3DControls統合

- `src/core/__tests__/edit-app-3d-controls.test.tsx`を作成（または既存のEditAppテストに追加）
- EditAppでの統合テスト：
  - 3Dモード時に`Grid3DControls`が表示されること
  - 3Dモードでない時に`Grid3DControls`が表示されないこと
  - `grid3DConfigAtom`の値が`Grid3DControls`に正しく渡されること
  - `Grid3DControls`での設定変更が`grid3DConfigAtom`に反映されること

## ファイル構成

```javascript
src/
├── core/
│   ├── three/
│   │   ├── grid-3d-config.ts (新規)
│   │   └── __tests__/
│   │       └── grid-3d-config.test.ts (新規)
│   ├── edit-app.tsx (編集)
│   └── __tests__/
│       └── edit-app-3d-controls.test.tsx (新規、または既存テストに追加)
└── components/
    └── editor/
        └── renderers/
            └── 3d-layout/
                ├── grid-3d-controls.tsx (既存)
                ├── types.ts (既存)
                └── __tests__/
                    └── grid-3d-controls.test.tsx (新規)
```

## 実装の詳細

### grid-3d-config.tsの構造

- `atomWithStorage`を使用して`Grid3DConfig`を管理
- `jotaiJsonStorage`を使用（`Grid3DConfig`はオブジェクトなので、`adaptForLocalStorage`は不要）
- 既存の`cell3DPositionsAtom`の実装パターンに従う（ただし、ストレージアダプターは`jotaiJsonStorage`を使用）

### EditAppでの統合

- `is3DModeAtom`を使用して3Dモード判定
- `useAtomValue(grid3DConfigAtom)`で設定を取得
- `useSetAtom(grid3DConfigAtom)`で設定を更新
- `is3DMode`が`true`の時のみ`Grid3DControls`を表示
- `Grid3DControls`の配置は、既存の`GridControls`と同じ位置（`absolute pl-5 top-8`）に配置

### テストの実装パターン

#### ユニットテスト（grid-3d-config.test.ts）

```typescript
// テスト例
describe("grid3DConfigAtom", () => {
  beforeEach(() => {
    // ストレージをクリア
    localStorage.clear();
    // Jotaiストアをリセット
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
  });

  it("should initialize with default values", () => {
    const config = store.get(grid3DConfigAtom);
    expect(config).toEqual(DEFAULT_GRID_3D_CONFIG);
  });

  it("should persist values to storage", () => {
    const newConfig = { ...DEFAULT_GRID_3D_CONFIG, spacingX: 500 };
    store.set(grid3DConfigAtom, newConfig);
    
    // ストレージから直接確認
    const stored = localStorage.getItem("marimo:3d:gridConfig:v1");
    expect(stored).toBeTruthy();
    const parsed = JSON.parse(stored!);
    expect(parsed.spacingX).toBe(500);
  });
});
```

#### コンポーネントテスト（grid-3d-controls.test.tsx）

```typescript
// テスト例
describe("Grid3DControls", () => {
  it("should render all controls", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-spacing-x-input")).toBeInTheDocument();
    // ... 他のコントロールも確認
  });

  it("should call setConfig when input changes", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    const input = screen.getByTestId("grid-3d-spacing-x-input");
    fireEvent.change(input, { target: { value: "500" } });
    
    expect(mockSetConfig).toHaveBeenCalledWith(
      expect.objectContaining({ spacingX: 500 })
    );
  });
});
```

#### 統合テスト（edit-app-3d-controls.test.tsx）

```typescript
// テスト例
describe("EditApp Grid3DControls Integration", () => {
  beforeEach(() => {
    store.set(is3DModeAtom, true);
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
  });

  it("should show Grid3DControls when in 3D mode", () => {
    render(<EditApp />);
    expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
  });

  it("should hide Grid3DControls when not in 3D mode", () => {
    store.set(is3DModeAtom, false);
    render(<EditApp />);
    expect(screen.queryByTestId("grid-3d-columns-input")).not.toBeInTheDocument();
  });
});
```

## 注意事項

- 設定の永続化は`atomWithStorage`で自動的に処理される
- `Grid3DConfig`はオブジェクトなので、`jotaiJsonStorage`を直接使用（`adaptForLocalStorage`は不要）
- テストでは、各テスト前にストレージとJotaiストアをリセットすること
- テスト実行時は、`@vitest-environment jsdom`を指定すること（コンポーネントテストの場合）

## 次のステップ（この計画では含まない）

- 3Dレンダラーでの設定の実際の適用（Grid3DRendererでの使用）
