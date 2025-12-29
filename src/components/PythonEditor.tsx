import React, { useState, useEffect, useRef } from 'react';
import CodeMirror from '@uiw/react-codemirror';
import { python } from '@codemirror/lang-python';
import { oneDark } from '@codemirror/theme-one-dark';
import type { PyodideInterface } from 'pyodide';
import './PythonEditor.css';

// Pyodideの型定義
declare global {
  interface Window {
    loadPyodide: (config?: { indexURL?: string }) => Promise<PyodideInterface>;
  }
}

const PythonEditor: React.FC = () => {
  const [code, setCode] = useState(`# Pythonコードを入力してください
import sys

print("Hello, Backcast!")
print(f"Python version: {sys.version}")

# 簡単な計算
result = 2 + 3
print(f"2 + 3 = {result}")

# リストの操作
numbers = [1, 2, 3, 4, 5]
squared = [x**2 for x in numbers]
print(f"元のリスト: {numbers}")
print(f"2乗したリスト: {squared}")
`);
  const [output, setOutput] = useState<string>('');
  const [error, setError] = useState<string>('');
  const [isLoading, setIsLoading] = useState(false);
  const [isPyodideReady, setIsPyodideReady] = useState(false);
  const pyodideRef = useRef<PyodideInterface | null>(null);

  // Pyodideの初期化
  useEffect(() => {
    const initPyodide = async () => {
      try {
        setIsLoading(true);
        // PyodideをCDNから読み込む
        if (!window.loadPyodide) {
          const script = document.createElement('script');
          script.src = 'https://cdn.jsdelivr.net/pyodide/v0.27.7/full/pyodide.js';
          document.head.appendChild(script);
          
          await new Promise<void>((resolve) => {
            script.onload = () => resolve();
          });
        }

        const pyodide = await window.loadPyodide({
          indexURL: 'https://cdn.jsdelivr.net/pyodide/v0.27.7/full/',
        });

        // 標準出力をキャプチャするための設定
        pyodide.runPython(`
import sys
from io import StringIO

class OutputCapture:
    def __init__(self):
        self.buffer = StringIO()
    
    def write(self, s):
        if s:
            self.buffer.write(str(s))
    
    def flush(self):
        pass
    
    def getvalue(self):
        return self.buffer.getvalue()
    
    def reset(self):
        self.buffer = StringIO()

_stdout_capture = OutputCapture()
_stderr_capture = OutputCapture()
sys.stdout = _stdout_capture
sys.stderr = _stderr_capture
        `);

        pyodideRef.current = pyodide;
        setIsPyodideReady(true);
        setOutput('✅ Pyodideの初期化が完了しました。Pythonコードを実行できます。\n');
      } catch (err) {
        setError(`Pyodideの初期化に失敗しました: ${err}`);
      } finally {
        setIsLoading(false);
      }
    };

    initPyodide();
  }, []);

  const executeCode = async () => {
    if (!pyodideRef.current) {
      setError('Pyodideがまだ初期化されていません。');
      return;
    }

    setError('');
    setOutput('');
    setIsLoading(true);

    try {
      // 出力バッファをリセット
      pyodideRef.current.runPython(`
_stdout_capture.reset()
_stderr_capture.reset()
      `);

      // Pythonコードを実行
      let result: any;
      try {
        result = pyodideRef.current.runPython(code);
      } catch (execError: any) {
        // 実行エラーをキャッチ
        const errorOutput = pyodideRef.current.runPython(`
_stderr_capture.getvalue()
        `);
        throw new Error(errorOutput || execError.message || String(execError));
      }

      // 出力を取得
      const stdoutOutput = pyodideRef.current.runPython(`
_stdout_capture.getvalue()
      `);

      const stderrOutput = pyodideRef.current.runPython(`
_stderr_capture.getvalue()
      `);

      let finalOutput = '';
      
      if (stdoutOutput) {
        finalOutput += stdoutOutput;
      }
      
      if (stderrOutput) {
        finalOutput += stderrOutput;
      }

      if (result !== undefined && result !== null) {
        if (finalOutput) {
          finalOutput += `\n[戻り値]: ${result}`;
        } else {
          finalOutput = `[戻り値]: ${result}`;
        }
      }

      if (finalOutput) {
        setOutput(finalOutput);
      } else {
        setOutput('✅ コードが正常に実行されました（出力なし）\n');
      }
    } catch (err: any) {
      setError(err.message || String(err));
      setOutput('');
    } finally {
      setIsLoading(false);
    }
  };

  const clearOutput = () => {
    setOutput('');
    setError('');
  };

  return (
    <div className="python-editor-container">
      <div className="editor-header">
        <h2>🐍 Python コードエディタ</h2>
        <div className="editor-actions">
          <button
            onClick={executeCode}
            disabled={!isPyodideReady || isLoading}
            className="run-button"
          >
            {isLoading ? '実行中...' : '▶ 実行'}
          </button>
          <button onClick={clearOutput} className="clear-button">
            🗑 クリア
          </button>
        </div>
      </div>

      <div className="editor-wrapper">
        <CodeMirror
          value={code}
          height="400px"
          extensions={[python()]}
          theme={oneDark}
          onChange={(value) => setCode(value)}
          basicSetup={{
            lineNumbers: true,
            foldGutter: true,
            dropCursor: false,
            allowMultipleSelections: false,
          }}
        />
      </div>

      <div className="output-container">
        <div className="output-header">
          <h3>出力</h3>
          {isPyodideReady && (
            <span className="status-badge ready">準備完了</span>
          )}
          {isLoading && (
            <span className="status-badge loading">実行中...</span>
          )}
        </div>
        <div className="output-content">
          {error && (
            <div className="error-output">
              <strong>エラー:</strong>
              <pre>{error}</pre>
            </div>
          )}
          {output && (
            <div className="stdout-output">
              <pre>{output}</pre>
            </div>
          )}
          {!output && !error && !isLoading && (
            <div className="empty-output">
              コードを実行すると、ここに結果が表示されます。
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default PythonEditor;

