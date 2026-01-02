# テスト手法ガイド（Testing Guide）

**対象プロジェクト:** backcast（Webアプリケーション / Electronアプリケーション）

> 本ドキュメントは、backcastプロジェクトにおけるテスト手法、テスト戦略、実装パターンを解説します。  
> プロジェクト仕様書（[project_specification.md](./project_specification.md)）と品質ガイドライン（[quality_guidelines.md](./quality_guidelines.md)）と整合性を保ちながら、実践的なテスト手法を提供します。

---

## 目次

1. [テスト戦略の概要](#1-テスト戦略の概要)
2. [テスト階層と種類](#2-テスト階層と種類)
3. [テスト環境のセットアップ](#3-テスト環境のセットアップ)
4. [ユニットテスト](#4-ユニットテスト)
5. [コンポーネントテスト](#5-コンポーネントテスト)
6. [統合テスト](#6-統合テスト)
7. [E2Eテスト](#7-e2eテスト)
8. [テスト実行プロセス](#8-テスト実行プロセス)
9. [テストのベストプラクティス](#9-テストのベストプラクティス)
10. [トラブルシューティング](#10-トラブルシューティング)

---

## 1. テスト戦略の概要

### 1.1 テストピラミッド

backcastプロジェクトでは、以下のテストピラミッドに基づいたテスト戦略を採用しています：

```
        /\
       /E2E\          ← 少数のE2Eテスト（Playwright）
      /------\
     /統合テスト\      ← 中程度の統合テスト
    /----------\
   /コンポーネント\    ← 多くのコンポーネントテスト（Vitest + Testing Library）
  /------------\
 /  ユニットテスト  \  ← 最も多いユニットテスト（Vitest）
/------------------\
```

### 1.2 テストの目的

- **品質保証**: 機能が正しく動作することを保証
- **回帰防止**: 既存機能が壊れないことを保証
- **リファクタリング支援**: 安全にコードを改善できる環境を提供
- **ドキュメント化**: テストコードが仕様のドキュメントとして機能

### 1.3 テストカバレッジの目標

- **新機能**: 新機能や変更箇所には必ずテストを追加
- **エッジケース**: 正常系・異常系・エッジケースを網羅
- **カバレッジ率**: 可能な限り高いカバレッジを目指す（品質ガイドライン参照）

---

## 2. テスト階層と種類

### 2.1 ユニットテスト（Unit Tests）

**目的**: 関数、ユーティリティ、ロジックの単体テスト

**ツール**: Vitest

**対象**:
- ユーティリティ関数（`src/utils/`）
- コアロジック（`src/core/`）
- パッケージ内のロジック（`packages/*/src/`）

**特徴**:
- 高速に実行可能
- 外部依存を最小限に
- モックを活用

### 2.2 コンポーネントテスト（Component Tests）

**目的**: Reactコンポーネントの動作確認

**ツール**: Vitest + React Testing Library

**対象**:
- Reactコンポーネント（`src/components/`）
- カスタムフック（`src/hooks/`）
- プラグイン（`src/plugins/`）

**特徴**:
- DOM操作のテスト
- ユーザーインタラクションのシミュレーション
- アクセシビリティの確認

### 2.3 統合テスト（Integration Tests）

**目的**: フロントエンドとバックエンドの統合動作確認

**ツール**: Vitest + Testing Library + MSW（Mock Service Worker）

**対象**:
- API通信
- WebSocket通信
- 状態管理（Jotai）とコンポーネントの連携

**特徴**:
- バックエンドサーバーが起動している必要がある
- 実際の通信をシミュレート

### 2.4 E2Eテスト（End-to-End Tests）

**目的**: エンドツーエンドのシナリオテスト

**ツール**: Playwright

**対象**:
- ユーザーストーリー全体
- Gridモードと3Dモードの切り替え
- Pythonコード実行フロー

**特徴**:
- 実際のブラウザ環境で実行
- バックエンドサーバーが起動している必要がある
- 実行時間が長い

---

## 3. テスト環境のセットアップ

### 3.1 必要なツール

プロジェクトで使用するテストツールは以下の通りです：

- **Vitest**: ユニットテスト・コンポーネントテストフレームワーク
- **React Testing Library**: Reactコンポーネントのテストユーティリティ
- **Playwright**: E2Eテストフレームワーク
- **MSW**: APIモック用ライブラリ
- **jsdom**: DOM環境のシミュレーション

### 3.2 テスト設定ファイル

#### 3.2.1 グローバルセットアップ

`src/__tests__/setup.ts`でグローバルなテスト設定を行います：

```typescript
import { cleanup } from "@testing-library/react";
import { afterEach, beforeEach, vi } from "vitest";
import "@testing-library/jest-dom/vitest";
import "blob-polyfill";

// 各テスト前にモックをリセット
beforeEach(() => {
  vi.clearAllMocks();
});

// 各テスト後にクリーンアップ
afterEach(() => {
  cleanup();
});
```

#### 3.2.2 テスト環境の指定

テストファイルの先頭で環境を指定できます：

```typescript
// @vitest-environment jsdom
```

### 3.3 依存関係のインストール

```powershell
# 依存関係のインストール
pnpm install
```

---

## 4. ユニットテスト

### 4.1 基本的な書き方

ユニットテストは、関数やユーティリティの動作を検証します。

#### 例: 配列操作関数のテスト

```typescript
import { describe, expect, it } from "vitest";
import { arrayDelete, arrayInsert, arrayMove } from "../arrays";

describe("arrays", () => {
  describe("arrayDelete", () => {
    it("should delete an element at the specified index", () => {
      expect(arrayDelete([1, 2, 3], 1)).toEqual([1, 3]);
    });

    it("should handle first and last elements", () => {
      expect(arrayDelete([1, 2, 3], 0)).toEqual([2, 3]);
      expect(arrayDelete([1, 2, 3], 2)).toEqual([1, 2]);
    });
  });

  describe("arrayInsert", () => {
    it("should insert an element at the specified index", () => {
      expect(arrayInsert([1, 2, 3], 1, 4)).toEqual([1, 4, 2, 3]);
    });

    it("should clamp index to array bounds", () => {
      expect(arrayInsert([1, 2, 3], -1, 4)).toEqual([4, 1, 2, 3]);
      expect(arrayInsert([1, 2, 3], 5, 4)).toEqual([1, 2, 3, 4]);
    });
  });
});
```

### 4.2 テストの構造

1. **`describe`**: テストスイートをグループ化
2. **`it` / `test`**: 個別のテストケース
3. **`expect`**: アサーション（期待値の検証）

### 4.3 テストパターン

#### 4.3.1 正常系テスト

```typescript
it("should return expected result for valid input", () => {
  expect(functionUnderTest(validInput)).toEqual(expectedOutput);
});
```

#### 4.3.2 異常系テスト

```typescript
it("should throw error for invalid input", () => {
  expect(() => functionUnderTest(invalidInput)).toThrow();
});
```

#### 4.3.3 エッジケーステスト

```typescript
it("should handle empty array", () => {
  expect(functionUnderTest([])).toEqual([]);
});

it("should handle null/undefined", () => {
  expect(functionUnderTest(null)).toBeNull();
});
```

#### 4.3.4 パラメータ化テスト

```typescript
it.each([
  ["edit", "edit"],
  ["read", "read"],
  ["home", "home"],
])("should mount with mode %s", (mode) => {
  expect(mount({ mode })).toBeDefined();
});
```

### 4.4 モックの使用

外部依存をモックする場合：

```typescript
import { vi } from "vitest";

vi.mock("../utils/vitals", () => ({
  reportVitals: vi.fn(),
}));

vi.mock("react-dom/client", () => ({
  createRoot: vi.fn().mockImplementation((_el) => ({
    render: vi.fn(),
  })),
}));
```

### 4.5 テスト実行

```powershell
# 個別ファイルのテスト実行
cd frontend && pnpm test src/utils/__tests__/arrays.test.ts

# すべてのユニットテスト実行
pnpm test
```

---

## 5. コンポーネントテスト

### 5.1 基本的な書き方

Reactコンポーネントのテストは、React Testing Libraryを使用します。

#### 例: コンポーネントのレンダリングテスト

```typescript
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { OutputRenderer } from "../Output";

describe("OutputRenderer", () => {
  it("should render text output", () => {
    render(
      <OutputRenderer
        message={{
          channel: "output",
          data: "Hello World",
          mimetype: "text/plain",
        }}
      />
    );

    expect(screen.getByText("Hello World")).toBeInTheDocument();
  });

  it("should use renderFallback for unsupported mimetypes", () => {
    const renderFallback = (mimetype: string) => (
      <div data-testid="custom-fallback">Custom fallback for {mimetype}</div>
    );

    render(
      <OutputRenderer
        message={{
          channel: "output",
          data: "some data",
          mimetype: "application/unsupported",
        }}
        renderFallback={renderFallback}
      />
    );

    expect(screen.getByTestId("custom-fallback")).toBeInTheDocument();
  });
});
```

### 5.2 Testing Libraryのベストプラクティス

#### 5.2.1 クエリの優先順位

1. **`getByRole`**: アクセシビリティを考慮した推奨方法
2. **`getByLabelText`**: フォーム要素に最適
3. **`getByText`**: テキストコンテンツの検索
4. **`getByTestId`**: 最後の手段（可能な限り避ける）

```typescript
// 推奨: ロールベースのクエリ
const button = screen.getByRole("button", { name: "Submit" });

// 非推奨: test-idへの過度な依存
const button = screen.getByTestId("submit-button");
```

#### 5.2.2 ユーザーインタラクションのシミュレーション

```typescript
import { render, screen, fireEvent } from "@testing-library/react";

it("should handle button click", () => {
  const handleClick = vi.fn();
  render(<Button onClick={handleClick}>Click me</Button>);

  fireEvent.click(screen.getByRole("button"));
  expect(handleClick).toHaveBeenCalledTimes(1);
});
```

#### 5.2.3 非同期操作の待機

```typescript
import { waitFor } from "@testing-library/react";

it("should load data asynchronously", async () => {
  render(<DataComponent />);

  await waitFor(() => {
    expect(screen.getByText("Data loaded")).toBeInTheDocument();
  });
});
```

### 5.3 状態管理のテスト（Jotai）

Jotaiアトムを使用するコンポーネントのテスト：

```typescript
import { store } from "@/core/state/jotai";
import { viewStateAtom } from "@/core/mode";

beforeEach(() => {
  // 各テスト前に状態をリセット
  store.set(viewStateAtom, { mode: "not-set", cellAnchor: null });
});

it("should update state when component renders", () => {
  render(<MyComponent />);
  expect(store.get(viewStateAtom).mode).toBe("edit");
});
```

### 5.4 カスタムフックのテスト

```typescript
import { renderHook, act } from "@testing-library/react";
import { useBoolean } from "../useBoolean";

describe("useBoolean", () => {
  it("should toggle boolean value", () => {
    const { result } = renderHook(() => useBoolean(false));

    expect(result.current[0]).toBe(false);

    act(() => {
      result.current[1](); // toggle
    });

    expect(result.current[0]).toBe(true);
  });
});
```

### 5.5 スナップショットテスト

コンポーネントの出力をスナップショットとして保存：

```typescript
import { render } from "@testing-library/react";
import { expect, it } from "vitest";

it("should match snapshot", () => {
  const { container } = render(<MyComponent />);
  expect(container).toMatchSnapshot();
});
```

---

## 6. 統合テスト

### 6.1 統合テストの目的

統合テストは、複数のコンポーネントやモジュールが連携して動作することを確認します。

### 6.2 API通信のテスト（MSW使用）

MSW（Mock Service Worker）を使用してAPI通信をモック：

```typescript
import { setupServer } from "msw/node";
import { http, HttpResponse } from "msw";

const server = setupServer(
  http.get("/api/data", () => {
    return HttpResponse.json({ data: "test" });
  })
);

beforeAll(() => server.listen());
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
```

### 6.3 WebSocket通信のテスト

WebSocket通信をモックする場合：

```typescript
import { vi } from "vitest";

const mockWebSocket = {
  send: vi.fn(),
  close: vi.fn(),
  addEventListener: vi.fn(),
};

global.WebSocket = vi.fn(() => mockWebSocket) as unknown as typeof WebSocket;
```

### 6.4 統合テストの実行前提条件

**重要**: 統合テストを実行する前に、バックエンドサーバーが起動している必要があります。

```powershell
# バックエンドサーバーを起動（別ターミナル）
pnpm server

# 統合テストを実行
pnpm test:integration
```

---

## 7. E2Eテスト

### 7.1 E2Eテストの目的

E2Eテストは、実際のブラウザ環境でユーザーストーリー全体を検証します。

### 7.2 Playwrightの基本的な使い方

```typescript
import { test, expect } from "@playwright/test";

test("should switch between Grid and 3D mode", async ({ page }) => {
  await page.goto("http://localhost:3000");
  
  // Gridモードでセルを作成
  await page.click('[data-testid="add-cell"]');
  await page.fill('[data-testid="cell-editor"]', "print('hello')");
  
  // 3Dモードに切り替え
  await page.click('[data-testid="switch-to-3d"]');
  
  // 3Dモードでセルが表示されることを確認
  await expect(page.locator('[data-testid="3d-cell"]')).toBeVisible();
});
```

### 7.3 E2Eテストの実行前提条件

**重要**: E2Eテストを実行する前に、以下が必要です：

1. **バックエンドサーバーが起動していること**
2. **フロントエンド開発サーバーが起動していること**（またはビルド済み）

```powershell
# バックエンドサーバーを起動（別ターミナル）
pnpm server

# フロントエンド開発サーバーを起動（別ターミナル）
pnpm vite

# E2Eテストを実行
pnpm test:e2e
```

### 7.4 E2Eテストのベストプラクティス

- **安定性**: 適切な待機（`waitFor`）を使用
- **独立性**: 各テストは独立して実行可能に
- **データ分離**: テスト間でデータが干渉しないように

---

## 8. テスト実行プロセス

### 8.1 3段階デバッグプロセス

問題が発生した場合、以下の順序でテストを実行して原因を特定します：

#### ステップ1: Backendテスト

```powershell
# バックエンドのテストを実行
# （バックエンドがPythonの場合）
cd backend && python -m pytest tests/
```

**期待結果**: すべてのテストが成功していること

#### ステップ2: Frontend統合テスト

```powershell
# バックエンドサーバーを起動（別ターミナル）
pnpm server

# フロントエンド統合テストを実行
pnpm test:integration
```

**期待結果**: すべての統合テストが成功していること

#### ステップ3: E2Eテスト

```powershell
# バックエンドサーバーとフロントエンドサーバーを起動（別ターミナル）
pnpm dev

# E2Eテストを実行
pnpm test:e2e
```

**期待結果**: すべてのE2Eテストが成功すること

### 8.2 問題切り分けの原則

**重要**: 問題が起きたら小さい単位でテストして問題を切り分けて対処する

- 全体テストではなく、個別のテストケースや関数単位で実行
- エラーメッセージから原因を特定し、該当箇所のみを修正
- デバッグログを追加して詳細を確認
- 修正後、該当テストを再実行して確認

### 8.3 テスト実行コマンド一覧

```powershell
# 型チェック
pnpm typecheck

# リンター
pnpm lint

# ユニットテスト（個別ファイル）
cd frontend && pnpm test src/path/to/file.test.ts

# すべてのテスト実行
pnpm test

# E2Eテスト（サーバー起動必須）
pnpm test:e2e
```

### 8.4 コード変更時の注意事項

#### ソースコードを変更したら上書き保存する

ファイルを編集した後、必ず保存してください。保存されていない変更がある場合、テスト結果が不正確になる可能性があります。

#### サーバー側を変更したら、サーバーを再起動する

バックエンドのコード変更後は、必ずサーバーを停止して再起動してください。

```powershell
# サーバーを停止（Ctrl+C）
# サーバーを再起動
pnpm server

# ヘルスチェックで起動確認
curl http://localhost:8000/healthz
```

---

## 9. テストのベストプラクティス

### 9.1 テストの設計原則

#### 9.1.1 単一責任の原則

各テストは1つのことをテストする：

```typescript
// 良い例: 1つのテストで1つのことを検証
it("should delete an element at the specified index", () => {
  expect(arrayDelete([1, 2, 3], 1)).toEqual([1, 3]);
});

// 悪い例: 複数のことを1つのテストで検証
it("should handle array operations", () => {
  expect(arrayDelete([1, 2, 3], 1)).toEqual([1, 3]);
  expect(arrayInsert([1, 2, 3], 1, 4)).toEqual([1, 4, 2, 3]);
  // ...
});
```

#### 9.1.2 テストの独立性

各テストは独立して実行可能であること：

```typescript
beforeEach(() => {
  // 各テスト前に状態をリセット
  store.set(viewStateAtom, { mode: "not-set", cellAnchor: null });
  vi.clearAllMocks();
});
```

#### 9.1.3 明確なテスト名

テスト名は、何をテストしているかが明確であること：

```typescript
// 良い例: 明確なテスト名
it("should return error when mode is invalid", () => {
  // ...
});

// 悪い例: 曖昧なテスト名
it("should work", () => {
  // ...
});
```

### 9.2 アサーションのベストプラクティス

#### 9.2.1 適切なマッチャーの使用

```typescript
// 等価性の検証
expect(result).toEqual(expected);

// 厳密等価性の検証
expect(result).toBe(expected);

// 真偽値の検証
expect(result).toBe(true);
expect(result).toBeTruthy();

// エラーの検証
expect(() => functionUnderTest()).toThrow();
expect(() => functionUnderTest()).toThrow("Error message");
```

#### 9.2.2 部分的なマッチング

```typescript
// オブジェクトの部分的なマッチング
expect(result).toEqual(expect.objectContaining({
  id: 1,
  name: "test",
}));

// 配列の部分的なマッチング
expect(result).toEqual(expect.arrayContaining([1, 2]));
```

### 9.3 モックのベストプラクティス

#### 9.3.1 必要な箇所のみモック

外部依存や副作用のある関数のみをモックし、内部ロジックは可能な限り実際の実装を使用：

```typescript
// 良い例: 外部依存のみをモック
vi.mock("../utils/vitals", () => ({
  reportVitals: vi.fn(),
}));

// 悪い例: 過度なモック
vi.mock("../utils/arrays", () => ({
  arrayDelete: vi.fn(),
  arrayInsert: vi.fn(),
  // ...
}));
```

#### 9.3.2 モックのリセット

各テスト前にモックをリセット：

```typescript
beforeEach(() => {
  vi.clearAllMocks();
});
```

### 9.4 エッジケースのテスト

正常系だけでなく、異常系・エッジケースもテスト：

```typescript
describe("arrayDelete", () => {
  it("should delete an element at the specified index", () => {
    // 正常系
    expect(arrayDelete([1, 2, 3], 1)).toEqual([1, 3]);
  });

  it("should handle empty array", () => {
    // エッジケース: 空配列
    expect(arrayDelete([], 0)).toEqual([]);
  });

  it("should handle out of bounds index", () => {
    // エッジケース: 範囲外のインデックス
    expect(() => arrayDelete([1, 2, 3], 10)).toThrow();
  });

  it("should handle negative index", () => {
    // エッジケース: 負のインデックス
    expect(() => arrayDelete([1, 2, 3], -1)).toThrow();
  });
});
```

### 9.5 テストカバレッジ

新機能や変更箇所には必ずテストを追加し、カバレッジを確保：

```powershell
# カバレッジレポートの生成
pnpm test --coverage
```

---

## 10. トラブルシューティング

### 10.1 よくある問題と解決方法

#### 問題1: テストがタイムアウトする

**原因**: 
- 非同期操作の待機が不適切
- サーバーが起動していない（統合テスト・E2Eテスト）

**解決方法**:
```typescript
// 適切な待機を使用
await waitFor(() => {
  expect(screen.getByText("Data loaded")).toBeInTheDocument();
});

// タイムアウト時間を調整
test.setTimeout(10000); // 10秒
```

#### 問題2: モックが機能しない

**原因**:
- モックのタイミングが間違っている
- モックのリセットが不適切

**解決方法**:
```typescript
// beforeEachでモックをリセット
beforeEach(() => {
  vi.clearAllMocks();
});

// モックを適切なタイミングで設定
vi.mock("../module", () => ({
  functionToMock: vi.fn(),
}));
```

#### 問題3: 状態がリセットされない

**原因**:
- Jotaiストアの状態がリセットされていない
- グローバル状態が残っている

**解決方法**:
```typescript
beforeEach(() => {
  // Jotaiストアをリセット
  store.set(viewStateAtom, { mode: "not-set", cellAnchor: null });
  store.set(codeAtom, undefined);
  // ...
});
```

#### 問題4: E2Eテストが失敗する

**原因**:
- サーバーが起動していない
- サーバーが最新のコードで実行されていない

**解決方法**:
```powershell
# サーバーを再起動
# 1. サーバーを停止（Ctrl+C）
# 2. サーバーを再起動
pnpm server

# ヘルスチェックで起動確認
curl http://localhost:8000/healthz

# E2Eテストを再実行
pnpm test:e2e
```

### 10.2 デバッグのヒント

#### テストの実行を一時停止

```typescript
it("should debug test", async () => {
  // デバッグ用の一時停止
  await new Promise(resolve => setTimeout(resolve, 1000));
  
  // デバッグ情報を出力
  console.log("Debug info:", someValue);
  
  // テストを続行
  expect(result).toEqual(expected);
});
```

#### テストの詳細ログを有効化

```powershell
# 詳細なログを有効化
pnpm test --reporter=verbose
```

#### Playwrightのデバッグモード

```powershell
# Playwrightのデバッグモードで実行
pnpm test:e2e --debug
```

---

## 11. 参考資料

### 11.1 関連ドキュメント

- [プロジェクト仕様書](./project_specification.md): プロジェクトの目的、要件、技術スタック
- [品質ガイドライン](./quality_guidelines.md): 品質保証の基準・プロセス・チェックリスト

### 11.2 外部リソース

- [Vitest公式ドキュメント](https://vitest.dev/): Vitestの詳細なドキュメント
- [React Testing Library公式ドキュメント](https://testing-library.com/react): React Testing Libraryのベストプラクティス
- [Playwright公式ドキュメント](https://playwright.dev/): Playwrightの使い方
- [Testing Library Best Practices](https://kentcdodds.com/blog/common-mistakes-with-react-testing-library): Testing Libraryのベストプラクティス

---

**最終更新**: 2026年1月  
**バージョン**: 1.0.0

