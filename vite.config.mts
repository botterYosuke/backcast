/* Copyright 2026 Marimo. All rights reserved. */

import { codecovVitePlugin } from "@codecov/vite-plugin";
import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";
import topLevelAwait from "vite-plugin-top-level-await";
import wasm from "vite-plugin-wasm";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { readFileSync } from "node:fs";

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
  return {
    name: "json-import-plugin",
    resolveId(id) {
      if (
        id === "@marimo-team/llm-info/models.json" ||
        id === "@marimo-team/llm-info/providers.json"
      ) {
        return `\0json-import:${id}`;
      }
      return null;
    },
    load(id) {
      if (id.startsWith("\0json-import:")) {
        const jsonPath = id.replace("\0json-import:", "");
        const isModels = jsonPath === "@marimo-team/llm-info/models.json";
        const filePath = isModels
          ? path.resolve(__dirname, "./packages/llm-info/data/generated/models.json")
          : path.resolve(__dirname, "./packages/llm-info/data/generated/providers.json");
        
        try {
          const jsonContent = readFileSync(filePath, "utf-8");
          const jsonData = JSON.parse(jsonContent);
          const exportKey = isModels ? "models" : "providers";
          
          // JSONファイルの構造を確認: { "models": [...] } または { "providers": [...] }
          if (!jsonData[exportKey]) {
            throw new Error(
              `Expected key "${exportKey}" not found in ${filePath}. Found keys: ${Object.keys(jsonData).join(", ")}`
            );
          }
          
          return `export const ${exportKey} = ${JSON.stringify(jsonData[exportKey], null, 2)};`;
        } catch (error) {
          if (error instanceof Error) {
            throw new Error(
              `Failed to load JSON file ${filePath}: ${error.message}`
            );
          }
          throw error;
        }
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
