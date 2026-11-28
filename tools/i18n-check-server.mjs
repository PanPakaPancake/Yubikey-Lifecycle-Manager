// Prüft die Übersetzungen von Server und Agent (src/Ylm.Contracts/Localization/en.json):
// fehlende Einträge, verwaiste Einträge und abweichende Platzhalter {0}, {1} …
// Texte werden im Code als L.T("…"), L.N("…") oder L.F($"… {wert} …") markiert; bei L.F ist der Schlüssel
// der Text mit {0}, {1} … an Stelle der Werte (wie FormattableString.Format).
// Aufruf: node tools/i18n-check-server.mjs [--missing]   (--missing gibt fehlende Schlüssel als JSON aus)
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const src = join(root, 'src');
const enPath = join(src, 'Ylm.Contracts/Localization/en.json');

function* files(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (['bin', 'obj', 'Migrations'].includes(name)) continue;
    if (statSync(p).isDirectory()) yield* files(p);
    else if (name.endsWith('.cs')) yield p;
  }
}

const escapes = { n: '\n', r: '\r', t: '\t', '0': '\0', '\\': '\\', '"': '"', "'": "'" };

/** Liest ab s[i] (nach dem öffnenden ") einen normalen String; gibt [Wert, Ende] zurück. */
function readString(s, i) {
  let v = '';
  for (; i < s.length; i++) {
    const c = s[i];
    if (c === '\\') {
      const n = s[++i];
      if (n === 'u') { v += String.fromCharCode(parseInt(s.slice(i + 1, i + 5), 16)); i += 4; }
      else v += escapes[n] ?? n;
    } else if (c === '"') return [v, i + 1];
    else v += c;
  }
  throw new Error('String ohne Ende');
}

/** Liest einen interpolierten String ($"…") ab s[i] (nach dem "); Schlüssel wie FormattableString.Format. */
function readInterpolated(s, i) {
  let key = '', hole = 0;
  for (; i < s.length; i++) {
    const c = s[i];
    if (c === '\\') {
      const n = s[++i];
      if (n === 'u') { key += String.fromCharCode(parseInt(s.slice(i + 1, i + 5), 16)); i += 4; }
      else key += escapes[n] ?? n;
    } else if (c === '{' && s[i + 1] === '{') { key += '{{'; i++; }
    else if (c === '}' && s[i + 1] === '}') { key += '}}'; i++; }
    else if (c === '{') {
      // Ausdruck bis zur passenden Klammer; Format nach ':' auf oberster Ebene, Ausrichtung nach ','
      let depth = 0, j = i + 1, fmtStart = -1, alignStart = -1;
      for (; j < s.length; j++) {
        const d = s[j];
        if (d === '"') { j = (s[j - 1] === '$' ? readInterpolated(s, j + 1) : readString(s, j + 1))[1] - 1; continue; }
        if (d === "'") { j = s.indexOf("'", j + (s[j + 1] === '\\' ? 3 : 2)); continue; }
        if (d === '(' || d === '[' || d === '{') depth++;
        else if (d === ')' || d === ']') depth--;
        else if (d === '}') { if (depth === 0) break; depth--; }
        else if (d === '?' && depth === 0) depth += 0; // Bedingungen stehen in Klammern
        else if (d === ':' && depth === 0 && fmtStart < 0) { fmtStart = j; break; }
        else if (d === ',' && depth === 0 && alignStart < 0) alignStart = j;
      }
      let end = j;
      let fmt = '';
      if (fmtStart >= 0) { end = s.indexOf('}', fmtStart); fmt = ':' + s.slice(fmtStart + 1, end); }
      const align = alignStart >= 0 ? ',' + s.slice(alignStart + 1, fmtStart >= 0 ? fmtStart : end).trim() : '';
      key += `{${hole++}${align}${fmt}}`;
      i = end;
    } else if (c === '"') return [key, i + 1];
    else key += c;
  }
  throw new Error('Interpolierter String ohne Ende');
}

const used = new Map();
for (const f of files(src)) {
  const s = readFileSync(f, 'utf8');
  const rx = /\bL\.(T|N|F)\(\s*(\$?)"/g;
  let m;
  while ((m = rx.exec(s))) {
    const start = m.index + m[0].length;
    const [key] = m[2] ? readInterpolated(s, start) : readString(s, start);
    const line = s.slice(0, m.index).split('\n').length;
    if (!used.has(key)) used.set(key, `${relative(src, f)}:${line}`);
  }
}

const en = JSON.parse(readFileSync(enPath, 'utf8'));
const holes = (t) => [...t.replaceAll('{{', '').matchAll(/\{(\d+)[^}]*\}/g)].map((x) => x[1]).sort().join(',');
const missing = [...used.keys()].filter((k) => !(k in en));
const unused = Object.keys(en).filter((k) => !used.has(k));
const mismatch = Object.entries(en).filter(([k, v]) => used.has(k) && holes(k) !== holes(v)).map(([k]) => k);

if (process.argv.includes('--missing')) {
  console.log(JSON.stringify(Object.fromEntries(missing.map((k) => [k, used.get(k)])), null, 1));
  process.exit(0);
}
const show = (title, list, loc = true) => {
  if (!list.length) return;
  console.log(`\n${title} (${list.length}):`);
  for (const k of list.slice(0, 50)) console.log('  ' + JSON.stringify(k) + (loc && used.get(k) ? '  ' + used.get(k) : ''));
  if (list.length > 50) console.log('  …');
};
show('Ohne englische Übersetzung', missing);
show('Verwaiste Einträge in en.json', unused, false);
show('Platzhalter passen nicht', mismatch);
if (missing.length || unused.length || mismatch.length) {
  console.log('\nÜbersetzungen unvollständig. Texte in src/Ylm.Contracts/Localization/en.json ergänzen.');
  process.exit(1);
}
console.log(`Server-Übersetzungen vollständig: ${used.size} Texte, Deutsch und Englisch.`);
