import { test, expect, devLogin, chooseTenant, pairSimulatedAgent, insertFreshKey, e2eInfo, OPERATOR } from './fixtures';
import { readFile } from 'node:fs/promises';

// Kernablauf: Agent koppeln, Key für eine Person registrieren, PIN-Brief herunterladen, Key im Inventar.
test.describe.configure({ mode: 'serial' });

test('Agent koppeln', async ({ page, request }) => {
  await devLogin(page, OPERATOR);
  await pairSimulatedAgent(page, request);
  await page.reload();
  await expect(page.getByText('online', { exact: true }).first()).toBeVisible();
  expect((await e2eInfo(request)).agentConnected).toBeTruthy();
});

test('Key für Anna registrieren und PIN-Brief herunterladen', async ({ page, request }) => {
  const serial = 40_000_000 + Math.floor(Math.random() * 1_000_000);
  await insertFreshKey(request, serial);
  await devLogin(page, OPERATOR);
  if (!(await e2eInfo(request)).agentConnected) await pairSimulatedAgent(page, request);

  await page.goto('/register');
  await chooseTenant(page);
  await page.getByPlaceholder('Name, UPN oder Mail eintippen').fill('anna');
  await page.getByRole('button', { name: /Anna Beispiel/ }).click();

  // Schritt Key: der simulierte Agent meldet den eingesteckten Key.
  await expect(page.getByText(String(serial)).first()).toBeVisible();
  await page.getByRole('button', { name: 'Weiter' }).click();

  // Schritt Profil & Prüfung.
  await page.getByRole('button', { name: 'Prüfen' }).click();
  await page.getByRole('button', { name: 'Registrierung starten' }).click();

  // Fertig: PIN-Brief als PDF.
  await expect(page.getByText('Der Key ist für Anna Beispiel registriert.')).toBeVisible({ timeout: 45_000 });
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'PIN-Brief (PDF)' }).click();
  const pdf = await readFile(await (await download).path());
  expect(pdf.subarray(0, 4).toString('ascii')).toBe('%PDF');

  const info = await e2eInfo(request);
  expect(info.registered).toContain('u-anna');
  expect(info.key.pin, 'Der Key hat eine PIN bekommen').not.toBeNull();

  // Key-Detailseite.
  await page.getByRole('link', { name: 'Key ansehen' }).click();
  await expect(page.getByText('anna@contoso.com').first()).toBeVisible();
  await expect(page.getByText(String(serial)).first()).toBeVisible();
});

test('Inventar zeigt registrierte und selbst registrierte Keys', async ({ page }) => {
  await devLogin(page, OPERATOR);
  await page.goto('/keys');
  await expect(page.getByText('Anna Beispiel').first()).toBeVisible();
  // Carla hat ihren Key selbst in Entra registriert, er kam über den Sync ins Inventar.
  await expect(page.getByText('Carla Selbst').first()).toBeVisible();
});
