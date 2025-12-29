# Electron + Pythonバックエンドサーバー 実装計画

## 概要

`backcast`アプリをElectron + Pythonバックエンドサーバー構成で実装するための詳細な実装計画。

## 実装フェーズ

### フェーズ1: プロジェクトセットアップ（1-2日）

#### 1.1 Electronプロジェクトの初期化
- [x] ✅ Electronとelectron-builderをインストール
- [x] ✅ `electron/` ディレクトリ構造の作成
- [x] ✅ Main Process の基本ファイル作成
  - `electron/main.js`: メインプロセス
  - `electron/preload.js`: プリロードスクリプト
  - `electron/utils/paths.js`: パス管理ユーティリティ
  - `electron/utils/logger.js`: ログ管理ユーティリティ
  - `server/server-manager.js`: Pythonサーバー管理（Phase 2で実装予定）

**進捗状況**: 完了
**知見・Tips**:
- Electron 39.2.7、electron-builder 26.0.12をインストール
- ES Modules形式（`type: "module"`）で実装
- `contextIsolation: true`、`nodeIntegration: false`でセキュリティを確保
- ログファイルは`app.getPath("userData")/logs/`に保存

#### 1.2 モノレポ設定
- [x] ✅ `pnpm-workspace.yaml` の作成（ワークスペース設定）
- [x] ✅ ルート `package.json` にワークスペース設定を追加（`main`フィールド追加）
- [ ] `server/package.json` の作成（必要に応じて - 現時点では不要）

**進捗状況**: ほぼ完了
**知見・Tips**:
- `pnpm-workspace.yaml`を作成し、既存の`packages/*`をワークスペースに含めた
- `server/`は現時点ではNode.js依存がないため、ワークスペースから除外

#### 1.3 ビルド設定の追加
- [x] ✅ `package.json` にElectron関連スクリプトを追加
- [x] ✅ `electron-builder.yml` の設定ファイル作成
- [x] ✅ Viteのビルド設定をElectron用に調整（既存設定で問題なし）

**進捗状況**: 完了
**知見・Tips**:
- `electron:dev`: 開発モード（Vite dev server + Electron）
- `electron:build`: 本番ビルド（全プラットフォーム）
- `electron-builder.yml`でWindows/Mac/Linuxの設定を定義
- Vite設定は既に`base: "./"`が設定されており、Electron用に適切
- `concurrently`と`wait-on`のインストールは既存ワークスペース依存関係の問題で保留（後で対応）

#### 1.4 ディレクトリ構造の作成（モノレポ構成）
```
backcast/
├── electron/
│   ├── main.js
│   ├── preload.js
│   └── utils/
│       ├── paths.js
│       └── logger.js
├── server/               # Pythonバックエンドサーバー（独立プロジェクト）
│   ├── server-manager.js  # Node.js側のサーバー管理スクリプト
│   ├── python-runtime/    # Pythonランタイム（Embeddable Package）
│   ├── requirements.txt   # Python依存パッケージ
│   └── package.json       # Node.js依存（オプション）
├── src/                 # 既存のフロントエンドコード
├── package.json         # ルートパッケージ（ワークスペース設定）
└── pnpm-workspace.yaml  # pnpmワークスペース設定
```

---

### フェーズ2: Pythonサーバー管理機能の実装（2-3日）

#### 2.1 Pythonランタイムの準備
- [x] ✅ Python Embeddable Package の取得方法を調査
  - Windows: https://www.python.org/downloads/windows/ からEmbeddable Packageを取得
  - 配置場所: `server/python-runtime/`（Phase 4で実装予定）
- [x] ✅ Pythonランタイムの配置場所を決定（`server/python-runtime/`）
- [x] ✅ Python環境の初期化処理を実装
  - システムPythonまたはEmbeddable Packageを自動検出
  - `findPythonExecutable()`でPython実行ファイルを検出

