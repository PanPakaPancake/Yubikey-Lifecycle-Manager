# Translations / Übersetzungen

The web interface is available in German and English. German is the source language: the German
text in the code is also the translation key.

```tsx
import { tr } from '../i18n';

<button>{tr('Key registrieren')}</button>
toast.ok(tr('{n} Keys gesamt', { n: total }));
```

- English texts live in `web/src/locales/en.ts` (German text → English text).
- Placeholders such as `{n}` must appear in both languages.
- `npm run i18n:check` (in `web/`) fails if a `tr()` text has no English entry, if placeholders
  differ, or if `en.ts` contains entries no longer used. CI runs it on every pull request.
- If a translation is missing at runtime, the German text is shown.

Language selection: the browser language is used by default (German for `de-*`, otherwise English).
The globe button in the sidebar, on the sign-in page and on the passkey info page switches the language;
when signed in, the choice is stored in the user account (`PUT /api/me/language`) and applies on every PC.

## Server

Server texts (error messages, exports, PDF reports, PIN letters, mails, webhook labels) work the same way:
the German text is the key, English lives in `src/Ylm.Contracts/Localization/en.json`.

```csharp
using Ylm.Contracts.Localization;

throw ApiException.BadRequest("too_long", L.T("Die Datei ist zu groß."));
var text = L.F($"Key {serial} ist bereits erfasst.");   // key: "Key {0} ist bereits erfasst."
```

- `L.T("…")` translates a fixed text, `L.F($"…")` an interpolated one (the key has `{0}`, `{1}` … in place
  of the values, format specifiers such as `{0:dd.MM.yyyy}` stay part of the key and may differ in English).
- Fixed tables (roles, letter blocks, webhook events, security advisories) are built once per language with
  `L.PerLanguage<…>`, so `L.T` works inside them.
- `node tools/i18n-check-server.mjs` checks for missing, unused and mismatched entries (CI runs it).

Which language is used:

- API requests: the `X-YLM-Language` header the web interface sends, otherwise the `ylm-lang` cookie
  (downloads via link), otherwise `Accept-Language`, otherwise German.
- PIN letters: the language set under branding (tenant, otherwise partner, otherwise German), independent of
  the admin's interface language. The info page link in the letter carries `?lang=en`.
- Mail reports, mail alerts, webhooks and background jobs (sync): the partner branding language.
- Texts already stored (audit log details, last sync message) stay in the language they were written in.

Not yet translated: the agent tray app (progress texts from the agent appear in German) and the version
history.

## Deutsch (Kurzfassung)

Texte im Code bleiben deutsch und werden mit `tr('…')` umschlossen. Die englische Fassung steht in
`web/src/locales/en.ts`. `npm run i18n:check` prüft Vollständigkeit und Platzhalter.
Im Server entsprechend `L.T("…")` bzw. `L.F($"…")`, Englisch in `src/Ylm.Contracts/Localization/en.json`,
Prüfung mit `node tools/i18n-check-server.mjs`. Die Sprache von PIN-Briefen, Mails und Webhooks steht im
Erscheinungsbild.
