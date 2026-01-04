import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright設定ファイル
 * Grid3DLayoutRendererのE2Eテスト用設定
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'html',
  timeout: 180000, // テストのタイムアウトを180秒に設定（接続待機処理を含む）
  use: {
    baseURL: 'http://localhost:3000',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],

  // webServer設定: 開発サーバーとバックエンドサーバーを自動起動
  webServer: [
    {
      command: 'npm run vite',
      url: 'http://localhost:3000',
      reuseExistingServer: !process.env.CI,
      timeout: 120 * 1000,
    },
    {
      command: 'npm run server',
      url: 'http://127.0.0.1:2718',
      reuseExistingServer: !process.env.CI,
      timeout: 180 * 1000,
      // バックエンドサーバーは起動に時間がかかる可能性があるため、待機時間を長めに設定
    },
  ],
});