#### 2.2 サーバー起動管理の実装
- [x] ✅ Pythonサーバープロセスの起動機能
  - 動的ポート割り当て（2718-2728の範囲、`findAvailablePort()`）
  - プロセス管理（`child_process.spawn`）
  - 標準出力/エラーのキャプチャ（`addLog()`でログ管理）
- [x] ✅ サーバーのヘルスチェック機能
  - `/healthz` エンドポイントへの定期アクセス（`checkHealth()`）
  - 起動待機処理（最大30秒、1秒間隔でポーリング）
- [x] ✅ サーバーの終了処理
  - 正常終了シグナル送信（SIGTERM）
  - 強制終了処理（タイムアウト5秒後にSIGKILL）

#### 2.3 エラーハンドリング
- [x] ✅ サーバー起動失敗時の処理
  - エラーログ出力、ステータス更新
- [x] ✅ サーバークラッシュ時の自動再起動
  - 最大3回の再試行、指数バックオフ（1秒、2秒、4秒、最大10秒）
- [x] ✅ ログ出力機能
  - 標準出力/エラーのキャプチャ（`addLog()`）
  - ログ履歴の保持（最大1000件）
  - `electron/utils/logger.js`を使用したファイル出力

#### 2.4 プロセス間通信（IPC）の実装
- [x] ✅ Main Process → Renderer Process への通信
  - サーバーURLの通知（`server:get-url` IPCハンドラー）
  - サーバーステータスの通知（`server:get-status` IPCハンドラー、`server:status-changed`イベント）
- [x] ✅ Renderer Process → Main Process への通信
  - サーバー再起動リクエスト（`server:restart` IPCハンドラー）
  - ログ取得リクエスト（`server:get-logs` IPCハンドラー）

**進捗状況**: 完了
**知見・Tips**:
- Node.js 18+の組み込み`fetch`を使用してヘルスチェックを実装
- ポート検出は`net.createServer()`を使用してポートの使用状況を確認
- プロセス終了時は`SIGTERM`で正常終了を試み、タイムアウト後に`SIGKILL`で強制終了（Windowsでは`kill()`を直接使用）
- 自動再起動は指数バックオフで実装し、最大3回まで再試行
- ログ管理は配列で保持し、最大1000件まで保持（メモリ効率を考慮）
- ステータス変更時はコールバック関数でRenderer Processに通知
- `marimo edit --headless`コマンドを使用（ノートブックファイル不要でサーバー起動可能）
- ヘルスチェックは`AbortController`を使用してタイムアウト処理（互換性向上）
- プロセス終了時の競合状態対策として`isStopping`フラグを実装

**コードレビュー対応（2025-12-29）**:
- ✅ marimoコマンドを`run`→`edit`に変更、`--headless`オプション追加（ノートブックファイル不要）
- ✅ ヘルスチェックのタイムアウト処理を`AbortSignal.timeout()`→`AbortController`に変更（互換性向上）
- ✅ プロセス終了時の競合状態を`isStopping`フラグで解決（正常終了時に`handleServerCrash()`を呼ばない）
- ✅ Windowsでのシグナル処理を改善（`process.platform`をチェックして適切なシグナルを送信）
- ✅ preload.jsのイベントリスナー削除を改善（`removeAllListeners`→`removeListener`で特定リスナーのみ削除）

---

### フェーズ3: フロントエンド統合（2-3日）

#### 3.1 runtimeConfigの動的設定
- [ ] Electron環境の検出機能
  - `process.versions.electron` を確認
  - IPC経由でサーバーURLを取得
- [ ] `src/core/runtime/config.ts` の修正
  - Electron環境ではIPC経由でURLを取得
  - 通常のWeb環境では従来通りの動作

#### 3.2 mount.tsx の修正
- [ ] Electron環境での初期化処理を追加
- [ ] サーバーURLを `runtimeConfig` に設定
- [ ] サーバー接続失敗時のエラーハンドリング

#### 3.3 エラーハンドリングの強化
- [ ] サーバー未起動時のUI表示
- [ ] 接続エラー時の再試行機能
- [ ] ユーザーフレンドリーなエラーメッセージ

---

