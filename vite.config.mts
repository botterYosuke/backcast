/* Copyright 2026 Marimo. All rights reserved. */

import { codecovVitePlugin } from "@codecov/vite-plugin";
import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";
import topLevelAwait from "vite-plugin-top-level-await";
import wasm from "vite-plugin-wasm";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { existsSync, readFileSync } from "node:fs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const isDev = process.env.NODE_ENV === "development";
const isStorybook = process.env.npm_lifecycle_script?.includes("storybook");
const isPyodide = process.env.PYODIDE === "true";

console.log("Building environment:", process.env.NODE_ENV);

const ReactCompilerConfig = {
  target: "19",
};

// Plugin to handle SVG imports from @marimo-team/llm-info/icons
const svgInlinePlugin = (): Plugin => {
  return {
    name: "svg-inline-plugin",
    resolveId(id) {
      if (id.startsWith("@marimo-team/llm-info/icons/") && id.endsWith("?inline")) {
        const svgPath = id
          .replace("@marimo-team/llm-info/icons/", "")
          .replace("?inline", "");
        return `\0svg-inline:${svgPath}`;
      }
      return null;
    },
    load(id) {
      if (id.startsWith("\0svg-inline:")) {
        const svgPath = id.replace("\0svg-inline:", "");
        const fullPath = path.resolve(__dirname, "./packages/llm-info/icons", svgPath);
        const svgContent = readFileSync(fullPath, "utf-8");
        return `export default ${JSON.stringify(svgContent)};`;
      }
      return null;
    },
  };
};

