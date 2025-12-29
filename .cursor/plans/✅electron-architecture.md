# Electron スタンドアロンアプリ構成プラン

## 概要

`backcast`アプリをElectronでスタンドアロンアプリとして配布するための構成案。
marimoのフロントエンドを使用しているため、Pythonランタイム環境が必要。

## 構成案の比較

### 1. Electron + Pythonバックエンドサーバー（組み込み）

#### 構成
```
Electron App
├── Renderer Process (React/Vite)
│   └── backcast フロントエンド (現在のコード)
└── Main Process
    └── Python バックエンドサーバー管理
        ├── Pythonランタイム（embedded Python または portable Python）
        ├── marimoサーバー起動・管理
        └── プロセス間通信（IPC/HTTP/WebSocket）
```

#### 実装方法
- **Pythonランタイムの同梱**
  - Python Embeddable Package を使用（Windows）
  - PyInstaller でバンドル
  - Portable Python ディストリビューション

- **バックエンドサーバーの起動**
  - Electron Main Process から Python サーバーを起動
  - ローカルホスト（例: `http://localhost:2718`）でサーバーを起動
  - フロントエンドから `runtimeConfig` でサーバーURLを指定

- **プロセス管理**
  - アプリ起動時にPythonサーバーを自動起動
  - アプリ終了時にサーバーを適切に終了
  - クラッシュ時の再起動処理

#### メリット
- ✅ フル機能が使用可能（marimoの全機能）
- ✅ パフォーマンスが良い（ネイティブPython実行）
- ✅ 既存のmarimoサーバーコードをそのまま使用可能
- ✅ 外部依存が少ない（Pythonライブラリのインストールが可能）
- ✅ 開発・保守が比較的容易

#### デメリット
- ❌ アプリサイズが大きくなる（Pythonランタイム + 依存パッケージ）
- ❌ プラットフォームごとにPythonランタイムを用意する必要
- ❌ Python環境の管理が必要（仮想環境、依存パッケージ）
- ❌ 初回起動時のセットアップ時間がかかる可能性

#### 推奨ツール
- **electron-builder**: パッケージング
- **pyenv-win** / **embedded Python**: Pythonランタイム同梱
- **electron-python-exe**: Python実行ファイルのラッパー

---

### 2. Electron + WASMモード（Pyodide）

#### 構成
```
Electron App
├── Renderer Process (React/Vite)
│   ├── backcast フロントエンド
│   └── Pyodide (WASM)
│       └── marimoパッケージ（WASM版）
└── Main Process
    └── 最小限の管理機能
```

#### 実装方法
- **Pyodideの同梱**
  - Pyodideパッケージをアプリに同梱
  - marimo-base パッケージをWASM形式で提供

- **WASMモードの有効化**
  - 現在のコードで `<marimo-wasm>` 要素を追加済み
  - `index.html` でWASMモードを指定
  - ブラウザ内でPythonコードを実行

#### メリット
- ✅ アプリサイズが小さい（Pythonランタイム不要）
- ✅ クロスプラットフォーム対応が容易
- ✅ セキュリティが高い（サンドボックス環境）
- ✅ インターネット接続不要（オフライン動作可能）

#### デメリット
- ❌ パフォーマンスが限定的（WASM実行のオーバーヘッド）
- ❌ Pythonライブラリの互換性が限定的
- ❌ 一部の機能が使用できない可能性
- ❌ marimoパッケージのWASM版が必要（開発が必要）
- ❌ メモリ使用量が大きい可能性

#### 課題
- marimoパッケージのWASM版を用意する必要がある
- `getMarimoWheel` 関数でwheelファイルの配布方法を検討
- CDNまたはアプリ内でのwheelファイル配布

---

### 3. Electron + 組み込みPythonランタイム（軽量版）

#### 構成
```
Electron App
├── Renderer Process (React/Vite)
│   └── backcast フロントエンド
└── Main Process
    ├── 軽量Pythonランタイム（Miniconda/conda-pack）
    └── marimoサーバー（最小構成）
```

#### 実装方法
- **conda-pack を使用**
  - Miniconda環境をパックして同梱
  - 必要なパッケージのみを含める
  - プラットフォーム固有のPython環境をバンドル

- **サーバーの最小化**
  - marimoサーバーの必要な機能のみを起動
  - 不要な機能を除外してサイズを削減

