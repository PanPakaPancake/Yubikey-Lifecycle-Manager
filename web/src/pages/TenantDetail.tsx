import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { api, fieldTypes, fmtDate, keyFromLabel, type CustomField, type GroupRef, type Profile, type ResolvedField, type Tenant } from '../api';
import { useCan } from '../auth';
import { Badge, Card, download, ErrorBox, Field, GroupChips, GroupSearch, Info, Modal, OriginBadge, PageHeader, Spinner, useAction, useApi } from '../ui';
import { useToast } from '../feedback';
import { BrandingCard } from './Branding';

const statusText = { Ok: 'In Ordnung', Warning: 'Hinweis', Failed: 'Fehlt' } as const;

export default function TenantDetailPage({ id: propId }: { id?: string }) {
  const routeParams = useParams();
  const id = propId ?? routeParams.id;
  const [params] = useSearchParams();
  const can = useCan();
  const { data: t, error, setData } = useApi<Tenant>('/api/tenants/' + id, [id]);
  const act = useAction();
  const toast = useToast();
  const [offboarding, setOffboarding] = useState(false);

  if (error) return <ErrorBox error={error} />;
  if (!t) return <Spinner label="Wird geladen …" />;
  const active = t.status === 'Active';
  const fixable = t.checks.filter((c) => c.canFix && c.status !== 'Ok');

  const runChecks = () => act.run(async () => setData(await api.post<Tenant>(`/api/tenants/${t.id}/checks`)));
  const fix = (ids: string[]) => act.run(async () => setData(await api.post<Tenant>(`/api/tenants/${t.id}/fix`, { checkIds: ids })));
  const sync = () => act.run(async () => {
    const r = await api.post<{ usersScanned: number; imported: number; updated: number; removed: number }>(`/api/tenants/${t.id}/sync`);
    toast.ok(`Synchronisiert: ${r.usersScanned} Personen geprüft, ${r.imported} Keys neu übernommen, ${r.updated} aktualisiert, ${r.removed} entfernt.`);
    setData(await api.get<Tenant>('/api/tenants/' + t.id));
  });

  return (
    <>
      <PageHeader
        title={t.displayName}
        subtitle={<><Link to="/tenants">Tenants</Link> · {t.defaultDomain} · <span className="mono small">{t.entraTenantId}</span></>}
        actions={active && <>
          {can('SyncTenants') && <button disabled={act.busy} onClick={sync}>Jetzt synchronisieren</button>}
          {can('ManageTenants') && <button onClick={() => download(`/api/tenants/${t.id}/export.zip`)}>Daten exportieren</button>}
        </>}
      />
      {params.get('onboarded') && <Info tone="ok">Der Tenant ist angebunden. Prüfen Sie unten die FIDO2-Einstellungen.</Info>}
      {t.status === 'Offboarded' && <Info tone="warn">Dieser Tenant wurde am {fmtDate(t.offboardedAt)} offboarded. Die Daten werden am {fmtDate(t.dataDeletionDueAt, false)} gelöscht. Ein erneuter Admin-Consent stellt ihn wieder her.</Info>}
      {t.status === 'PendingConsent' && <Info tone="warn">Der Admin-Consent für diesen Tenant fehlt noch. Fügen Sie den Tenant unter „Tenants“ erneut hinzu.</Info>}
      <ErrorBox error={act.error} />

      <Card
        title="Prüfungen"
        actions={<>
          <span className="small muted">Zuletzt {fmtDate(t.lastCheckAt)}</span>
          {active && <button disabled={act.busy} onClick={runChecks}>Erneut prüfen</button>}
          {active && can('ManageTenants') && fixable.length > 1 && <button className="primary" disabled={act.busy} onClick={() => fix(fixable.map((c) => c.id))}>Alles beheben</button>}
        </>}
      >
        <div className="checks">
          {t.checks.map((c) => (
            <div key={c.id} className="check-row">
              <span className={'dot ' + c.status} aria-label={statusText[c.status]} />
              <div style={{ flex: 1 }}>
                <strong>{c.title}</strong> <span className="small muted">{statusText[c.status]}</span>
                <div>{c.message}</div>
                {c.status !== 'Ok' && c.manualInstructions && (
                  <details><summary className="small">Manuell beheben</summary><p className="small" style={{ whiteSpace: 'pre-wrap' }}>{c.manualInstructions}</p></details>
                )}
              </div>
              {active && c.canFix && c.status !== 'Ok' && can('ManageTenants') && <button className="primary" disabled={act.busy} onClick={() => fix([c.id])}>Jetzt beheben</button>}
            </div>
          ))}
          {t.checks.length === 0 && <p className="muted">Noch nicht geprüft.</p>}
        </div>
      </Card>

      {active && (
        <div className="grid cols-2 mt">
          <SettingsCard tenant={t} onSaved={setData} />
          <Card title="Synchronisation">
            <table>
              <tbody>
                <tr><th>Letzter Sync</th><td>{fmtDate(t.lastSyncAt)} {t.lastSyncSucceeded === false && <Badge tone="error">Fehler</Badge>}</td></tr>
                {t.lastSyncMessage && <tr><th>Meldung</th><td className="small">{t.lastSyncMessage}</td></tr>}
                <tr><th>Personen in Zielgruppe</th><td>{t.targetUserCount ?? '–'}</td></tr>
                <tr><th>Admin-Consent</th><td>{fmtDate(t.consentedAt)} durch {t.consentedBy}</td></tr>
              </tbody>
            </table>
          </Card>
        </div>
      )}

      {active && <GroupProfilesCard tenantId={t.id} editable={can('ManageProfiles')} />}
      {active && <FieldsCard tenantId={t.id} editable={can('ManageProfiles')} />}
      {active && <BrandingCard tenantId={t.id} editable={!!can('ManageTenants')} />}
      {active && can('ManageApiKeys') && (
        <Card title="API-Schlüssel" className="mt">
          <p className="muted small" style={{ margin: 0 }}>API-Schlüssel werden jetzt plattformweit verwaltet und gelten für alle Tenants oder nur für ausgewählte. <Link to="/platform#api">Zur API-Verwaltung</Link></p>
        </Card>
      )}

      {active && can('ManageTenants') && (
        <Card title="Offboarding" className="mt">
          <p>Die Plattform nutzt den Tenant danach nicht mehr, API-Schlüssel verlieren den Zugriff auf ihn und die Daten werden nach der Aufbewahrungsfrist gelöscht. Registrierte Keys in Entra ID bleiben unverändert.</p>
          <button className="danger" onClick={() => setOffboarding(true)}>Tenant offboarden</button>
        </Card>
      )}
      {offboarding && <OffboardModal tenant={t} onClose={() => setOffboarding(false)} />}
    </>
  );
}

