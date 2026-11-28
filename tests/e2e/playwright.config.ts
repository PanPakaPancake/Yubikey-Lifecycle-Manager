import { defineConfig, devices } from '@playwright/test';

// Ende-zu-Ende-Tests der Web-Oberfläche. Zwei Ziele:
//  - "host": Testserver tests/Ylm.E2E.Host (echter Server, simuliertes Entra und simulierter YubiKey-Agent).
//            Startet automatisch, außer E2E_BASE_URL zeigt auf einen schon laufenden Server.
//  - "docker": Rauchtest gegen den Docker-Stack (E2E_DOCKER_URL, Standard http://localhost:8080).
// Anleitung: tests/e2e/README.md
const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:5180';
const dockerURL = process.env.E2E_DOCKER_URL ?? 'http://localhost:8080';
const onlyDocker = process.argv.some((a) => a === '--project=docker');

export default defineConfig({
  testDir: './specs',
  // Ein simulierter Agent und eine Datenbank für alle Tests: nacheinander ausführen.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }], ['github']] : [['list'], ['html', { open: 'never' }]],
  use: {
    locale: 'de-DE',
    timezoneId: 'Europe/Berlin',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    launchOptions: process.env.E2E_CHROMIUM ? { executablePath: process.env.E2E_CHROMIUM } : undefined,
  },
  projects: [
    {
      name: 'host',
      testIgnore: /docker\..*spec\.ts/,
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, baseURL },
    },
    {
      name: 'docker',
      testMatch: /docker\..*spec\.ts/,
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, baseURL: dockerURL },
    },
  ],
  webServer: process.env.E2E_BASE_URL || onlyDocker ? undefined : {
    command: 'dotnet run --project ../Ylm.E2E.Host -c Release',
    url: `${baseURL}/e2e/info`,
    timeout: 240_000,
    reuseExistingServer: !process.env.CI,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
