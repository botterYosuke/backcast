---
name: GitHub Actions Electronビルドエラー修正
overview: pnpm workspaceでローカルパッケージの生成ファイルがnode_modulesに反映されない問題を修正。codegen実行後にパッケージを再インストールして、生成されたTypeScriptファイルをnode_modulesに反映させる。
todos:
  - id: add-reinstall-step
    content: deploy-electron.ymlにcodegen実行後の再インストールステップを追加（--forceフラグ付き）
    status: pending
  - id: enhance-verify-step
    content: 検証ステップにnode_modules内のファイル存在確認を追加
    status: pending
    dependencies:
      - add-reinstall-step
  - id: verify-workflow
    content: ワークフローの実行順序が正しいことを確認（codegen → reinstall → verify → build）
    status: pending
    dependencies:
      - enhance-verify-step
---

# GitHub Actions Electronビルドエラー修正

## 問題の原因

エラーメッセージ：

```javascript
Error: [vite]: Rolldown failed to resolve import "@marimo-team/llm-info/models.ts" 
from "/home/runner/work/backcast/backcast/frontend/src/core/ai/model-registry.ts".
```

**根本原因**：

1. `pnpm install`実行時点では`models.ts`と`providers.ts`がまだ生成されていない
2. pnpm workspaceでローカルパッケージ（`file:./packages/llm-info`）をインストールする際、`files`フィールドに基づいてファイルを`node_modules`にコピーする
3. その時点で`data/generated/*.ts`ファイルが存在しないため、コピーされない
4. その後`codegen`でファイルが生成されるが、`node_modules`には既にパッケージがインストール済みのため、新しく生成されたファイルが反映されない
5. ビルド時にRolldownが`node_modules/@marimo-team/llm-info/models.ts`を探すが存在せずエラーになる

## 解決策

`codegen`実行後に`@marimo-team/llm-info`パッケージを再インストールして、生成されたTypeScriptファイルを`node_modules`に反映させる。

## 実装内容

### 1. ワークフローファイルの修正

[`.github/workflows/deploy-electron.yml`](.github/workflows/deploy-electron.yml)に、`codegen`実行後の再インストールステップを追加：

```yaml
- name: Generate LLM info TypeScript files
  run: pnpm --filter @marimo-team/llm-info codegen

- name: Reinstall llm-info package to include generated files
  run: pnpm install --filter @marimo-team/llm-info --force

- name: Verify generated TypeScript files
  shell: bash
  run: |
    echo "=== Checking generated TypeScript files ==="
    ls -lah packages/llm-info/data/generated/ || echo "Directory does not exist"
    if [ -f packages/llm-info/data/generated/models.ts ]; then
      echo "models.ts exists"
      echo "Size: $(wc -c < packages/llm-info/data/generated/models.ts) bytes"
      echo "First 100 chars: $(head -c 100 packages/llm-info/data/generated/models.ts)"
    else
      echo "ERROR: models.ts does not exist"
      exit 1
    fi
    if [ -f packages/llm-info/data/generated/providers.ts ]; then
      echo "providers.ts exists"
      echo "Size: $(wc -c < packages/llm-info/data/generated/providers.ts) bytes"
      echo "First 100 chars: $(head -c 100 packages/llm-info/data/generated/providers.ts)"
    else
      echo "ERROR: providers.ts does not exist"
      exit 1
    fi
    echo "=== Checking files in node_modules ==="
    if [ -f node_modules/@marimo-team/llm-info/data/generated/models.ts ]; then
      echo "✓ models.ts exists in node_modules"
      echo "Size: $(wc -c < node_modules/@marimo-team/llm-info/data/generated/models.ts) bytes"
    else
      echo "ERROR: models.ts does not exist in node_modules"
      exit 1
    fi
    if [ -f node_modules/@marimo-team/llm-info/data/generated/providers.ts ]; then
      echo "✓ providers.ts exists in node_modules"
      echo "Size: $(wc -c < node_modules/@marimo-team/llm-info/data/generated/providers.ts) bytes"
    else
      echo "ERROR: providers.ts does not exist in node_modules"
      exit 1
    fi
```

**修正箇所**：

- 54-55行目の`Generate LLM info TypeScript files`ステップの後に、新しいステップを追加
- 57行目の`Verify generated TypeScript files`ステップの前に挿入

### 2. package.jsonの確認（既に修正済み）

[`packages/llm-info/package.json`](packages/llm-info/package.json)の`files`フィールドは既に`["data/generated"]`に修正されているため、追加の修正は不要。

## 期待される結果

- `codegen`で生成された`models.ts`と`providers.ts`が`node_modules/@marimo-team/llm-info/data/generated/`にコピーされる
- Rolldownが`@marimo-team/llm-info/models.ts`を正しく解決できるようになる
- Electronアプリのビルドが成功する

## 実装の詳細

### `--force`フラグの重要性

- `pnpm install --filter`だけでは、既存のインストールが更新されない場合がある
- `--force`フラグにより、既存のインストールを強制的に更新し、生成ファイルを確実に反映

### 検証ステップの強化

- ソースディレクトリ（`packages/llm-info/data/generated/`）の確認に加えて、`node_modules`内のファイル存在も確認
- これにより、再インストールが成功し、ビルド時にファイルが参照可能であることを検証

## 代替案（フォールバック）

もし`pnpm install --filter @marimo-team/llm-info --force`で解決しない場合：

1. **軽量な代替案**: `pnpm install --force`で全体を再インストール（時間はかかるが確実）
2. **根本的な解決案**: ワークフローを変更して`codegen`を`pnpm install`の前に実行（ただし、依存関係の問題がある可能性があるため、慎重に検討が必要）