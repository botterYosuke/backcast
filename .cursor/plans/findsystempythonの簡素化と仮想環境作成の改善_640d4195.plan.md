---
name: findSystemPythonの簡素化と仮想環境作成の改善
overview: "`findSystemPython`関数を環境変数のチェックのみに簡素化し、PATH検索やフォールバック処理を削除します。環境変数にPythonが見つからない場合のエラーハンドリングも改善します。"
todos:
  - id: simplify-findSystemPython
    content: findSystemPython()関数からPATH検索とフォールバック処理を削除し、環境変数チェックのみに簡素化
    status: pending
  - id: improve-error-messages
    content: 環境変数にPythonが見つからない場合のエラーメッセージを改善し、設定方法を明確に指示
    status: pending
    dependencies:
      - simplify-findSystemPython
---

# findSystemPythonの簡素化と仮想環境作成の改善

## 変更内容

### 1. `findSystemPython()`関数の簡素化

現在の`findSystemPython()`関数（60-158行）は以下の処理を行っています：

- 環境変数`PYTHON`をチェック
- 環境変数`PYTHON_HOME`をチェック
- PATH環境変数を検索（WindowsAppsを除く）
- `python.exe`のフォールバック試行

**変更**: PATH検索とフォールバック処理を削除し、環境変数（`PYTHON`、`PYTHON_HOME`）のチェックのみを行います。環境変数に有効なPythonが見つからない場合は、明確なエラーメッセージを返します。

### 2. `ensureVirtualEnvironment()`の改善

`ensureVirtualEnvironment()`関数（416-497行）では、仮想環境が存在しない場合に`findSystemPython()`を呼び出しています。**変更**: `findSystemPython()`がエラーを投げた場合のエラーメッセージを改善し、環境変数の設定方法を明確に指示します。

## 実装詳細

### 変更ファイル

- [`backcast/server/server-manager.js`](backcast/server/server-manager.js)

### 具体的な変更

1. **`findSystemPython()`関数（60-158行）**:

- PATH環境変数の検索処理（96-120行）を削除
- `python.exe`のフォールバック処理（122-157行）を削除
- 環境変数（`PYTHON`、`PYTHON_HOME`）のチェックのみを残す
- 環境変数にPythonが見つからない場合、明確なエラーメッセージを投げる

2. **エラーメッセージの改善**:

- 環境変数の設定方法を具体的に示すメッセージに変更

## 期待される動作

1. 環境変数`PYTHON`または`PYTHON_HOME`に有効なPythonが設定されている場合：

- そのPythonを使用して仮想環境を作成

2. 環境変数にPythonが設定されていない場合：

- エラーを投げて、ユーザーに環境変数の設定を促す