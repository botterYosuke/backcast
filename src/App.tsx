import React from 'react';
import PythonEditor from './components/PythonEditor';
import './App.css';

function App() {
  return (
    <div className="app">
      <header className="app-header">
        <h1>🌊 Backcast - Python実行環境</h1>
        <p>ブラウザ上でPythonコードを実行・表示できるアプリ</p>
      </header>

      <main className="app-main">
        <PythonEditor />
      </main>

      <footer className="app-footer">
        <p>Built with ❤️ using Marimo Frontend + Pyodide</p>
      </footer>
    </div>
  );
}

export default App;

