---
name: Firebase Hostingデプロイワークフロー追加
overview: "`backcast`のpyodide版をFirebase HostingにデプロイするGitHub ActionsワークフローとFirebase設定ファイルを作成します。参考プロジェクト`BackcastPro-Steam`の構造を基に、pyodide版に必要なCOOP/COEPヘッダー設定を含めます。"
todos:
  - id: create-firebase-config
    content: firebase.jsonを作成し、COOP/COEPヘッダーとSPA用のrewrites設定を追加
    status: completed
  - id: create-deploy-workflow
    content: .github/workflows/deploy-web.ymlを作成し、pyodide版のビルドとFirebase Hostingへのデプロイを実装
    status: completed
  - id: adjust-build-command
    content: ワークフロー内でpyodide版のビルドコマンドを調整（サーバービルドとelectron-builderを除外）
    status: completed
    dependencies:
      - create-deploy-workflow
---

#Firebase Hostingデプロイワークフロー追加

## 概要

`backcast`のpyodide版をFirebase HostingにデプロイするためのGitHub ActionsワークフローとFirebase設定ファイルを作成します。参考プロジェクト`BackcastPro-Steam`の構造を基に、pyodide版に必要なHTTPヘッダー（COOP/COEP）設定を含めます。

## 実装内容

### 1. Firebase Hosting設定ファイルの作成

`firebase.json`を作成し、以下の設定を追加：

- **public**: `dist`ディレクトリを指定（pyodide版のビルド出力）
- **headers**: COOP/COEPヘッダーを設定（SharedArrayBufferに必要）
- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Embedder-Policy: require-corp`
- **rewrites**: SPA用のルーティング設定（すべてのリクエストを`/index.html`にリダイレクト）
- **ignore**: 不要なファイルを除外

### 2. GitHub Actionsワークフローの作成

`.github/workflows/deploy-web.yml`を作成し、以下のステップを実装：

1. **リポジトリのチェックアウト**
2. **pnpmのセットアップ**（既存のワークフローと同様にversion 8を使用）
3. **Node.js 20のセットアップ**（pnpmキャッシュを使用）
4. **依存関係のインストール**（`pnpm install`）
5. **LLM info TypeScriptファイルの生成**（`pnpm --filter @marimo-team/llm-info codegen`）
6. **pyodide版のビルド**（`pnpm build:pyodide` - ただし、サーバービルドとelectron-builderは除外）
7. **ビルド出力の検証**（`dist`ディレクトリの確認）
8. **Firebase CLIのインストール**（`npm install -g firebase-tools`）
9. **Firebase Hostingへのデプロイ**（シークレットを使用）

### 3. ビルドスクリプトの調整

`build:pyodide`スクリプトは現在、サーバービルドとelectron-builderも含んでいますが、Webデプロイでは不要です。ワークフロー内で直接Viteビルドコマンドを実行するか、新しいスクリプト`build:pyodide:web`を追加することを検討します。

## 技術的詳細

### Firebase設定ファイルの構造

```json
{
  "hosting": {
    "public": "dist",
    "ignore": [
      "firebase.json",
      "**/.*",
      "**/node_modules/**"
    ],
    "headers": [
      {
        "source": "**",
        "headers": [
          {
            "key": "Cross-Origin-Opener-Policy",
            "value": "same-origin"
          },
          {
            "key": "Cross-Origin-Embedder-Policy",
            "value": "require-corp"
          }
        ]
      }
    ],
    "rewrites": [
      {
        "source": "**",
        "destination": "/index.html"
      }
    ]
  }
}
```



### ワークフローのトリガー

- **push**: `main`ブランチへのプッシュ時に自動実行
- **workflow_dispatch**: 手動実行も可能

### 必要なGitHub Secrets

- `FIREBASE_TOKEN`: Firebase CLI認証トークン
- `FIREBASE_PROJECT_ID`: FirebaseプロジェクトID

## ファイル変更

- `firebase.json`: 新規作成（Firebase Hosting設定）
- `.github/workflows/deploy-web.yml`: 新規作成（GitHub Actionsワークフロー）

## 注意事項

- `build:pyodide`スクリプトはサーバービルドとelectron-builderも含むため、ワークフロー内で直接Viteビルドコマンドを実行するか、ビルドスクリプトを調整する必要があります