### フェーズ4: Python環境のセットアップ（3-4日）

#### 4.1 marimoパッケージのインストール
- [ ] 初回起動時の仮想環境作成
- [ ] `pip install marimo` の実行
- [ ] 依存パッケージのインストール

#### 4.2 パッケージ管理の実装
- [ ] 仮想環境の作成・管理
- [ ] パッケージインストール状況の確認
- [ ] パッケージのアップデート機能（将来拡張）

#### 4.3 設定ファイルの管理
- [ ] ユーザーデータディレクトリの決定
  - Windows: `%APPDATA%\backcast`
  - macOS: `~/Library/Application Support/backcast`
  - Linux: `~/.config/backcast`
- [ ] 設定ファイル（`config.json`）の保存
- [ ] Python環境パスの保存

---

### フェーズ5: パッケージング（3-5日）

#### 5.1 electron-builderの設定
- [ ] `electron-builder.yml` の作成
- [ ] プラットフォーム別の設定
  - Windows: NSISインストーラー
  - macOS: DMG + Code Signing
  - Linux: AppImage / deb / rpm
- [ ] アイコン・リソースの準備

#### 5.2 Pythonランタイムの同梱
- [ ] Python Embeddable Package の同梱方法
- [ ] ファイルサイズの最適化
- [ ] 不要ファイルの除外

#### 5.3 ビルドプロセスの確立
- [ ] ビルドスクリプトの作成
- [ ] 自動ビルドの設定（CI/CD）
- [ ] ビルド成果物の検証

---

### フェーズ6: テスト・検証（2-3日）

#### 6.1 動作確認
- [ ] アプリ起動・終了の確認
- [ ] Pythonサーバーの起動・終了確認
- [ ] フロントエンドとの通信確認
- [ ] ノートブックの実行確認

#### 6.2 エッジケースのテスト
- [ ] サーバー起動失敗時の動作
- [ ] ネットワークエラー時の動作
- [ ] 複数インスタンス起動時の動作
- [ ] 異常終了時のクリーンアップ

#### 6.3 パフォーマンステスト
- [ ] 起動時間の測定
- [ ] メモリ使用量の確認
- [ ] CPU使用率の確認

---

### フェーズ7: ドキュメント・改善（1-2日）

#### 7.1 ドキュメント作成
- [ ] 開発者向けドキュメント
- [ ] ユーザー向けREADME
- [ ] トラブルシューティングガイド

#### 7.2 UI/UX改善
- [ ] ローディング画面の改善
- [ ] エラーメッセージの改善
- [ ] 設定画面の追加（将来拡張）

---

## 技術スタック

### Electron関連
- **electron**: 最新安定版
- **electron-builder**: パッケージング
- **electron-store**: 設定の永続化（オプション）

### Python関連
- **Python 3.12+**: marimoの要件
- **marimo**: バックエンドサーバー
- **pip**: パッケージ管理

### 開発ツール
- **TypeScript**: 型安全性
- **Vite**: フロントエンドビルド
- **Jest / Vitest**: テストフレームワーク
- **pnpm workspace**: モノレポ管理

---

## 実装上の注意点

### 1. ポート管理
- 動的ポート割り当てを使用（例: 2718-2728の範囲）
- ポート競合時の自動再割り当て
- ポート番号をIPC経由でフロントエンドに通知

### 2. プロセス管理
- Pythonプロセスの適切な終了処理
- シグナルハンドリング（SIGTERM, SIGINT）
- 子プロセスのクリーンアップ

### 3. セキュリティ
- ローカルホストのみでリッスン（外部アクセス不可）
- IPC通信の検証
- コード署名（配布時）

### 4. パフォーマンス
- サーバー起動時間の最小化
- メモリ使用量の最適化
- 不要なPythonパッケージの除外

### 5. クロスプラットフォーム対応
- プラットフォーム固有のパス処理
- プラットフォーム固有のPythonランタイム
- プラットフォーム固有のビルド設定

