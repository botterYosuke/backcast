---
name: pyodideフロントエンド完結型モード追加
overview: "`backcast`にpyodideを使ったフロントエンド完結型モードを追加します。環境変数`PYODIDE=true`で有効化できるようにし、`dev:pyodide`と`build:pyodide`スクリプトを追加します。"
todos:
  - id: add-vite-plugin
    content: vite.config.mtsにHTML変換プラグインを追加して、PYODIDE=trueのときに<marimo-wasm>要素を自動追加
    status: completed
  - id: add-dev-script
    content: package.jsonにdev:pyodideスクリプトを追加（PYODIDE=trueで開発サーバー起動）
    status: completed
  - id: add-build-script
    content: package.jsonにbuild:pyodideスクリプトを追加（PYODIDE=trueで本番ビルド）
    status: completed
  - id: verify-implementation
    content: dev:pyodideとbuild:pyodideが正しく動作し、<marimo-wasm>要素が追加されることを確認
    status: completed
    dependencies:
      - add-vite-plugin
      - add-dev-script
      - add-build-script
---

# py

odideフロントエンド完結型モード追加

## 概要

`backcast`に`marimo`のpyodideを使ったフロントエンド完結型モードを追加します。既存のpyodide関連コードは実装済みのため、環境変数で有効化できるように設定を追加します。

## 実装内容

### 1. Vite設定にHTML変換プラグインを追加

`frontend/vite.config.mts`に、環境変数`PYODIDE=true`のときに`<marimo-wasm>`要素を`index.html`に自動追加するプラグインを実装します。

- `transformIndexHtml`フックを使用
- 既存の`isPyodide`変数（16行目で定義済み）を使用して判定
- `<marimo-wasm hidden></marimo-wasm>`要素を`<marimo-server-token>`の後に追加
- プラグインは`plugins`配列の先頭付近に配置（HTML変換は早期に実行）

実装例：

```typescript
const pyodideHtmlPlugin = (): Plugin => {
  return {
    name: "pyodide-html-plugin",
    transformIndexHtml(html) {
      if (isPyodide) {
        // <marimo-server-token>の後に<marimo-wasm>を追加
        return html.replace(
          /(<marimo-server-token[^>]*>)/,
          `$1\n    <marimo-wasm hidden></marimo-wasm>`
        );
      }
      return html;
    },
  };
};
```

このプラグインを`plugins`配列に追加します。

### 2. package.jsonにスクリプトを追加

`package.json`の`scripts`セクションに以下を追加：

- `dev:pyodide`: `PYODIDE=true`環境変数を設定して開発サーバーを起動
- `build:pyodide`: `PYODIDE=true`環境変数を設定して本番ビルドを実行

既存のスクリプトで`cross-env`を使用しているため、一貫性を保つために`cross-env`を使用します：

```json
{
  "scripts": {
    "dev:pyodide": "cross-env PYODIDE=true pnpm vite",
    "build:pyodide": "cross-env PYODIDE=true vite build --config frontend/vite.config.mts && pnpm build:server && electron-builder"
  }
}
```

注意：`build:pyodide`では、既存の`build`スクリプトと同様に、サーバービルドとelectron-builderも含めます。

### 3. 動作確認

以下の項目を確認します：

**pyodideモード有効時（`PYODIDE=true`）：**

- `pnpm dev:pyodide`で開発サーバーを起動し、pyodideモードが有効化されることを確認
- 開発サーバーのHTTPヘッダーにCOOP/COEPが設定されることを確認（`vite.config.mts`の61-66行目で既に実装済み）
- ブラウザでアプリを開き、`PyodideLoader`がpyodideをロードすることを確認
- `pnpm build:pyodide`でビルドし、生成されたHTML（`dist/index.html`）に`<marimo-wasm>`要素が含まれることを確認

**pyodideモード無効時（`PYODIDE`未設定または`false`）：**

- 通常の`pnpm dev`で開発サーバーを起動し、`<marimo-wasm>`要素が追加されないことを確認
- `isWasm()`関数が`false`を返すことを確認

**Electron環境：**

- Electron環境では`isWasm()`が`false`を返すことを確認（既存コードで対応済み、`frontend/src/core/wasm/utils.ts`の12-18行目）

## 技術的詳細

### HTML要素の追加タイミング

Viteの`transformIndexHtml`フックを使用して、ビルド時（開発サーバー起動時と本番ビルド時）にHTMLを変換します。これにより、環境変数に基づいて動的に`<marimo-wasm>`要素を追加できます。

### 既存コードとの統合

- `isWasm()`関数（`frontend/src/core/wasm/utils.ts`）は`document.querySelector("marimo-wasm")`をチェックするため、要素が追加されれば自動的にpyodideモードが有効化されます
- `PyodideLoader`コンポーネントは既に`app-container.tsx`で使用されているため、追加の統合作業は不要です
- Electron環境では`isWasm()`が`false`を返すため、pyodideモードは自動的に無効化されます（`frontend/src/core/wasm/utils.ts`の12-18行目）

### 開発サーバーのヘッダー設定

`vite.config.mts`の61-66行目で、`isPyodide`が`true`のときに以下のHTTPヘッダーが設定されます：

- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Embedder-Policy: require-corp`

これらのヘッダーは、SharedArrayBufferを使用するために必要です（pyodideの割り込み機能で使用）。この設定は既に実装済みのため、追加の作業は不要です。

## ファイル変更

- `frontend/vite.config.mts`: HTML変換プラグインを追加
- `package.json`: `dev:pyodide`と`build:pyodide`スクリプトを追加

## 注意事項

- 環境変数`PYODIDE=true`を設定しない限り、デフォルトではpyodideモードは無効です
- Electron環境では、`isWasm()`が`false`を返すため、pyodideモードは使用されません（既存のPythonサーバーが使用されます）
- 開発サーバー起動時と本番ビルド時の両方で、Viteの`transformIndexHtml`フックが実行されます
- HTML要素の追加は正規表現による置換で実装します。より安全な方法として、`cheerio`や`jsdom`を使ったHTMLパースも検討できますが、シンプルな正規表現で十分な場合はそのまま使用します
- CI/CDパイプラインで使用する場合は、環境変数`PYODIDE=true`の設定方法を文書化してください