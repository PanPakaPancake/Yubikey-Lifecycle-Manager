import { test, expect, devLogin, chooseTenant, useTenant, OWNER } from './fixtures';

// Rauchtest: jede Hauptseite lädt ohne Absturz und ohne CSP-Fehler.
const pages: { path: string; heading: RegExp }[] = [
  { path: '/', heading: /Übersicht/ },
  { path: '/keys', heading: /Inventar/ },
  { path: '/sicherheitshinweise', heading: /Sicherheitshinweise/ },
  { path: '/rollout', heading: /Rollout/ },
  { path: '/register', heading: /Key registrieren/ },
  { path: '/todos', heading: /ToDo/ },
  { path: '/audit', heading: /Audit/ },
  { path: '/tenants', heading: /Tenants/ },
  { path: '/partner/stock', heading: /Partnerlager/ },
  { path: '/profiles', heading: /profile/i },
  { path: '/users', heading: /Benutzer/ },
  { path: '/partner/notifications', heading: /Benachrichtigungen/ },
  { path: '/platform', heading: /Einstellungen/ },
  { path: '/agents', heading: /Agent/ },
  { path: '/versionen', heading: /Version/ },
  { path: '/passkey', heading: /Passkey/ },
];

for (const p of pages) {
  test(`Seite ${p.path}`, async ({ page, request }) => {
    await devLogin(page, OWNER);
    await useTenant(page, request);
    await page.goto(p.path);
    await expect(page.getByRole('heading', { name: p.heading }).first()).toBeVisible();
    await expect(page.getByText('Seite nicht gefunden')).toHaveCount(0);
  });
}

test('Rollout-Übersicht listet die Personen des Tenants', async ({ page }) => {
  await devLogin(page, OWNER);
  await page.goto('/rollout');
  await chooseTenant(page);
  await expect(page.getByText('Ben Muster').first()).toBeVisible();
  await expect(page.getByText('Dana Admin').first()).toBeVisible();
});

test('PIN-Brief-Editor öffnet und zeigt die Vorschau', async ({ page }) => {
  await devLogin(page, OWNER);
  await page.goto('/platform');
  const editor = page.getByRole('link', { name: /Brief/ }).or(page.getByRole('button', { name: /Brief bearbeiten|PIN-Brief/ })).first();
  await editor.click();
  await expect(page.getByText(/PIN-Brief/).first()).toBeVisible();
});
