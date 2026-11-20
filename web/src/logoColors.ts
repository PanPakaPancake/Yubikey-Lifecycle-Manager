/**
 * Ermittelt bis zu drei markante Farben aus einem Logo (für die Akzentfarbe im Whitelabeling).
 * Läuft im Browser: Bild auf eine kleine Leinwand zeichnen, Pixel in Farbfächer einsortieren und nach Häufigkeit
 * mal Sättigung gewichten. Transparente, fast weiße und fast schwarze Pixel zählen nicht, sonst gewinnt der Hintergrund.
 */
export async function logoColors(url: string, max = 3): Promise<string[]> {
  const img = await new Promise<HTMLImageElement>((resolve, reject) => {
    const i = new Image();
    i.onload = () => resolve(i);
    i.onerror = () => reject(new Error('Das Logo ließ sich nicht laden.'));
    i.src = url;
  });
  const size = 96;
  const scale = Math.min(1, size / Math.max(img.naturalWidth, img.naturalHeight));
  const w = Math.max(1, Math.round(img.naturalWidth * scale)), h = Math.max(1, Math.round(img.naturalHeight * scale));
  const canvas = document.createElement('canvas');
  canvas.width = w; canvas.height = h;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) return [];
  ctx.drawImage(img, 0, 0, w, h);
  const px = ctx.getImageData(0, 0, w, h).data;

  type Bucket = { r: number; g: number; b: number; n: number; score: number };
  const buckets = new Map<number, Bucket>();
  const greys = new Map<number, Bucket>();
  for (let i = 0; i < px.length; i += 4) {
    const [r, g, b, a] = [px[i], px[i + 1], px[i + 2], px[i + 3]];
    if (a < 128) continue;
    const mx = Math.max(r, g, b), mn = Math.min(r, g, b);
    const light = (mx + mn) / 510;
    if (light > 0.94 || light < 0.06) continue; // Hintergrundweiß und reines Schwarz
    const sat = mx === mn ? 0 : (mx - mn) / (255 - Math.abs(mx + mn - 255));
    const key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
    const target = sat < 0.2 ? greys : buckets;
    const k = target.get(key) ?? { r: 0, g: 0, b: 0, n: 0, score: 0 };
    k.r += r; k.g += g; k.b += b; k.n++; k.score += 0.3 + sat;
    target.set(key, k);
  }
  const pick = (m: Map<number, Bucket>) => [...m.values()].sort((a, b) => b.score - a.score)
    .map((k) => [k.r / k.n, k.g / k.n, k.b / k.n] as const);
  const candidates = [...pick(buckets), ...pick(greys)];
  const out: (readonly [number, number, number])[] = [];
  for (const c of candidates) {
    // Ähnliche Töne zusammenfassen, damit die Vorschläge sich wirklich unterscheiden.
    if (out.every((o) => Math.hypot(o[0] - c[0], o[1] - c[1], o[2] - c[2]) > 60)) out.push(c);
    if (out.length >= max) break;
  }
  return out.map(([r, g, b]) => '#' + [r, g, b].map((v) => Math.round(v).toString(16).padStart(2, '0')).join(''));
}
