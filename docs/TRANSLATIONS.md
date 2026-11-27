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

Not yet translated (separate changes): PIN letters and PDF reports, mails, error messages from the
server, the agent tray app and the version history.

## Deutsch (Kurzfassung)

Texte im Code bleiben deutsch und werden mit `tr('…')` umschlossen. Die englische Fassung steht in
`web/src/locales/en.ts`. `npm run i18n:check` prüft Vollständigkeit und Platzhalter.
