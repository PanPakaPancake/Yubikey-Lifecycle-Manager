import { api, fmtDate, loginKindLabel, type UserPasskey } from '../api';
import { useAuth } from '../auth';
import { Card, ErrorBox, PageHeader, Spinner, useAction, useApi } from '../ui';
import { createPasskey, passkeyError } from '../webauthn';
import { useConfirm, useToast } from '../feedback';
import RecoveryCodesCard from './RecoveryCodes';

/** Eigenes Konto: Anmeldeart und, bei lokalen Konten, die eigenen Passkeys. */
export default function AccountPage() {
  const { me } = useAuth();
  const user = me.user!;
  const local = user.loginKind === 'Local';
  const { data, error, reload } = useApi<UserPasskey[]>(local ? '/api/me/passkeys' : null);
  const { busy, error: actionError, run } = useAction();
  const confirm = useConfirm();
  const toast = useToast();

  const add = () => run(async () => {
    const name = window.prompt('Name des neuen Schlüssels', 'YubiKey Ersatz');
    if (name === null) return;
    const { requestId, options } = await api.post<{ requestId: string; options: Record<string, unknown> }>('/api/me/passkeys/options');
    let response;
    try { response = await createPasskey(options); } catch (e) { throw new Error(passkeyError(e)); }
    await api.post('/api/me/passkeys', { requestId, response, name });
    reload();
  });
  const remove = async (p: UserPasskey) => {
    if (!(await confirm({ title: 'Passkey entfernen?', message: `Mit „${p.name}“ ist danach keine Anmeldung mehr möglich.`, confirmLabel: 'Entfernen', danger: true }))) return;
    await run(async () => {
      await api.del('/api/me/passkeys/' + p.id);
      toast.ok(`Passkey „${p.name}“ entfernt.`);
      reload();
    });
  };

  return (
    <>
      <PageHeader title="Mein Konto" subtitle={`${user.displayName} · ${user.upn}`} />
      <Card title="Anmeldung">
        <p>Anmeldeart: <strong>{loginKindLabel[user.loginKind] ?? user.loginKind}</strong></p>
        {user.loginKind === 'Entra' && <p className="muted">Sie melden sich mit Ihrem Microsoft-Konto an. Passkeys verwalten Sie in Entra (My Sign-ins).</p>}
        {user.loginKind === 'Emergency' && <p className="muted">Notfallkonto mit Passwort und Authenticator-Code. Zum Zurücksetzen erzeugt ein Owner einen neuen Einrichtungslink.</p>}
      </Card>
      {me.recoveryCodes != null && <RecoveryCodesCard />}
      {local && (
        <Card title="Meine Passkeys" actions={<button className="primary" disabled={busy} onClick={add}>Passkey hinzufügen</button>}>
          <p className="muted">Richten Sie am besten einen zweiten YubiKey als Ersatz ein. Ohne Passkey ist keine Anmeldung möglich.</p>
          <ErrorBox error={error ?? actionError} />
          {!data && !error && <Spinner />}
          {data && (
            <table>
              <thead><tr><th>Name</th><th>Eingerichtet</th><th>Zuletzt verwendet</th><th /></tr></thead>
              <tbody>
                {data.map((p) => (
                  <tr key={p.id}>
                    <td>{p.name}</td>
                    <td className="small">{fmtDate(p.createdAt)}</td>
                    <td className="small">{p.lastUsedAt ? fmtDate(p.lastUsedAt) : '–'}</td>
                    <td><button className="link" disabled={busy || data.length < 2} title={data.length < 2 ? 'Der letzte Passkey kann nicht entfernt werden' : undefined} onClick={() => remove(p)}>Entfernen</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
      )}
    </>
  );
}
