import { test as base, expect, type Page, type APIRequestContext } from '@playwright/test';

export const OWNER = 'owner@muster-it.example';
export const OPERATOR = 'operator@muster-it.example';

/**
 * Jeder Test schlägt fehl, wenn im Browser ein JavaScript-Fehler auftritt (z. B. Absturz einer Seite)
 * oder die Content-Security-Policy etwas blockiert. Fehlgeschlagene Netzwerkanfragen (401 vor der Anmeldung,
 * 404 für ein fehlendes Profilbild) sind normal und zählen nicht.
 */
export const test = base.extend<{ browserErrors: string[] }>({
  browserErrors: [async ({ page }, use, testInfo) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(`Unbehandelter Fehler: ${e.message}`));
    page.on('console', (m) => {
      if (m.type() !== 'error') return;
      const text = m.text();
      if (text.startsWith('Failed to load resource')) return;
      errors.push(`Konsole: ${text}`);
    });
    await use(errors);
    if (testInfo.status === testInfo.expectedStatus) expect(errors, 'Fehler im Browser').toEqual([]);
  }, { auto: true }],
});

export { expect };

/** Anmeldung über die Entwicklungs-Anmeldung der Login-Seite (nur im Testserver bzw. mit AuthMode=Development). */
export async function devLogin(page: Page, upn: string) {
  await page.goto('/login');
  await page.getByText('Entwicklungs-Anmeldung').click();
  await page.getByLabel('UPN').fill(upn);
  await page.getByRole('button', { name: 'Anmelden (Entwicklung)' }).click();
  await expect(page.getByRole('link', { name: 'Übersicht' }).first()).toBeVisible();
}

/** Seite fragt nach dem Kunden-Tenant: in der Auswahl auf der Seite anklicken. */
export async function chooseTenant(page: Page, name = 'Contoso GmbH') {
  await page.getByRole('button', { name, exact: true }).click();
  await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
}

/** Kunden-Tenant des Testservers vorab wählen (wie der Umschalter oben, gespeichert im Browser). */
export async function useTenant(page: Page, request: APIRequestContext) {
  const { tenantId } = await e2eInfo(request);
  await page.evaluate((id) => localStorage.setItem('ylm-tenant', id), tenantId);
}

export type E2EInfo = {
  tenantId: string;
  agentConnected: boolean;
  key: { serialNumber: number; firmware: string; inserted: boolean; pin: string | null };
  registered: string[];
};

export async function e2eInfo(request: APIRequestContext): Promise<E2EInfo> {
  const res = await request.get('/e2e/info');
  expect(res.ok()).toBeTruthy();
  return res.json();
}

/** Simulierten Key wechseln: frischer Key im Werkszustand mit neuer Seriennummer. */
export async function insertFreshKey(request: APIRequestContext, serialNumber: number, firmware = '5.7.1') {
  const res = await request.post('/e2e/agent/key', { data: { action: 'replace', serialNumber, firmware } });
  expect(res.ok()).toBeTruthy();
}

/**
 * Kopplungscode auf der Seite „Mein Agent“ erzeugen und den simulierten Agent des Testservers damit koppeln
 * (wie die Eingabe im Tray). Danach meldet sich der Agent per SignalR beim Server.
 */
export async function pairSimulatedAgent(page: Page, request: APIRequestContext) {
  await page.goto('/agents');
  await page.getByRole('button', { name: /Kopplungscode erzeugen|Neuen Code erzeugen/ }).click();
  const code = (await page.locator('.pairing-code').innerText()).trim();
  expect(code).not.toBe('');
  const res = await request.post('/e2e/agent/pair', { data: { code } });
  const body = await res.json();
  expect(body.ok, body.message).toBeTruthy();
}
