import { defineConfig, devices } from '@playwright/test';

const ci = !!process.env['CI'];
// Another dev server may already hold 4200 (and serve other code); E2E_WEB_PORT picks a free port
const webPort = process.env['E2E_WEB_PORT'] ?? '4200';

/**
 * The tests run against the real stack: the API with the fake providers (PLAN.md 6.8) on a database the run owns, and
 * the production build of the Angular app, which proxies /api to the API. The database comes from the environment
 * (`ConnectionStrings__SupportDesk`): in CI a service container, locally a disposable one, since the tests create tickets.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: ci,
  retries: 0,
  reporter: ci ? [['list'], ['html', { open: 'never' }]] : 'list',
  expect: { timeout: 10_000 },
  use: {
    baseURL: `http://localhost:${webPort}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      // The dev container runs Chromium without its sandbox
      use: { ...devices['Desktop Chrome'], launchOptions: { args: ['--no-sandbox'] } },
    },
  ],
  webServer: [
    {
      command: 'dotnet run --project ../src/SupportDesk.Api',
      url: 'http://localhost:5080/api/health',
      // A server that is already running is reused locally: start it with the same environment as below
      reuseExistingServer: !ci,
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        AI__Chat__Provider: 'Fake',
        AI__Embeddings__Provider: 'Fake',
        // The fake embeddings are hashes, so chunks are barely similar to anything; accept all non-negative ones so replies have citations
        Retrieval__MinSimilarity: '0',
      },
    },
    {
      command: `npm start -- --configuration production --port ${webPort}`,
      url: `http://localhost:${webPort}`,
      reuseExistingServer: !ci,
      timeout: 180_000,
    },
  ],
});
