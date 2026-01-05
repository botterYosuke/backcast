---
name: JSONファイル生成の有効化
overview: GitHub Actionsのビルドエラーを修正するため、`generate.ts`でコメントアウトされているJSONファイル生成を有効化します。
todos: []
---

# Gi

tHub Actionsビルドエラー修正: JSONファイル生成の有効化

## 問題の概要

GitHub ActionsのElectronアプリビルドで以下のエラーが発生しています：

```javascript
Error: [vite]: Rolldown failed to resolve import "@marimo-team/llm-info/models.json" from "/home/runner/work/backcast/backcast/frontend/src/core/ai/model-registry.ts".
```

## 原因

1. `frontend/src/core/ai/model-registry.ts`が`@marimo-team/llm-info/models.json`と`@marimo-team/llm-info/providers.json`をインポートしている
2. `packages/llm-info/package.json`の`exports`フィールドが`.json`ファイルを参照している
3. しかし、`packages/llm-info/src/generate.ts`でJSONファイルの生成がコメントアウトされている（119行目と125行目）

## 解決策

`packages/llm-info/src/generate.ts`でコメントアウトされているJSONファイル生成コードを有効化します。

## 変更内容

### 1. `packages/llm-info/src/generate.ts`の修正

119行目と125行目のコメントアウトを解除し、JSONファイルを生成するようにします：

**修正前（119-121行目）:**

```typescript
const models = loadAndValidateModels(modelsYamlPath);
// .jsonファイルは生成しない（.tsファイルのみを使用）
// writeJsonFile(modelsJsonPath, { models: models });
writeTsFile(modelsTsPath, "models", models);
```

**修正後:**

```typescript
const models = loadAndValidateModels(modelsYamlPath);
writeJsonFile(modelsJsonPath, { models: models });  // コメント解除
writeTsFile(modelsTsPath, "models", models);
```

**修正前（124-127行目）:**

```typescript
const providers = loadAndValidateProviders(providersYamlPath);
// .jsonファイルは生成しない（.tsファイルのみを使用）
// writeJsonFile(providersJsonPath, { providers: providers });
writeTsFile(providersTsPath, "providers", providers);
```

**修正後:**

```typescript
const providers = loadAndValidateProviders(providersYamlPath);
writeJsonFile(providersJsonPath, { providers: providers });  // コメント解除
writeTsFile(providersTsPath, "providers", providers);
```

### 2. JSONファイルの構造について

`generate.ts`の113-115行目のコメントによると、Viteなどのバンドラーの互換性のために、JSONファイルはネストされた形式（`{ models: [...] }`と`{ providers: [...] }`）で生成されます。これは`model-registry.ts`のインポート方法と一致しています：

- `import { models } from "@marimo-team/llm-info/models.json";` → `{ models: [...] }`形式を期待
- `import { providers } from "@marimo-team/llm-info/providers.json";` → `{ providers: [...] }`形式を期待

既に`writeJsonFile`関数は正しい形式でJSONファイルを生成するため、コメントアウトを解除するだけで問題は解決します。

## 実装手順

1. **`packages/llm-info/src/generate.ts`を修正**

   - 119行目の`writeJsonFile(modelsJsonPath, { models: models });`のコメントアウトを解除
   - 125行目の`writeJsonFile(providersJsonPath, { providers: providers });`のコメントアウトを解除
   - 118行目と124行目の「.jsonファイルは生成しない」コメントも削除

2. **ローカルで検証**

   - `pnpm --filter @marimo-team/llm-info codegen`を実行
   - `packages/llm-info/data/generated/`ディレクトリに以下が生成されることを確認：
     - `models.ts`（既存）
     - `providers.ts`（既存）
     - `models.json`（新規生成）
     - `providers.json`（新規生成）
   - 生成されたJSONファイルの構造を確認：
     - `models.json`が`{ models: [...] }`の形式であること
     - `providers.json`が`{ providers: [...] }`の形式であること

3. **ビルドテスト**

   - フロントエンドのビルド（`pnpm build`）が成功することを確認
   - `model-registry.ts`がJSONファイルを正しくインポートできることを確認

## 検証ポイント

- ✅ JSONファイルが正しく生成される（`{ models: [...] }`と`{ providers: [...] }`の形式）
- ✅ `model-registry.ts`の`import { models } from "..."`が正しく動作する
- ✅ GitHub Actionsでのビルドが成功する（ステップ9の`codegen`実行後にJSONファイルが生成される）

## 推奨される追加改善（オプション）

### GitHub Actionsの検証ステップ強化

現在のステップ10「Verify generated TypeScript files」では`.ts`ファイルのみを確認していますが、JSONファイルの存在確認も追加することを推奨します：

```yaml
- name: Verify generated TypeScript files
  shell: bash
  run: |
    echo "=== Checking generated TypeScript files ==="
    # ... 既存の.tsファイル確認 ...
    
    echo "=== Checking generated JSON files ==="
    if [ -f packages/llm-info/data/generated/models.json ]; then
      echo "models.json exists"
      echo "Size: $(wc -c < packages/llm-info/data/generated/models.json) bytes"
    else
      echo "ERROR: models.json does not exist"
      exit 1
    fi
    if [ -f packages/llm-info/data/generated/providers.json ]; then
      echo "providers.json exists"
      echo "Size: $(wc -c < packages/llm-info/data/generated/providers.json) bytes"
    else
      echo "ERROR: providers.json does not exist"
      exit 1
    fi
```

**注意**: この改善はオプションです。必須の修正（`generate.ts`のコメント解除）のみでも問題は解決しますが、検証を強化することで早期に問題を発見できます。

## 影響範囲

- `packages/llm-info/src/generate.ts`: JSONファイル生成コードの有効化
- 生成されるファイル: `packages/llm-info/data/generated/models.json`と`packages/llm-info/data/generated/providers.json`

## 注意事項

- ✅ JSONファイルの構造は既に正しい形式（ネストされた形式）で生成されるため、追加の変更は不要です
  - `writeJsonFile`関数は`JSON.stringify(data, null, 2)`を実行するため、`{ models: models }`を渡せば`{ models: [...] }`形式で生成されます
- ✅ `package.json`の`exports`フィールドは既に正しく設定されているため、変更は不要です
- ✅ `package.json`の`files`フィールド（8行目）に`data/generated/*.json`が含まれているため、パッケージ公開時にJSONファイルが含まれます
- ✅ GitHub Actionsのステップ9で`codegen`が実行されているため、修正後は自動的にJSONファイルが生成されます
- ✅ `.ts`ファイルと`.json`ファイルの両方が生成されますが、これは意図された動作です（`.ts`ファイルは型安全性のため、`.json`ファイルはVite/Rolldownでのインポートのため）

## 修正箇所の要約

以下の3箇所を修正します：

1. **`packages/llm-info/src/generate.ts`の119-120行目**

   - `writeJsonFile(modelsJsonPath, { models: models });`のコメント解除

2. **`packages/llm-info/src/generate.ts`の125-126行目**

   - `writeJsonFile(providersJsonPath, { providers: providers });`のコメント解除

3. **`packages/llm-info/src/generate.ts`の118行目と124行目**

   - 「.jsonファイルは生成しない（.tsファイルのみを使用）」のコメント行を削除