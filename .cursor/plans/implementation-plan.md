# Electron + Pythonバックエンドサーバー 実装計画

## 概要

`backcast`アプリをElectron + Pythonバックエンドサーバー構成で実装するための詳細な実装計画。

## 実装フェーズ

### フェーズ1: プロジェクトセットアップ（1-2日）

#### 1.1 Electronプロジェクトの初期化
- [ ] Electronとelectron-builderをインストール
- [ ] `electron/` ディレクトリ構造の作成
- [ ] Main Process の基本ファイル作成
  - `electron/main.js`: メインプロセス
  - `electron/preload.js`: プリロードスクリプト
  - `server/server-manager.js`: Pythonサーバー管理

#### 1.2 モノレポ設定
- [ ] `pnpm-workspace.yaml` の作成（ワークスペース設定）
- [ ] ルート `package.json` にワークスペース設定を追加
- [ ] `server/package.json` の作成（必要に応じて）

#### 1.3 ビルド設定の追加
- [ ] `package.json` にElectron関連スクリプトを追加
- [ ] `electron-builder` の設定ファイル作成
- [ ] Viteのビルド設定をElectron用に調整

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
- [ ] Python Embeddable Package の取得方法を調査
- [ ] Pythonランタイムの配置場所を決定（`server/python-runtime/`）
- [ ] Python環境の初期化処理を実装

#### 2.2 サーバー起動管理の実装
- [ ] Pythonサーバープロセスの起動機能
  - 動的ポート割り当て
  - プロセス管理（spawn）
  - 標準出力/エラーのキャプチャ
- [ ] サーバーのヘルスチェック機能
  - `/healthz` エンドポイントへの定期アクセス
  - 起動待機処理
- [ ] サーバーの終了処理
  - 正常終了シグナル送信
  - 強制終了処理（タイムアウト時）

#### 2.3 エラーハンドリング
- [ ] サーバー起動失敗時の処理
- [ ] サーバークラッシュ時の自動再起動
- [ ] ログ出力機能（ファイル出力）

#### 2.4 プロセス間通信（IPC）の実装
- [ ] Main Process → Renderer Process への通信
  - サーバーURLの通知
  - サーバーステータスの通知
- [ ] Renderer Process → Main Process への通信
  - サーバー再起動リクエスト
  - ログ取得リクエスト

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

## 次のアクション

1. **即座に開始**: フェーズ1（プロジェクトセットアップ）
2. **並行作業**: フェーズ2とフェーズ3（サーバー管理とフロントエンド統合）
3. **段階的実装**: 各フェーズを完了してから次へ進む

---

## 参考資料

- [Electron公式ドキュメント](https://www.electronjs.org/docs/latest)
- [electron-builder公式ドキュメント](https://www.electron.build/)
- [Python Embeddable Package](https://www.python.org/downloads/windows/)
- [marimo公式ドキュメント](https://marimo.io/docs)

