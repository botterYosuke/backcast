---
name: frontendフォルダ構成への対応
overview: フロントエンドソースを`frontend`フォルダに移動したことに伴い、Vite設定、package.jsonスクリプト、Electron設定を更新して、正しく動作するようにします。
todos:
  - id: update_package_json
    content: package.jsonのスクリプトをfrontendフォルダで実行するように更新（vite, build, preview, typecheck）
    status: completed
  - id: update_vite_config
    content: frontend/vite.config.mtsのビルド出力先を../distに設定し、packages/llm-infoへのパス参照を修正
    status: completed
  - id: update_electron_main
    content: electron/main.jsのpublicフォルダ参照をfrontend/publicに変更
    status: completed
  - id: update_electron_builder
    content: electron-builder.ymlのアイコンパスをfrontend/publicに変更
    status: completed
---

# f

rontendフォルダ構成への対応計画

## 変更概要

フロントエンドソースが`frontend/`フォルダに移動されたため、以下の設定を更新します：

1. **package.jsonスクリプトの更新**: Viteコマンドを`frontend`フォルダで実行するように変更
2. **vite.config.mtsの修正**: ビルド出力先をルートの`dist`に設定し、パス解決を修正
3. **electron/main.jsの修正**: `public`フォルダの参照を`frontend/public`に変更
4. **electron-builder.ymlの修正**: アイコンパスを`frontend/public`に変更

## 変更ファイル

### 1. [package.json](package.json)

**スクリプトの変更**:

- `vite`: `vite` → `vite --config frontend/vite.config.mts`
- `build`: `cross-env vite build` → `cross-env vite build --config frontend/vite.config.mts`
- `preview`: `vite preview` → `vite preview --config frontend/vite.config.mts`
- `typecheck`: `tsc --noEmit` → `tsc --project frontend/tsconfig.json`（推奨：明示的で明確）

### 2. [frontend/vite.config.mts](frontend/vite.config.mts)

**ビルド出力先の設定**:

- `build`セクション（332-335行目）に`outDir: "../dist"`を追加（ルートの`dist`フォルダに出力）

**パス解決の修正**:

- `packages/llm-info`への参照を`../packages/llm-info`に変更
- `path.resolve(__dirname, "./packages/llm-info/...")`を`path.resolve(__dirname, "../packages/llm-info/...")`に変更
- 該当箇所（すべて`../packages/llm-info`に変更）：
- **40行目**: `svgInlinePlugin`内の`"./packages/llm-info/icons"` → `"../packages/llm-info/icons"`
- **51-54行目**: `jsonImportPlugin`内の4つのパス定義：
- `"./packages/llm-info/data/generated/models.json"` → `"../packages/llm-info/data/generated/models.json"`
- `"./packages/llm-info/data/generated/providers.json"` → `"../packages/llm-info/data/generated/providers.json"`
- `"./packages/llm-info/data/generated/models.ts"` → `"../packages/llm-info/data/generated/models.ts"`
- `"./packages/llm-info/data/generated/providers.ts"` → `"../packages/llm-info/data/generated/providers.ts"`
- **106-107行目**: `jsonImportPlugin`内の相対パス文字列（`path.resolve`に渡される前に使用）：
- `'./packages/llm-info/data/generated/models.json'` → `'../packages/llm-info/data/generated/models.json'`
- `'./packages/llm-info/data/generated/providers.json'` → `'../packages/llm-info/data/generated/providers.json'`
- **126-127行目**: `jsonImportPlugin`内の`path.resolve`呼び出し：
- `path.resolve(__dirname, './packages/llm-info/data/generated/models.json')` → `path.resolve(__dirname, '../packages/llm-info/data/generated/models.json')`
- `path.resolve(__dirname, './packages/llm-info/data/generated/providers.json')` → `path.resolve(__dirname, '../packages/llm-info/data/generated/providers.json')`
- **215-216行目**: `jsonImportPlugin`内の`load`関数内の`path.resolve`呼び出し：
- `path.resolve(__dirname, './packages/llm-info/data/generated/models.json')` → `path.resolve(__dirname, '../packages/llm-info/data/generated/models.json')`
- `path.resolve(__dirname, './packages/llm-info/data/generated/providers.json')` → `path.resolve(__dirname, '../packages/llm-info/data/generated/providers.json')`
- **340行目**: `resolve.alias`の`"./packages/llm-info/icons"` → `"../packages/llm-info/icons"`

**注意**: `node_modules`への参照（120-121行目）は変更不要です。また、文字列マッチング（`id.endsWith('/packages/llm-info/...')`など）は動的なパスマッチングのため、実際のファイルパスを変更すれば自動的に対応されます。

### 3. [electron/main.js](electron/main.js)

**publicフォルダの参照を修正**:

- 34行目: `icon: path.join(getAppRoot(), "public", "logo.png")` → `icon: path.join(getAppRoot(), "frontend", "public", "logo.png")`

### 4. [electron-builder.yml](electron-builder.yml)

**アイコンパスの修正**:

- 33行目: `icon: public/logo.png` → `icon: frontend/public/logo.png`
- 42行目: `icon: public/android-chrome-512x512.png` → `icon: frontend/public/android-chrome-512x512.png`
- 51行目: `icon: public/logo.png` → `icon: frontend/public/logo.png`

## 実装手順

1. `package.json`のスクリプトを更新（`vite`, `build`, `preview`, `typecheck`）
2. `frontend/vite.config.mts`のビルド出力先（`build.outDir`）を追加
3. `frontend/vite.config.mts`のパス解決を修正（上記の全箇所を`../packages/llm-info`に変更）
4. `electron/main.js`の`public`フォルダ参照を修正
5. `electron-builder.yml`のアイコンパスを修正

## 注意事項

- `vite.config.mts`内のパス修正は、`path.resolve`を使用している箇所と文字列リテラルを使用している箇所の両方を含みます
- `node_modules`への参照は変更不要です