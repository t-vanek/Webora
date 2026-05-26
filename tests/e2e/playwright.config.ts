import { defineConfig, devices } from '@playwright/test';

// The suite drives the real Blazor app in a browser, so it needs the web app
// running with its backing services (Postgres etc.) up — see README.md.
const baseURL = process.env.BASE_URL ?? 'http://localhost:5163';

export default defineConfig({
  testDir: './tests',
  // The app and a single Postgres are shared state, so keep runs serial.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    locale: 'cs-CZ',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    // Logs in once and stores the admin session for the authenticated specs.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'e2e',
      testIgnore: /auth\.setup\.ts/,
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['setup'],
    },
  ],
  // Starts the app if one isn't already listening. Building runs the first time,
  // so the timeout is generous; locally an already-running instance is reused.
  webServer: {
    command:
      'dotnet run --project src/Webora.Web/Webora.Web.csproj -c Debug --urls http://localhost:5163',
    cwd: '../..',
    url: `${baseURL}/login`,
    timeout: 180_000,
    reuseExistingServer: !process.env.CI,
  },
});