function SettingsCard({ tenant, onSaved }: { tenant: Tenant; onSaved: (t: Tenant) => void }) {
  const can = useCan();
  const [name, setName] = useState(tenant.displayName);
  const [interval, setInterval] = useState(tenant.syncIntervalMinutes);
  const [include, setInclude] = useState<GroupRef[]>(tenant.targetIncludeGroups ?? []);
  const [exclude, setExclude] = useState<GroupRef[]>(tenant.targetExcludeGroups ?? []);
  const [fourEyes, setFourEyes] = useState(!!tenant.requireBreakglassApproval);
  const { busy, error, run } = useAction();
  const [saved, setSaved] = useState(false);
  const toast = useToast();
  const editable = can('ManageTenants');
  const changed = () => setSaved(false);
  const used = [...include, ...exclude].map((g) => g.id);

  const save = () => run(async () => {
    onSaved(await api.patch<Tenant>(`/api/tenants/${tenant.id}`, { displayName: name, syncIntervalMinutes: interval, targetIncludeGroups: include, targetExcludeGroups: exclude, requireBreakglassApproval: fourEyes }));
    setSaved(true);
    toast.ok('Einstellungen gespeichert.');
  });

  return (
    <Card title="Einstellungen">
      <Field label="Anzeigename"><input value={name} disabled={!editable} onChange={(e) => { setName(e.target.value); changed(); }} /></Field>

      <div className="field-group">
        <span className="field-label">Zielgruppe</span>
        <p className="field-hint" style={{ margin: '0 0 6px' }}>Personen, die einen YubiKey bekommen sollen. Grundlage für die Abdeckung im Dashboard und für die Prüfung der FIDO2-Richtlinie.</p>
        <div className="small"><strong>Einschließen</strong></div>
        <GroupChips groups={include} empty="Keine Gruppe gewählt: alle aktiven Benutzer des Tenants." onRemove={editable ? (id) => { setInclude(include.filter((g) => g.id !== id)); changed(); } : undefined} />
        {editable && <GroupSearch tenantId={tenant.id} exclude={used} placeholder="Gruppe suchen …" onPick={(g) => { setInclude([...include, g]); changed(); }} />}
        <div className="small" style={{ marginTop: 10 }}><strong>Ausschließen</strong></div>
        <GroupChips groups={exclude} empty="Keine Ausschlussgruppe." onRemove={editable ? (id) => { setExclude(exclude.filter((g) => g.id !== id)); changed(); } : undefined} />
        {editable && <GroupSearch tenantId={tenant.id} exclude={used} placeholder="z. B. Dienstkonten" onPick={(g) => { setExclude([...exclude, g]); changed(); }} />}
        <p className="field-hint" style={{ margin: '6px 0 0' }}>Zielgruppe = Mitglieder aller Einschlussgruppen ohne die Mitglieder der Ausschlussgruppen. Ausschlussgruppen werden in der FIDO2-Richtlinie nicht gesperrt, sie zählen nur nicht zur Abdeckung.</p>
      </div>

      <Field label="Sync-Intervall (Minuten)"><input type="number" min={5} max={1440} value={interval} disabled={!editable} onChange={(e) => { setInterval(Number(e.target.value)); changed(); }} /></Field>
      <label className="check" style={{ marginBottom: 12 }}>
        <input type="checkbox" checked={fourEyes} disabled={!editable} onChange={(e) => { setFourEyes(e.target.checked); changed(); }} />
        <span>Vier-Augen-Freigabe für Breakglass-Keys<br /><span className="small muted">Ein zweiter Admin mit dem Recht „Breakglass freigeben“ muss jede Breakglass-Registrierung vorab freigeben (gilt 24 Stunden für einen Durchlauf).</span></span>
      </label>
      <ErrorBox error={error} />
      {editable && <button className="primary" disabled={busy} onClick={save}>{saved ? 'Gespeichert' : 'Speichern'}</button>}
    </Card>
  );
}

