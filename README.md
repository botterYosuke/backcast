# Backcast - Python実行環境

ブラウザ上でPythonコードを入力・実行・表示できるmarimoベースのカスタムフロントエンドアプリケーションです。

## 機能

- ✅ React 19 + TypeScript
- ✅ Vite ビルドシステム
- ✅ **Pyodideによるブラウザ上でのPython実行**
- ✅ **CodeMirrorによるシンタックスハイライト付きコードエディタ**
- ✅ **リアルタイム出力表示**
- ✅ エラーハンドリング
- ✅ モダンなUIデザイン
- ✅ レスポンシブデザイン

## セットアップ

### 依存関係のインストール

```powershell
npm install
```

または

```powershell
pnpm install
```

## 開発

開発サーバーを起動:

```powershell
npm run dev
```

ブラウザで `http://localhost:3000` にアクセスしてください。

## ビルド

本番用ビルド:

```powershell
npm run build
```

ビルド成果物は `dist/` ディレクトリに出力されます。

## プレビュー

ビルド後のプレビュー:

```powershell
npm run preview
```

## 使用方法

1. 開発サーバーを起動後、ブラウザで `http://localhost:3000` にアクセス
2. エディタにPythonコードを入力
3. 「▶ 実行」ボタンをクリックしてコードを実行
4. 出力エリアに結果が表示されます

### サポートされている機能

- 基本的なPython構文
- 標準ライブラリ（sys, math, json等）
- NumPy、Pandas等のPyodide対応パッケージ（要インストール）

### パッケージのインストール

Pyodideで追加パッケージを使用する場合、コード内で`micropip`を使用してインストールできます:

```python
import micropip
await micropip.install("package-name")
```

## 次のステップ

このアプリを拡張して、以下の機能を追加できます:

- 複数ファイルの管理
- コードの保存/読み込み機能
- marimoバックエンドAPIとの連携
- データ可視化機能の追加
- より高度なエディタ機能（補完、リント等）

## ライセンス

MIT