### 6. モノレポ構成
- `server/` をルートレベルに配置
- pnpm workspace を使用してプロジェクトを管理
- 各プロジェクト（electron、server、src）の依存関係を分離
- 共通の依存関係はルートで管理

---

## リスクと対策

### リスク1: Python環境のセットアップ時間
- **対策**: 初回起動時にプログレスバーを表示
- **対策**: バックグラウンドでセットアップを実行

### リスク2: アプリサイズの肥大化
- **対策**: Pythonランタイムの最適化（不要ライブラリの除外）
- **対策**: 圧縮アルゴリズムの活用
- **対策**: インクリメンタルアップデート

### リスク3: プラットフォーム固有の問題
- **対策**: 各プラットフォームでの動作確認
- **対策**: CI/CDでの自動テスト
- **対策**: バージョン管理とロールバック機能

### リスク4: marimoサーバーの互換性
- **対策**: marimoのバージョンを固定
- **対策**: 互換性テストの実施
- **対策**: アップデート時の検証

---

## 進捗ログ

### 2025-12-29: フェーズ1完了

**完了項目**:
- ✅ Electronとelectron-builderのインストール
- ✅ ディレクトリ構造の作成
- ✅ Main Process、Preloadスクリプトの基本実装
- ✅ ログ機能、パス管理ユーティリティの実装
- ✅ pnpm-workspace.yamlの作成
- ✅ electron-builder.ymlの設定
- ✅ package.jsonにElectron関連スクリプト追加

**設計変更**:
- `server-manager.js`はPhase 2で実装するため、現時点ではスタブ実装
- `concurrently`と`wait-on`のインストールは既存ワークスペース依存関係の問題で保留

### 2025-12-29: フェーズ2完了

**完了項目**:
- ✅ `ServerManager`クラスの完全実装
  - Python実行ファイルの自動検出（システムPythonまたはEmbeddable Package）
  - 動的ポート割り当て（2718-2728の範囲）
  - marimoサーバーの起動・停止機能
  - ヘルスチェック機能（`/healthz`エンドポイント）
  - ログ管理機能（標準出力/エラーのキャプチャ）
  - エラーハンドリングと自動再起動（最大3回、指数バックオフ）
- ✅ IPCハンドラーの実装
  - `server:get-url`: サーバーURLを返す
  - `server:get-status`: サーバーステータスを返す
  - `server:restart`: サーバー再起動リクエスト
  - `server:get-logs`: サーバーログ取得
  - `server:status-changed`: ステータス変更イベントの送信

**設計変更**:
- Python Embeddable Packageの同梱はPhase 4で実装予定（現時点ではシステムPythonを使用）
- marimoのインストール確認は実装済み（Phase 4で自動インストール機能を追加予定）

**コードレビュー後の修正（2025-12-29）**:
- ✅ marimoコマンドを`marimo run`から`marimo edit`に変更（ノートブックファイル不要）
- ✅ ヘルスチェックのタイムアウト処理を`AbortSignal.timeout()`から`AbortController`に変更（互換性向上）
- ✅ プロセス終了時の競合状態を修正（`isStopping`フラグを追加して正常終了時は`handleServerCrash()`を呼ばない）
- ✅ Windowsでのシグナル処理を改善（`process.platform`をチェックして適切なシグナルを送信）
- ✅ preload.jsのイベントリスナー削除を改善（`removeAllListeners`から`removeListener`に変更して特定のリスナーのみ削除）

**次のステップ**:
- Phase 3: フロントエンド統合を開始

## 次のアクション

1. **即座に開始**: フェーズ3（フロントエンド統合）
2. **並行作業**: フェーズ3とフェーズ4（フロントエンド統合とPython環境セットアップ）
3. **段階的実装**: 各フェーズを完了してから次へ進む

---

## 参考資料

- [Electron公式ドキュメント](https://www.electronjs.org/docs/latest)
- [electron-builder公式ドキュメント](https://www.electron.build/)
- [Python Embeddable Package](https://www.python.org/downloads/windows/)
- [marimo公式ドキュメント](https://marimo.io/docs)