interface GroupProfileRow { groupId: string; groupName: string; profileId: string; profileName?: string }

/** Group level of the hierarchy: members of an Entra group get a profile suggested in the wizard. */
function GroupProfilesCard({ tenantId, editable }: { tenantId: string; editable: boolean }) {
  const { data, reload } = useApi<GroupProfileRow[]>(`/api/tenants/${tenantId}/group-profiles`, [tenantId]);
  const { data: profiles } = useApi<Profile[]>('/api/profiles?tenantId=' + tenantId, [tenantId]);
  const [draft, setDraft] = useState<GroupProfileRow[] | null>(null);
  const { busy, error, run } = useAction();
  const list = draft ?? data ?? [];
  const firstProfile = profiles?.find((p) => p.isDefault)?.id ?? profiles?.[0]?.id ?? '';
  const move = (i: number, d: number) => {
    const next = [...list];
    [next[i], next[i + d]] = [next[i + d], next[i]];
    setDraft(next);
  };
  const save = () => run(async () => {
    await api.put(`/api/tenants/${tenantId}/group-profiles`, list.map((r) => ({ groupId: r.groupId, groupName: r.groupName, profileId: r.profileId })));
    await reload();
    setDraft(null);
  });

  return (
    <Card title="Profile nach Gruppe" className="mt">
      <p className="muted small" style={{ marginTop: 0 }}>
        Gruppenebene: Ist eine Person Mitglied einer dieser Entra-Gruppen, schlägt der Registrierungs-Assistent das zugeordnete Profil vor.
        Bei mehreren Treffern gewinnt die obere Zeile. Ohne Treffer gilt das Standardprofil.
      </p>
      {list.length > 0 && (
        <table>
          <thead><tr><th>Reihenfolge</th><th>Gruppe</th><th>Profil</th><th /></tr></thead>
          <tbody>
            {list.map((r, i) => (
              <tr key={r.groupId}>
                <td>
                  {i + 1}
                  {editable && <> <button className="link" disabled={i === 0} onClick={() => move(i, -1)} aria-label="nach oben">▲</button> <button className="link" disabled={i === list.length - 1} onClick={() => move(i, 1)} aria-label="nach unten">▼</button></>}
                </td>
                <td>{r.groupName}</td>
                <td>
                  <select value={r.profileId} disabled={!editable} onChange={(e) => setDraft(list.map((x, j) => (j === i ? { ...x, profileId: e.target.value } : x)))}>
                    {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}{p.tenantId ? '' : ' (alle Tenants)'}</option>)}
                  </select>
                </td>
                <td>{editable && <button className="link" onClick={() => setDraft(list.filter((_, j) => j !== i))}>Entfernen</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {list.length === 0 && <p className="muted">Keine Zuordnung. Alle Personen bekommen das Standardprofil vorgeschlagen.</p>}
      {editable && firstProfile && (
        <div style={{ marginTop: 10 }}>
          <GroupSearch tenantId={tenantId} exclude={list.map((r) => r.groupId)} placeholder="Gruppe suchen …"
            onPick={(g) => setDraft([...list, { groupId: g.id, groupName: g.name, profileId: firstProfile }])} />
        </div>
      )}
      <ErrorBox error={error} />
      {draft && <div className="actions" style={{ marginTop: 12 }}><button onClick={() => setDraft(null)}>Verwerfen</button><button className="primary" disabled={busy} onClick={save}>Zuordnungen speichern</button></div>}
    </Card>
  );
}

export function FieldExplanation() {
  return (
    <details style={{ margin: '0 0 12px' }}>
      <summary>Was bedeuten die Spalten?</summary>
      <dl className="explain" style={{ marginTop: 8 }}>
        <dt>Bezeichnung</dt><dd>Name des Feldes, wie er im Inventar und in den Formularen angezeigt wird. Kann jederzeit geändert werden.</dd>
        <dt>Schlüssel</dt><dd>Technischer Name des Feldes. Er ist der Spaltenname im CSV-Export und CSV-Import und der Name unter <code>customFields</code> in der Lese-API. Erlaubt sind Buchstaben, Ziffern, _ und -. Nach dem Speichern nicht mehr änderbar, damit gespeicherte Werte und angebundene Tools weiter passen.</dd>
        <dt>Typ</dt><dd>Text, Zahl, Datum (JJJJ-MM-TT), Auswahl aus festen Optionen oder Ja/Nein. Die Eingabe wird beim Speichern geprüft.</dd>
        <dt>Optionen</dt><dd>Nur bei Typ „Auswahl“: die erlaubten Werte, mit Komma getrennt.</dd>
        <dt>Pflicht</dt><dd>Das Feld muss bei jedem Key ausgefüllt werden, auch beim Lagerzugang und beim CSV-Import.</dd>
        <dt>Herkunft</dt><dd>„Global“ wird auf Plattformebene angelegt und an alle Tenants vererbt. Ein Tenant kann ein globales Feld für sich verändern (Bezeichnung, Pflicht, Optionen) oder deaktivieren. „Lokal“ gibt es nur in diesem Tenant.</dd>
      </dl>
    </details>
  );
}

type OverrideDraft = { disabled: boolean; label: string; required: boolean; options: string[] };

function FieldsCard({ tenantId, editable }: { tenantId: string; editable: boolean }) {
  const { data, reload } = useApi<ResolvedField[]>(`/api/tenants/${tenantId}/fields/config`, [tenantId]);
  const [local, setLocal] = useState<CustomField[] | null>(null);
  const [overrides, setOverrides] = useState<Record<string, OverrideDraft> | null>(null);
  const { busy, error, run } = useAction();

  const globals = (data ?? []).filter((f) => f.origin === 'Global');
  const localList = local ?? (data ?? []).filter((f) => f.origin === 'Tenant');
  const ov = (f: ResolvedField): OverrideDraft => overrides?.[f.id] ?? { disabled: f.state === 'Disabled', label: f.label, required: f.required, options: f.options };
  const setOv = (f: ResolvedField, patch: Partial<OverrideDraft>) => setOverrides({ ...(overrides ?? {}), [f.id]: { ...ov(f), ...patch } });
  const stateOf = (f: ResolvedField): ResolvedField['state'] => {
    const o = ov(f);
    const base = f.inherited!;
    if (o.disabled) return 'Disabled';
    return o.label.trim() === base.label && o.required === base.required && o.options.join('|') === base.options.join('|') ? 'Inherited' : 'Modified';
  };
  const reset = (f: ResolvedField) => setOv(f, { disabled: false, label: f.inherited!.label, required: f.inherited!.required, options: f.inherited!.options });
  const dirty = local !== null || overrides !== null;
  const updateLocal = (i: number, patch: Partial<CustomField>) => setLocal(localList.map((f, j) => (j === i ? { ...f, ...patch } : f)));

  const save = () => run(async () => {
    await api.put(`/api/tenants/${tenantId}/fields`, {
      local: localList.map((f) => ({ key: f.key, label: f.label, type: f.type, required: f.required, options: f.type === 'Select' ? f.options : [] })),
      overrides: globals.map((f) => ({ fieldId: f.id, ...ov(f), options: f.type === 'Select' ? ov(f).options : null })),
    });
    await reload();
    setLocal(null);
    setOverrides(null);
  });

  return (
    <Card title="Zusatzfelder für Keys" className="mt" actions={editable && <button onClick={() => setLocal([...localList, { key: '', label: '', type: 'Text', required: false, options: [] }])}>Lokales Feld hinzufügen</button>}>
      <div className="hierarchy" aria-label="Vererbung">
        <span className="level">Plattform (global)</span><span aria-hidden>→</span><span className="level current">Tenant</span>
        <span className="muted">Globale Felder pflegen Sie unter <Link to="/platform#fields">Plattform</Link>.</span>
      </div>
      <p className="muted small" style={{ marginTop: 0 }}>Eigene Merkmale für jeden Key, z. B. Kostenstelle, Standort oder Inventarnummer. Sie erscheinen im Inventar, beim Lagerzugang, im Export und in der Lese-API.</p>
      <FieldExplanation />
      {globals.length === 0 && localList.length === 0 && <p className="muted">Keine Zusatzfelder.</p>}
      {(globals.length > 0 || localList.length > 0) && (
        <div style={{ overflowX: 'auto' }}><table>
          <thead><tr><th>Bezeichnung und Herkunft</th><th>Schlüssel</th><th>Typ</th><th>Optionen</th><th>Pflicht</th><th /></tr></thead>
          <tbody>
            {globals.map((f) => {
              const o = ov(f);
              const st = stateOf(f);
              return (
                <tr key={f.id} className={st === 'Disabled' ? 'disabled-row' : ''}>
                  <td>
                    <div style={{ marginBottom: 4 }}><OriginBadge origin="Global" state={st} /></div>
                    <input value={o.label} disabled={!editable || o.disabled} onChange={(e) => setOv(f, { label: e.target.value })} />
                    {st === 'Modified' && o.label.trim() !== f.inherited!.label && <div className="small muted">Global: {f.inherited!.label}</div>}
                  </td>
                  <td className="mono">{f.key}</td>
                  <td>{fieldTypes[f.type]}</td>
                  <td>{f.type === 'Select' && <input value={o.options.join(', ')} disabled={!editable || o.disabled} onChange={(e) => setOv(f, { options: e.target.value.split(',').map((x) => x.trim()).filter(Boolean) })} />}</td>
                  <td><input type="checkbox" checked={o.required} disabled={!editable || o.disabled} onChange={(e) => setOv(f, { required: e.target.checked })} /></td>
                  <td>
                    {editable && (
                      <span style={{ display: 'inline-flex', flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
                        <button className="link" onClick={() => setOv(f, { disabled: !o.disabled })}>{o.disabled ? 'Aktivieren' : 'Deaktivieren'}</button>
                        {st !== 'Inherited' && <button className="link" onClick={() => reset(f)} title="Vererbung wiederherstellen: wieder unverändert von der Plattform übernehmen">Zurücksetzen</button>}
                      </span>
                    )}
                  </td>
                </tr>
              );
            })}
            {localList.map((f, i) => (
              <tr key={'l' + i}>
                <td>
                  <div style={{ marginBottom: 4 }}><OriginBadge origin="Tenant" /></div>
                  <input value={f.label} disabled={!editable} placeholder="z. B. Raum" onChange={(e) => updateLocal(i, { label: e.target.value, key: f.id ? f.key : keyFromLabel(e.target.value) })} />
                </td>
                <td><input value={f.key} disabled={!editable || !!f.id} onChange={(e) => updateLocal(i, { key: e.target.value })} className="mono" title={f.id ? 'Der Schlüssel kann nach dem Speichern nicht mehr geändert werden.' : 'Wird aus der Bezeichnung vorgeschlagen.'} /></td>
                <td>
                  <select value={f.type} disabled={!editable} onChange={(e) => updateLocal(i, { type: e.target.value as CustomField['type'] })}>
                    {Object.entries(fieldTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                  </select>
                </td>
                <td>{f.type === 'Select' && <input value={f.options.join(', ')} disabled={!editable} placeholder="A, B, C" onChange={(e) => updateLocal(i, { options: e.target.value.split(',').map((o) => o.trim()).filter(Boolean) })} />}</td>
                <td><input type="checkbox" checked={f.required} disabled={!editable} onChange={(e) => updateLocal(i, { required: e.target.checked })} /></td>
                <td>{editable && <button className="link" onClick={() => setLocal(localList.filter((_, j) => j !== i))}>Entfernen</button>}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      <ErrorBox error={error} />
      {dirty && <div className="actions" style={{ marginTop: 12 }}><button onClick={() => { setLocal(null); setOverrides(null); }}>Verwerfen</button><button className="primary" disabled={busy} onClick={save}>Felder speichern</button></div>}
    </Card>
  );
}

function OffboardModal({ tenant, onClose }: { tenant: Tenant; onClose: () => void }) {
  const navigate = useNavigate();
  const [confirmName, setConfirmName] = useState('');
  const [result, setResult] = useState<{ removeServicePrincipalUrl: string; instructions: string }>();
  const { busy, error, run } = useAction();
  const go = () => run(async () => setResult(await api.post(`/api/tenants/${tenant.id}/offboard`, { confirm: true })));
  if (result) {
    return (
      <Modal title="Tenant offboarded" onClose={() => navigate('/tenants')} footer={<button className="primary" onClick={() => navigate('/tenants')}>Fertig</button>}>
        <p>{result.instructions}</p>
        <a className="btn" href={result.removeServicePrincipalUrl} target="_blank" rel="noreferrer">Unternehmensanwendung im Entra Admin Center öffnen</a>
      </Modal>
    );
  }
  return (
    <Modal title="Tenant offboarden" onClose={onClose} footer={<><button onClick={onClose}>Abbrechen</button><button className="danger" disabled={confirmName !== tenant.displayName || busy} onClick={go}>Offboarden</button></>}>
      <Info tone="warn">Exportieren Sie die Daten vorher, wenn Sie sie aufbewahren müssen.</Info>
      <Field label={`Zur Bestätigung den Namen „${tenant.displayName}“ eingeben`}><input value={confirmName} onChange={(e) => setConfirmName(e.target.value)} /></Field>
      <ErrorBox error={error} />
    </Modal>
  );
}
