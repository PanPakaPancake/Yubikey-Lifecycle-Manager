import { test, expect, devLogin, OWNER } from './fixtures';

// Rauchtest gegen den echten Docker-Stack (Image wie ausgeliefert, PostgreSQL im Container, frische Installation).
// Prüft, was der Testserver nicht abdeckt: gebautes Image mit Web-Oberfläche, Sicherheits-Header, Setup-Assistent.

test('Server ist gesund und liefert Sicherheits-Header', async ({ request }) => {
  const health = await request.get('/healthz');
  expect(health.ok()).toBeTruthy();
  const index = await request.get('/');
  expect(index.ok()).toBeTruthy();
  const headers = index.headers();
  expect(headers['content-security-policy']).toBeTruthy();
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(await index.text()).toContain('<div id="root">');
});

test('Frische Installation zeigt den Setup-Assistenten', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/setup/);
  await expect(page.getByRole('heading').first()).toBeVisible();
});

test('Anmeldung und Hauptseiten im Container', async ({ page }) => {
  await devLogin(page, OWNER);
  for (const path of ['/', '/keys', '/tenants', '/users', '/platform', '/agents', '/audit', '/passkey']) {
    await page.goto(path);
    await expect(page.locator('h1').first()).toBeVisible();
    await expect(page.getByText('Seite nicht gefunden')).toHaveCount(0);
  }
});