// Plugin to handle JSON imports from @marimo-team/llm-info
const jsonImportPlugin = (): Plugin => {
  const modelsJsonPath = path.resolve(__dirname, "./packages/llm-info/data/generated/models.json");
  const providersJsonPath = path.resolve(__dirname, "./packages/llm-info/data/generated/providers.json");
  const modelsTsPath = path.resolve(__dirname, "./packages/llm-info/data/generated/models.ts");
  const providersTsPath = path.resolve(__dirname, "./packages/llm-info/data/generated/providers.ts");
  
  return {
    name: "json-import-plugin",
    enforce: "pre", // 標準のJSONローダーより先に実行
    buildStart() {
    },
    resolveId(id, importer) {
      // .tsファイルの場合は、Viteの標準的なTypeScript処理に任せる
      if (
        id === "@marimo-team/llm-info/models.ts" ||
        id === "@marimo-team/llm-info/providers.ts"
      ) {
        return null; // Viteの標準処理に任せる
      }
      if (
        id === "@marimo-team/llm-info/models.json" ||
        id === "@marimo-team/llm-info/providers.json"
      ) {
        // 仮想モジュールIDを返すことで、標準のJSONローダーが処理しないようにする
        const resolvedId = `\0json-import:${id}`;
        return resolvedId;
      }
      // ファイルパスでもチェック（標準のJSONローダーが処理しようとする場合に備える）
      // .tsファイルの場合は、Viteの標準処理に任せる
      const normalizedId = id.replace(/\\/g, '/');
      if (
        normalizedId === modelsTsPath.replace(/\\/g, '/') ||
        id === modelsTsPath ||
        normalizedId === providersTsPath.replace(/\\/g, '/') ||
        id === providersTsPath ||
        id.endsWith('/packages/llm-info/data/generated/models.ts') ||
        id.endsWith('/packages/llm-info/data/generated/providers.ts') ||
        id.endsWith('/data/generated/models.ts') ||
        id.endsWith('/data/generated/providers.ts') ||
        id.endsWith('\\packages\\llm-info\\data\\generated\\models.ts') ||
        id.endsWith('\\packages\\llm-info\\data\\generated\\providers.ts') ||
        id.endsWith('\\data\\generated\\models.ts') ||
        id.endsWith('\\data\\generated\\providers.ts')
      ) {
        return null; // Viteの標準処理に任せる
      }
      
      // WindowsとUnixのパス形式の両方に対応
      const normalizedModelsJsonPath = modelsJsonPath.replace(/\\/g, '/');
      const normalizedProvidersJsonPath = providersJsonPath.replace(/\\/g, '/');
      
      // 相対パスでもチェック（package.jsonのexports解決後のパス）
      const relativeModelsJsonPath = './packages/llm-info/data/generated/models.json';
      const relativeProvidersJsonPath = './packages/llm-info/data/generated/providers.json';
      
      // package.jsonのexports解決後のパス（相対パス、./data/generated/models.jsonなど）
      const exportsRelativeModelsJsonPath = './data/generated/models.json';
      const exportsRelativeProvidersJsonPath = './data/generated/providers.json';
      
      // package.jsonのexports解決後のパス（相対パスから絶対パスへの解決後）
      const resolvedRelativeModelsJsonPath = path.resolve(__dirname, relativeModelsJsonPath);
      const resolvedRelativeProvidersJsonPath = path.resolve(__dirname, relativeProvidersJsonPath);
      const normalizedResolvedRelativeModelsJsonPath = resolvedRelativeModelsJsonPath.replace(/\\/g, '/');
      const normalizedResolvedRelativeProvidersJsonPath = resolvedRelativeProvidersJsonPath.replace(/\\/g, '/');
      
      // package.jsonのexports解決後のパス（node_modules内からの解決後）
      const nodeModulesModelsJsonPath = path.resolve(__dirname, './node_modules/@marimo-team/llm-info/data/generated/models.json');
      const nodeModulesProvidersJsonPath = path.resolve(__dirname, './node_modules/@marimo-team/llm-info/data/generated/providers.json');
      const normalizedNodeModulesModelsJsonPath = nodeModulesModelsJsonPath.replace(/\\/g, '/');
      const normalizedNodeModulesProvidersJsonPath = nodeModulesProvidersJsonPath.replace(/\\/g, '/');
      
      // package.jsonのexports解決後のパス（./data/generated/models.jsonなど）を解決
      const resolvedExportsRelativeModelsJsonPath = path.resolve(__dirname, './packages/llm-info/data/generated/models.json');
      const resolvedExportsRelativeProvidersJsonPath = path.resolve(__dirname, './packages/llm-info/data/generated/providers.json');
      const normalizedResolvedExportsRelativeModelsJsonPath = resolvedExportsRelativeModelsJsonPath.replace(/\\/g, '/');
      const normalizedResolvedExportsRelativeProvidersJsonPath = resolvedExportsRelativeProvidersJsonPath.replace(/\\/g, '/');
      
      if (
        normalizedId === normalizedModelsJsonPath || normalizedId === normalizedProvidersJsonPath ||
        id === modelsJsonPath || id === providersJsonPath ||
        id === relativeModelsJsonPath || id === relativeProvidersJsonPath ||
        id === exportsRelativeModelsJsonPath || id === exportsRelativeProvidersJsonPath ||
        normalizedId === normalizedResolvedRelativeModelsJsonPath || normalizedId === normalizedResolvedRelativeProvidersJsonPath ||
        id === resolvedRelativeModelsJsonPath || id === resolvedRelativeProvidersJsonPath ||
        normalizedId === normalizedResolvedExportsRelativeModelsJsonPath || normalizedId === normalizedResolvedExportsRelativeProvidersJsonPath ||
        id === resolvedExportsRelativeModelsJsonPath || id === resolvedExportsRelativeProvidersJsonPath ||
        normalizedId === normalizedNodeModulesModelsJsonPath || normalizedId === normalizedNodeModulesProvidersJsonPath ||
        id === nodeModulesModelsJsonPath || id === nodeModulesProvidersJsonPath ||
        id.endsWith('/packages/llm-info/data/generated/models.json') ||
        id.endsWith('/packages/llm-info/data/generated/providers.json') ||
        id.endsWith('/data/generated/models.json') ||
        id.endsWith('/data/generated/providers.json') ||
        id.endsWith('\\packages\\llm-info\\data\\generated\\models.json') ||
        id.endsWith('\\packages\\llm-info\\data\\generated\\providers.json') ||
        id.endsWith('\\data\\generated\\models.json') ||
        id.endsWith('\\data\\generated\\providers.json') ||
        (id.includes('packages/llm-info/data/generated/models.json') && !id.includes('.ts')) ||
        (id.includes('packages/llm-info/data/generated/providers.json') && !id.includes('.ts')) ||
        (id.includes('packages\\llm-info\\data\\generated\\models.json') && !id.includes('.ts')) ||
        (id.includes('packages\\llm-info\\data\\generated\\providers.json') && !id.includes('.ts')) ||
        (id.includes('@marimo-team/llm-info/data/generated/models.json') && !id.includes('.ts')) ||
        (id.includes('@marimo-team/llm-info/data/generated/providers.json') && !id.includes('.ts')) ||
        (id.includes('data/generated/models.json') && !id.includes('.ts')) ||
        (id.includes('data/generated/providers.json') && !id.includes('.ts'))
      ) {
        // 仮想モジュールIDを返すことで、標準のJSONローダーが処理しないようにする
        const resolvedId = `\0json-import:${id}`;
        return resolvedId;
      }
      return null;
    },
    load(id) {
      // 仮想モジュールIDまたは実際のファイルパスの両方を処理
      let filePath: string | null = null;
      let isModels: boolean | null = null;
      
      if (id.startsWith("\0json-import:")) {
        const jsonPath = id.replace("\0json-import:", "");
        // .tsファイルは処理しない（Viteの標準処理に任せる）
        if (jsonPath === "@marimo-team/llm-info/models.ts" || jsonPath === "@marimo-team/llm-info/providers.ts" || jsonPath === modelsTsPath || jsonPath === providersTsPath) {
          return null; // Viteの標準処理に任せる
        }
        // .jsonファイルのみ処理
        if (jsonPath === "@marimo-team/llm-info/models.json" || jsonPath === modelsJsonPath) {
          filePath = modelsJsonPath;
          isModels = true;
        } else if (jsonPath === "@marimo-team/llm-info/providers.json" || jsonPath === providersJsonPath) {
          filePath = providersJsonPath;
          isModels = false;
        } else {
          // ファイルパスが仮想モジュールIDに含まれている場合
          const normalizedJsonPath = jsonPath.replace(/\\/g, '/');
          const normalizedModelsJsonPath = modelsJsonPath.replace(/\\/g, '/');
          const normalizedProvidersJsonPath = providersJsonPath.replace(/\\/g, '/');
          const normalizedModelsTsPath = modelsTsPath.replace(/\\/g, '/');
          const normalizedProvidersTsPath = providersTsPath.replace(/\\/g, '/');
          if (normalizedJsonPath === normalizedModelsJsonPath || jsonPath === modelsJsonPath || normalizedJsonPath === normalizedModelsTsPath || jsonPath === modelsTsPath) {
            filePath = existsSync(modelsTsPath) ? modelsTsPath : modelsJsonPath;
            isModels = true;
          } else if (normalizedJsonPath === normalizedProvidersJsonPath || jsonPath === providersJsonPath || normalizedJsonPath === normalizedProvidersTsPath || jsonPath === providersTsPath) {
            filePath = existsSync(providersTsPath) ? providersTsPath : providersJsonPath;
            isModels = false;
          }
        }
      } else {
        // 実際のファイルパスで直接呼ばれた場合（標準のJSONローダーが処理しようとしている可能性）
        // .tsファイルの場合は、Viteの標準処理に任せる
        if (id === modelsTsPath || id === providersTsPath || 
            id.endsWith('/data/generated/models.ts') || id.endsWith('\\data\\generated\\models.ts') ||
            id.endsWith('/data/generated/providers.ts') || id.endsWith('\\data\\generated\\providers.ts') ||
            id === "@marimo-team/llm-info/models.ts" || id === "@marimo-team/llm-info/providers.ts") {
          return null; // Viteの標準処理に任せる
        }
        
        const normalizedId = id.replace(/\\/g, '/');
        const normalizedModelsJsonPath = modelsJsonPath.replace(/\\/g, '/');
        const normalizedProvidersJsonPath = providersJsonPath.replace(/\\/g, '/');
        
        // package.jsonのexports解決後のパス（./data/generated/models.jsonなど）もチェック
        const exportsRelativeModelsJsonPath = './data/generated/models.json';
        const exportsRelativeProvidersJsonPath = './data/generated/providers.json';
        const resolvedExportsRelativeModelsJsonPath = path.resolve(__dirname, './packages/llm-info/data/generated/models.json');
        const resolvedExportsRelativeProvidersJsonPath = path.resolve(__dirname, './packages/llm-info/data/generated/providers.json');
        const normalizedResolvedExportsRelativeModelsJsonPath = resolvedExportsRelativeModelsJsonPath.replace(/\\/g, '/');
        const normalizedResolvedExportsRelativeProvidersJsonPath = resolvedExportsRelativeProvidersJsonPath.replace(/\\/g, '/');
        
        if (normalizedId === normalizedModelsJsonPath || id === modelsJsonPath ||
            normalizedId === normalizedResolvedExportsRelativeModelsJsonPath || id === resolvedExportsRelativeModelsJsonPath ||
            id === exportsRelativeModelsJsonPath ||
            id.endsWith('/data/generated/models.json') ||
            id.endsWith('\\data\\generated\\models.json') ||
            id.includes('data/generated/models.json')) {
          filePath = modelsJsonPath;
          isModels = true;
        } else if (normalizedId === normalizedProvidersJsonPath || id === providersJsonPath ||
                   normalizedId === normalizedResolvedExportsRelativeProvidersJsonPath || id === resolvedExportsRelativeProvidersJsonPath ||
                   id === exportsRelativeProvidersJsonPath ||
                   id.endsWith('/data/generated/providers.json') ||
                   id.endsWith('\\data\\generated\\providers.json') ||
                   id.includes('data/generated/providers.json')) {
          filePath = providersJsonPath;
          isModels = false;
        } else if (id === "@marimo-team/llm-info/models.json" || id === "@marimo-team/llm-info/providers.json") {
          // 元のIDで直接呼ばれた場合（.jsonのみ）
          filePath = id === "@marimo-team/llm-info/models.json" ? modelsJsonPath : providersJsonPath;
          isModels = id === "@marimo-team/llm-info/models.json";
        }
      }
      
      if (filePath === null || isModels === null) {
        return null;
      }
        
        try {
          // ファイルの存在確認
          const fileExists = existsSync(filePath);
          if (!fileExists) {
            throw new Error(`File does not exist: ${filePath}`);
          }
          
          const fileContent = readFileSync(filePath, "utf-8");
          const isEmpty = !fileContent.trim();
          
          // 空ファイルのチェック
          if (isEmpty) {
            throw new Error(`File is empty: ${filePath}`);
          }
          
          // JSONファイルの場合は、従来通り処理
          const jsonData = JSON.parse(fileContent);
          const exportKey = isModels ? "models" : "providers";
          
          // JSONファイルの構造を確認: { "models": [...] } または { "providers": [...] }
          if (!jsonData[exportKey]) {
            throw new Error(
              `Expected key "${exportKey}" not found in ${filePath}. Found keys: ${Object.keys(jsonData).join(", ")}`
            );
          }
          
          const result = `export const ${exportKey} = ${JSON.stringify(jsonData[exportKey], null, 2)};`;
          return result;
        } catch (error) {
          if (error instanceof Error) {
            throw new Error(
              `Failed to load JSON file ${filePath}: ${error.message}`
            );
          }
          throw error;
        }
    },
    transform(code, id) {
      // 標準のJSONローダーが処理しようとするのを防ぐ
      // .tsファイルの場合は、Viteの標準処理に任せる
      if (
        id === modelsTsPath ||
        id === providersTsPath ||
        id.endsWith('/data/generated/models.ts') ||
        id.endsWith('\\data\\generated\\models.ts') ||
        id.endsWith('/data/generated/providers.ts') ||
        id.endsWith('\\data\\generated\\providers.ts')
      ) {
        return null; // Viteの標準処理に任せる
      }
      if (
        id === "@marimo-team/llm-info/models.json" ||
        id === "@marimo-team/llm-info/providers.json" ||
        id === modelsJsonPath ||
        id === providersJsonPath ||
        id.startsWith("\0json-import:")
      ) {
        // 既にloadで処理されているので、nullを返して標準のJSONローダーが処理しないようにする
        return null;
      }
      return null;
    },
  };
};

