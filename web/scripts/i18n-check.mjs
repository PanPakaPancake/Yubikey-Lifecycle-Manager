// Prüft die Übersetzungen der Oberfläche:
//  - jeder Text in tr("…") hat einen Eintrag in src/locales/en.ts,
//  - Platzhalter wie {n} stimmen in beiden Sprachen überein,
//  - en.ts enthält keine verwaisten Einträge.
// Aufruf: npm run i18n:check   (mit --missing gibt es die fehlenden Texte als JSON aus)
import ts from 'typescript';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'src');
const files = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'locales') walk(p); }
    else if (/\.(ts|tsx)$/.test(e.name) && !e.name.endsWith('.d.ts')) files.push(p);
  }
})(root);

const used = new Map(); // text -> first location
const dynamic = [];
for (const file of files) {
  const src = fs.readFileSync(file, 'utf8');
  const sf = ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, file.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  (function visit(n) {
    if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && n.expression.text === 'tr' && n.arguments.length) {
      const a = n.arguments[0];
      const where = path.relative(root, file) + ':' + (sf.getLineAndCharacterOfPosition(n.getStart()).line + 1);
      if (ts.isStringLiteral(a) || ts.isNoSubstitutionTemplateLiteral(a)) { if (!used.has(a.text)) used.set(a.text, where); }
      else if (!file.endsWith('i18n.ts')) dynamic.push(where);
    }
    ts.forEachChild(n, visit);
  })(sf);
}

// en.ts als Objektliteral auslesen (ohne es auszuführen).
const enFile = path.join(root, 'locales', 'en.ts');
const enSf = ts.createSourceFile(enFile, fs.readFileSync(enFile, 'utf8'), ts.ScriptTarget.Latest, true);
const en = new Map();
const dupes = [];
(function visit(n) {
  if (ts.isPropertyAssignment(n) && (ts.isStringLiteral(n.name) || ts.isIdentifier(n.name)) && (ts.isStringLiteral(n.initializer) || ts.isNoSubstitutionTemplateLiteral(n.initializer))) {
    const k = n.name.text;
    if (en.has(k)) dupes.push(k);
    en.set(k, n.initializer.text);
  }
  ts.forEachChild(n, visit);
})(enSf);

const placeholders = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');
const missing = [...used.keys()].filter((k) => !en.has(k));
const unused = [...en.keys()].filter((k) => !used.has(k));
const badVars = [...used.keys()].filter((k) => en.has(k) && placeholders(k) !== placeholders(en.get(k)));

if (process.argv.includes('--missing')) {
  console.log(JSON.stringify(Object.fromEntries(missing.map((k) => [k, used.get(k)])), null, 1));
  process.exit(0);
}

let failed = false;
const report = (title, list, fmt = (x) => x) => {
  if (!list.length) return;
  failed = true;
  console.error(`\n${title} (${list.length}):`);
  for (const x of list.slice(0, 50)) console.error('  ' + fmt(x));
  if (list.length > 50) console.error(`  … und ${list.length - 50} weitere`);
};
report('Ohne englische Übersetzung', missing, (k) => `${JSON.stringify(k)}  (${used.get(k)})`);
report('Platzhalter passen nicht', badVars, (k) => `${JSON.stringify(k)} → ${JSON.stringify(en.get(k))}`);
report('Verwaiste Einträge in en.ts', unused, (k) => JSON.stringify(k));
report('Doppelte Einträge in en.ts', dupes, (k) => JSON.stringify(k));
if (dynamic.length) console.warn(`\nHinweis: tr() mit berechnetem Text an ${dynamic.length} Stelle(n), nicht prüfbar: ${dynamic.slice(0, 5).join(', ')}`);
if (failed) { console.error('\nÜbersetzungen unvollständig. Texte in src/locales/en.ts ergänzen.'); process.exit(1); }
console.log(`Übersetzungen vollständig: ${used.size} Texte, Deutsch und Englisch.`);