#### メリット
- ✅ 中程度のサイズ（軽量Python環境）
- ✅ フル機能が使用可能
- ✅ 依存パッケージの管理が容易（conda）
- ✅ プラットフォーム固有の最適化が可能

#### デメリット
- ❌ アプリサイズが大きい（conda環境）
- ❌ プラットフォームごとに環境を用意する必要
- ❌ ビルドプロセスが複雑

---

## 採用決定

**採用構成: 構成1（Electron + Pythonバックエンドサーバー）**

検討の結果、以下の理由により構成1を採用することに決定しました：

1. **機能の完全性**: marimoの全機能を使用可能
2. **開発の容易さ**: 既存のmarimoサーバーコードをそのまま使用
3. **パフォーマンス**: ネイティブPython実行による高いパフォーマンス
4. **実績**: 類似アプリ（JupyterLab Desktop等）でも同様の構成が採用されている
5. **拡張性**: Pythonライブラリのインストールが容易で、将来的な機能追加が容易

---

## 推奨構成（参考）

### 推奨: **構成1（Electron + Pythonバックエンドサーバー）**

#### 理由
1. **機能の完全性**: marimoの全機能を使用可能
2. **開発の容易さ**: 既存のmarimoサーバーコードをそのまま使用
3. **パフォーマンス**: ネイティブPython実行による高いパフォーマンス
4. **実績**: 類似アプリ（JupyterLab Desktop等）でも同様の構成が採用されている

#### 実装ステップ
1. **Electronプロジェクトのセットアップ**
   ```bash
   npm install electron electron-builder --save-dev
   ```

2. **Pythonランタイムの同梱**
   - Windows: Python Embeddable Package を使用
   - macOS: Python Framework を同梱
   - Linux: Portable Python ディストリビューション

3. **バックエンドサーバーの起動管理**
   - Main Process でPythonサーバーを起動
   - ポート管理（動的ポート割り当て）
   - サーバーのヘルスチェック

4. **フロントエンドとの統合**
   - `runtimeConfig` でサーバーURLを動的に設定
   - WebSocket接続の確立
   - エラーハンドリング

---

## 実装詳細（構成1の場合）

### ディレクトリ構造（モノレポ構成）
```
backcast/
├── electron/
│   ├── main.js          # Electron Main Process
│   └── preload.js       # Preload Script
├── server/              # Pythonバックエンドサーバー（独立プロジェクト）
│   ├── server-manager.js
│   ├── python-runtime/
│   │   └── python.exe (Windows)
│   └── requirements.txt
├── src/                 # 既存のフロントエンドコード
├── package.json         # ルートパッケージ（ワークスペース設定）
└── pnpm-workspace.yaml  # pnpmワークスペース設定
```

### Main Process の責務
1. Pythonサーバーの起動・終了管理
2. ポートの動的割り当て
3. サーバーのヘルスチェック
4. フロントエンドへのサーバーURL通知（IPC経由）

### フロントエンドの変更点
1. `runtimeConfig` を Electron IPC から取得
2. サーバーURLを動的に設定
3. エラーハンドリングの強化

### パッケージング
- **electron-builder** を使用
- Pythonランタイムを `resources/` に配置
- プラットフォームごとの設定ファイル

---

## 検討事項

### 1. Pythonバージョン
- Python 3.12+ を推奨（marimoの要件）
- プラットフォームごとに適切なバージョンを選択

### 2. marimoパッケージのインストール
- 初回起動時に仮想環境を作成
- `pip install marimo` を実行
- または、事前にパッケージを同梱

### 3. ユーザーデータの管理
- ノートブックファイルの保存場所
- 設定ファイルの保存場所
- プラットフォームごとの適切なディレクトリを選択

### 4. アップデート機構
- Electronアプリのアップデート
- Pythonパッケージのアップデート
- 自動更新機能の実装

### 5. セキュリティ
- ローカルサーバーのセキュリティ
- サンドボックス環境の考慮
- コード署名（配布時）

---

## 次のステップ

1. **プロトタイプの作成**
   - 最小限のElectronアプリを作成
   - Pythonサーバーの起動・終了を実装
   - フロントエンドとの通信を確立

2. **パッケージングの検証**
   - electron-builder でパッケージング
   - サイズ・パフォーマンスの測定
   - 各プラットフォームでの動作確認

3. **実装の詳細化**
   - エラーハンドリングの強化
   - ログ機能の実装
   - 設定管理機能の実装