// https://vitejs.dev/config/
export default defineConfig({
  // This allows for a dynamic <base> tag in index.html
  base: "./",
  server: {
    host: "localhost",
    port: 3000,
    headers: isPyodide
      ? {
          "Cross-Origin-Opener-Policy": "same-origin",
          "Cross-Origin-Embedder-Policy": "require-corp",
        }
      : {},
  },
  define: {
    "import.meta.env.VITE_MARIMO_VERSION": process.env.VITE_MARIMO_VERSION
      ? JSON.stringify(process.env.VITE_MARIMO_VERSION)
      : JSON.stringify("latest"),
    "process.env.NODE_ENV": JSON.stringify(process.env.NODE_ENV),
  },
  build: {
    minify: isDev ? false : "oxc", // default is "oxc"
    sourcemap: isDev,
  },
  assetsInclude: ["**/*.svg"],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
      "@marimo-team/llm-info/icons": path.resolve(__dirname, "./packages/llm-info/icons"),
    },
    tsconfigPaths: true,
    dedupe: [
      "react",
      "react-dom",
      "@emotion/react",
      "@emotion/cache",
      "@codemirror/view",
      "@codemirror/state",
    ],
    conditions: ["import", "module", "browser", "default"],
  },
  experimental: {
    enableNativePlugin: true,
  },
  worker: {
    format: "es",
  },
  plugins: [
    svgInlinePlugin(),
    jsonImportPlugin(),
    react({
      babel: {
        presets: ["@babel/preset-typescript"],
        plugins: [
          ["@babel/plugin-proposal-decorators", { legacy: true }],
          ["babel-plugin-react-compiler", ReactCompilerConfig],
        ],
      },
    }),
    codecovVitePlugin({
      enableBundleAnalysis: process.env.CODECOV_TOKEN !== undefined,
      bundleName: "backcast",
      uploadToken: process.env.CODECOV_TOKEN,
    }),
    wasm(),
    topLevelAwait(),
  ],
});
