import { test, expect, devLogin, OWNER } from './fixtures';

test('Entwicklungs-Anmeldung führt zur Übersicht', async ({ page }) => {
  await devLogin(page, OWNER);
  await expect(page.getByText(/Guten (Morgen|Tag|Abend), owner/i)).toBeVisible();
});

test('Ohne Anmeldung landet man auf der Login-Seite', async ({ page }) => {
  await page.goto('/keys');
  await expect(page.getByRole('button', { name: 'Mit Passkey anmelden' }).or(page.getByRole('link', { name: 'Mit Microsoft anmelden' })).first()).toBeVisible();
});

// Lokales Konto mit Passkey: Owner legt das Konto an, die Person richtet über den Einmal-Link einen Passkey ein
// (virtueller FIDO2-Authenticator von Chromium statt YubiKey) und meldet sich damit an.
test('Lokales Konto: Passkey einrichten und damit anmelden', async ({ page, browser }) => {
  const upn = `lokal-${Date.now()}@muster-it.example`;
  await devLogin(page, OWNER);
  await page.goto('/users');
  await page.getByRole('button', { name: 'Benutzer hinzufügen' }).click();
  await page.getByText('Lokales Konto (Passkey)').click();
  await page.getByLabel('Anmeldename').fill(upn);
  await page.getByLabel('Anzeigename').fill('Lokale Testperson');
  await page.getByRole('button', { name: 'Speichern' }).click();
  const link = await page.locator('.setup-link input').inputValue();
  expect(link).toContain('/');

  // Eigener Browser-Kontext = andere Person ohne Sitzung.
  const context = await browser.newContext({ baseURL: new URL(page.url()).origin });
  const person = await context.newPage();
  const cdp = await context.newCDPSession(person);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'usb', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  });
  const errors: string[] = [];
  person.on('pageerror', (e) => errors.push(e.message));

  await person.goto(new URL(link, page.url()).toString());
  await person.getByRole('button', { name: 'Passkey einrichten' }).click();
  await expect(person.getByRole('link', { name: 'Übersicht' }).first()).toBeVisible();

  // Abmelden und mit dem Passkey wieder anmelden.
  await person.request.post('/auth/logout', { headers: { 'X-YLM-CSRF': '1' } });
  await person.goto('/login');
  await person.getByRole('button', { name: 'Mit Passkey anmelden' }).click();
  await expect(person.getByRole('link', { name: 'Übersicht' }).first()).toBeVisible();
  const me = await (await person.request.get('/api/me')).json();
  expect(me.upn ?? me.user?.upn).toBe(upn);
  expect(errors).toEqual([]);
  await context.close();
});
