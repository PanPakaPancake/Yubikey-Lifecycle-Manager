import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { api, batchStatusLabels, fmtDate, type Batch, type BatchCandidate, type BatchStatus, type BatchSummary, type Profile } from '../api';
import { Badge, Card, Empty, ErrorBox, Field, GroupSearch, Info, PageHeader, Spinner, useAction, useApi, useLiveSearch } from '../ui';
import { RegisterModes } from '../register';
import { ChooseTenant, useTenant } from '../tenant';

const statusTone: Record<BatchStatus, 'info' | 'ok' | 'neutral'> = { Open: 'info', Completed: 'ok', Cancelled: 'neutral' };

export function BatchStatusBadge({ status }: { status: BatchStatus }) {
  return <Badge tone={statusTone[status]}>{batchStatusLabels[status]}</Badge>;
}

/** Stapelregistrierung: Übersicht der Stapel des gewählten Tenants. */
export default function BatchesPage() {
  const { tenantId, tenant } = useTenant();
  const { data, error, loading } = useApi<BatchSummary[]>(tenantId ? `/api/tenants/${tenantId}/batches` : null, [tenantId]);
  return (
    <>
      <PageHeader
        title="Stapelregistrierung"
        subtitle="Viele Personen nacheinander mit Keys versorgen: Liste aus Gruppe, CSV oder Suche, dann Key für Key einstecken."
        actions={tenantId && <Link className="btn primary" to="/batches/new">Neuer Stapel</Link>}
      />
      <RegisterModes />
      {!tenantId && <ChooseTenant purpose="Für Stapel" />}
      <ErrorBox error={error} />
      {loading && !data && <Spinner />}
      {tenantId && data && data.length === 0 && <Empty>Für {tenant?.displayName} gibt es noch keinen Stapel.</Empty>}
      {data && data.length > 0 && (
        <div className="card table-wrap" style={{ padding: 0 }}>
          <table>
            <thead><tr><th>Name</th><th>Quelle</th><th>Status</th><th>Fortschritt</th><th>Angelegt</th></tr></thead>
            <tbody>
              {data.map((b) => (
                <tr key={b.id}>
                  <td><Link to={'/batches/' + b.id}>{b.name}</Link></td>
                  <td className="muted">{b.source ?? '–'}</td>
                  <td><BatchStatusBadge status={b.status} /></td>
                  <td style={{ minWidth: 180 }}><BatchProgress counts={b.counts} /></td>
                  <td className="small">{fmtDate(b.createdAt)}<br /><span className="muted">{b.createdByName}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

export function BatchProgress({ counts }: { counts: BatchSummary['counts'] }) {
  const finished = counts.done + counts.skipped;
  return (
    <div>
      <div className="progress" style={{ margin: '2px 0 4px' }}><span style={{ width: (counts.total ? (finished / counts.total) * 100 : 0) + '%' }} /></div>
      <span className="small">
        {counts.done} von {counts.total} registriert
        {counts.failed > 0 && <span className="tone-warn"> · {counts.failed} fehlgeschlagen</span>}
        {counts.skipped > 0 && <span className="muted"> · {counts.skipped} übersprungen</span>}
      </span>
    </div>
  );
}

type Source = 'group' | 'csv' | 'search';

interface Row extends BatchCandidate { selected: boolean; serialText: string }

const hasKey = (c: BatchCandidate) => c.activeKeys > 0 || (c.entraYubiKeys ?? 0) > 0;

const toRow = (c: BatchCandidate): Row => ({
  ...c,
  selected: c.accountEnabled && !hasKey(c),
  serialText: c.expectedSerialNumber ? String(c.expectedSerialNumber) : '',
});

/** Neuer Stapel: Personen sammeln, prüfen, Profil und optional feste Seriennummer pro Person. */
export function NewBatchPage() {
  const { tenantId, tenant } = useTenant();
  const navigate = useNavigate();
  const [source, setSource] = useState<Source>('group');
  const [sourceLabel, setSourceLabel] = useState<string>();
  const [rows, setRows] = useState<Row[]>([]);
  const [csvErrors, setCsvErrors] = useState<{ line: number; value: string; message: string }[]>([]);
  const [name, setName] = useState('');
  const [defaultProfileId, setDefaultProfileId] = useState('');
  const { busy, error, run } = useAction();
  const { data: profiles } = useApi<Profile[]>(tenantId ? '/api/profiles?tenantId=' + tenantId : null, [tenantId]);

  useEffect(() => {
    if (profiles && !defaultProfileId) {
      const def = profiles.find((p) => p.isDefault && p.tenantId === tenantId) ?? profiles.find((p) => p.isDefault) ?? profiles[0];
      if (def) setDefaultProfileId(def.id);
    }
  }, [profiles, defaultProfileId, tenantId]);

  // Neue Kandidaten anhängen; schon vorhandene Personen bleiben unverändert.
  const merge = (list: BatchCandidate[], label: string) => {
    setRows((cur) => {
      const known = new Set(cur.map((r) => r.entraUserId));
      return [...cur, ...list.filter((c) => !known.has(c.entraUserId)).map(toRow)];
    });
    setSourceLabel((cur) => (cur && cur !== label ? 'Gemischt' : label));
  };

  const load = (body: object, label: string) => run(async () => {
    const r = await api.post<{ candidates: BatchCandidate[]; errors: { line: number; value: string; message: string }[] }>(`/api/tenants/${tenantId}/batches/candidates`, body);
    merge(r.candidates, label);
    setCsvErrors(r.errors);
    if (!name && label.startsWith('Gruppe: ')) setName(label.slice(8));
  });

  const update = (id: string, patch: Partial<Row>) => setRows((cur) => cur.map((r) => (r.entraUserId === id ? { ...r, ...patch } : r)));
  const selected = rows.filter((r) => r.selected);
  const invalidSerial = selected.find((r) => r.serialText.trim() && !/^\d+$/.test(r.serialText.trim()));
  const duplicateSerial = useMemo(() => {
    const seen = new Set<string>();
    return selected.find((r) => { const s = r.serialText.trim(); if (!s) return false; if (seen.has(s)) return true; seen.add(s); return false; });
  }, [selected]);
  const withKey = rows.filter(hasKey).length;
  const profileName = (id?: string | null) => profiles?.find((p) => p.id === id)?.name;

  const create = () => run(async () => {
    const b = await api.post<Batch>(`/api/tenants/${tenantId}/batches`, {
      name, defaultProfileId, source: sourceLabel,
      items: selected.map((r) => ({
        entraUserId: r.entraUserId, userPrincipalName: r.userPrincipalName, userDisplayName: r.displayName, department: r.department,
        profileId: r.profileId || null, profileGroupName: r.profileGroupName, expectedSerialNumber: r.serialText.trim() ? Number(r.serialText.trim()) : null,
      })),
    });
    navigate('/batches/' + b.id);
  });

  if (!tenantId) return <><PageHeader title="Neuer Stapel" /><ChooseTenant purpose="Für Stapel" /></>;

  return (
    <>
      <PageHeader title="Neuer Stapel" subtitle={`Personen für die Stapelregistrierung in ${tenant?.displayName ?? 'diesem Tenant'} zusammenstellen`} actions={<Link className="btn" to="/batches">Zurück</Link>} />
      <Card title="1. Personen hinzufügen">
        <div className="tabs">
          <button className={source === 'group' ? 'selected' : ''} onClick={() => setSource('group')}>Entra-Gruppe</button>
          <button className={source === 'csv' ? 'selected' : ''} onClick={() => setSource('csv')}>CSV-Datei</button>
          <button className={source === 'search' ? 'selected' : ''} onClick={() => setSource('search')}>Personen suchen</button>
        </div>
        {source === 'group' && (
          <>
            <p className="small muted">Alle Mitglieder der Gruppe (auch über verschachtelte Gruppen) kommen in die Liste. Sie können danach einzelne abwählen.</p>
            <GroupSearch tenantId={tenantId} onPick={(g) => void load({ groupId: g.id }, 'Gruppe: ' + g.name)} />
          </>
        )}
        {source === 'csv' && <CsvSource busy={busy} onLoad={(csv) => void load({ csv }, 'CSV')} />}
        {source === 'search' && <PersonSearch tenantId={tenantId} exclude={rows.map((r) => r.entraUserId)} onPick={(id) => void load({ userIds: [id] }, 'Auswahl')} />}
        {busy && <Spinner label="Personen werden geprüft …" />}
        {csvErrors.length > 0 && (
          <Info tone="warn">
            <strong>{csvErrors.length} Zeile(n) wurden nicht übernommen:</strong>
            <ul>{csvErrors.slice(0, 30).map((e, i) => <li key={i}>{e.line > 0 ? `Zeile ${e.line}: ` : ''}{e.value} – {e.message}</li>)}</ul>
          </Info>
        )}
      </Card>

      {rows.length > 0 && (
        <Card title={`2. Liste prüfen (${selected.length} von ${rows.length} ausgewählt)`} actions={<button className="link" onClick={() => { setRows([]); setSourceLabel(undefined); setCsvErrors([]); }}>Liste leeren</button>}>
          {withKey > 0 && <Info tone="warn">{withKey} Person(en) haben bereits einen Key (im Inventar oder als YubiKey in Entra). Sie sind abgewählt; bei Bedarf wieder anhaken.</Info>}
          <p className="small muted">
            Die Reihenfolge der Liste ist die Reihenfolge am Agent: Jeder eingesteckte Key geht an die nächste offene Person.
            Steht bei einer Person eine Seriennummer, bekommt nur genau dieser Key die Person, egal an welcher Stelle er eingesteckt wird.
          </p>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th><input type="checkbox" aria-label="Alle auswählen" checked={selected.length === rows.length} onChange={(e) => setRows((cur) => cur.map((r) => ({ ...r, selected: e.target.checked && r.accountEnabled })))} /></th>
                  <th>Person</th><th>Keys</th><th>Profil</th><th>Feste Seriennummer (optional)</th><th />
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.entraUserId} className={r.selected ? '' : 'muted'}>
                    <td><input type="checkbox" checked={r.selected} disabled={!r.accountEnabled} onChange={(e) => update(r.entraUserId, { selected: e.target.checked })} aria-label={'Auswählen ' + r.userPrincipalName} /></td>
                    <td><strong>{r.displayName ?? r.userPrincipalName}</strong><br /><span className="small muted">{r.userPrincipalName}{r.department ? ' · ' + r.department : ''}</span></td>
                    <td><span className="badges">
                      {!r.accountEnabled && <Badge tone="error">deaktiviert</Badge>}
                      {r.activeKeys > 0 && <Badge tone="info">{r.activeKeys} im Inventar</Badge>}
                      {(r.entraYubiKeys ?? 0) > r.activeKeys && <Badge tone="warn">{r.entraYubiKeys} YubiKey(s) in Entra</Badge>}
                      {(r.entraOtherPasskeys ?? 0) > 0 && <Badge>{r.entraOtherPasskeys} anderer Passkey</Badge>}
                      {r.accountEnabled && !hasKey(r) && !r.entraOtherPasskeys && <Badge>kein Key</Badge>}
                    </span></td>
                    <td>
                      <select value={r.profileId ?? ''} onChange={(e) => update(r.entraUserId, { profileId: e.target.value || null, profileGroupName: null })} aria-label="Profil">
                        <option value="">Stapel-Standard: {profileName(defaultProfileId) ?? '–'}</option>
                        {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                      </select>
                      {r.profileId && r.profileGroupName && <div className="small muted">{r.profileGroupName === 'CSV' ? 'aus der CSV-Datei' : 'über Gruppe ' + r.profileGroupName}</div>}
                    </td>
                    <td><input className="mono" style={{ width: 130 }} inputMode="numeric" placeholder="beliebiger Key" value={r.serialText} onChange={(e) => update(r.entraUserId, { serialText: e.target.value })} /></td>
                    <td><button className="link" onClick={() => setRows((cur) => cur.filter((x) => x.entraUserId !== r.entraUserId))}>Entfernen</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      {rows.length > 0 && (
        <Card title="3. Stapel anlegen">
          <div className="grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))' }}>
            <Field label="Name" hint="Zum Wiederfinden, z. B. „Rollout Vertrieb März“."><input value={name} onChange={(e) => setName(e.target.value)} placeholder="Stapel vom heutigen Datum" /></Field>
            <Field label="Standardprofil" hint="Gilt für alle ohne eigenes Profil oder Gruppenprofil.">
              <select value={defaultProfileId} onChange={(e) => setDefaultProfileId(e.target.value)}>
                {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}{p.tenantId ? '' : ' (alle Tenants)'}</option>)}
              </select>
            </Field>
          </div>
          {invalidSerial && <Info tone="error">Die Seriennummer bei {invalidSerial.userPrincipalName} ist keine Zahl.</Info>}
          {duplicateSerial && <Info tone="error">Seriennummer {duplicateSerial.serialText} ist mehreren Personen zugeordnet.</Info>}
          <ErrorBox error={error} />
          <div className="actions">
            <button className="primary" disabled={busy || selected.length === 0 || !defaultProfileId || !!invalidSerial || !!duplicateSerial} onClick={create}>
              Stapel mit {selected.length} Person(en) anlegen
            </button>
          </div>
        </Card>
      )}
      {rows.length === 0 && <ErrorBox error={error} />}
    </>
  );
}

function CsvSource({ busy, onLoad }: { busy: boolean; onLoad: (csv: string) => void }) {
  const [text, setText] = useState('');
  const readFile = async (f?: File) => { if (f) setText(await f.text()); };
  return (
    <>
      <p className="small muted">
        Spalten mit Semikolon oder Komma getrennt, erste Zeile als Kopf: <code>UPN</code> (Pflicht, alternativ <code>Mail</code>),
        optional <code>Seriennummer</code> (fester Key für die Person) und <code>Profil</code> (Name eines Registrierungsprofils).
        Ohne Kopfzeile gilt: 1. Spalte UPN, 2. Seriennummer, 3. Profil.
      </p>
      <Field label="Datei"><input type="file" accept=".csv,text/csv,text/plain" onChange={(e) => void readFile(e.target.files?.[0])} /></Field>
      <Field label="oder Inhalt einfügen">
        <textarea rows={5} value={text} onChange={(e) => setText(e.target.value)} placeholder={'UPN;Seriennummer;Profil\nanna@contoso.com;;\nben@contoso.com;31234567;Standard'} style={{ width: '100%', fontFamily: 'monospace' }} />
      </Field>
      <div className="actions"><button disabled={busy || !text.trim()} onClick={() => onLoad(text)}>Liste prüfen und übernehmen</button></div>
    </>
  );
}

interface FoundUser { id: string; userPrincipalName: string; displayName?: string; department?: string; accountEnabled: boolean }

export function PersonSearch({ tenantId, exclude, onPick }: { tenantId: string; exclude: string[]; onPick: (id: string) => void }) {
  const [q, setQ] = useState('');
  const { results, busy, error } = useLiveSearch<FoundUser>((term) => `/api/tenants/${tenantId}/users?q=${encodeURIComponent(term)}`, q);
  const visible = results?.filter((u) => !exclude.includes(u.id));
  return (
    <>
      <div className="toolbar" style={{ margin: 0 }}>
        <input type="search" placeholder="Name, UPN oder Mail eintippen (ab 2 Zeichen)" value={q} onChange={(e) => setQ(e.target.value)} style={{ flex: 1 }} />
        {busy && <Spinner />}
      </div>
      <ErrorBox error={error} />
      {visible && (
        <div className="pick" style={{ marginTop: 8 }}>
          {visible.map((u) => (
            <button key={u.id} disabled={!u.accountEnabled} onClick={() => onPick(u.id)}>
              <span><strong>{u.displayName ?? u.userPrincipalName}</strong><br /><span className="small muted">{u.userPrincipalName}{u.department ? ' · ' + u.department : ''}</span></span>
              <span>{u.accountEnabled ? '+ hinzufügen' : <Badge tone="error">deaktiviert</Badge>}</span>
            </button>
          ))}
          {visible.length === 0 && <span className="muted small">Keine (weitere) Person gefunden.</span>}
        </div>
      )}
    </>
  );
}
