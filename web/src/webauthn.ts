/** Browser side of WebAuthn: converts the server's JSON options and the authenticator's answer (base64url <-> ArrayBuffer). */

type Json = Record<string, unknown>;

const toBuffer = (s: string) => {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(b64 + '==='.slice((b64.length + 3) % 4));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0)).buffer;
};
const toB64 = (b: ArrayBuffer | null | undefined) =>
  b ? btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') : undefined;

const descriptors = (list?: { id: string; type: string; transports?: string[] }[]) =>
  list?.map((c) => ({ ...c, id: toBuffer(c.id) })) as PublicKeyCredentialDescriptor[] | undefined;

export const passkeysSupported = () => typeof window !== 'undefined' && !!window.PublicKeyCredential;

export async function createPasskey(options: Json) {
  const o = options as Json & { challenge: string; user: { id: string; name: string; displayName: string }; excludeCredentials?: { id: string; type: string }[] };
  const publicKey = {
    ...o,
    challenge: toBuffer(o.challenge),
    user: { ...o.user, id: toBuffer(o.user.id) },
    excludeCredentials: descriptors(o.excludeCredentials) ?? [],
  } as unknown as PublicKeyCredentialCreationOptions;
  const cred = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Abgebrochen.');
  const r = cred.response as AuthenticatorAttestationResponse;
  return {
    id: cred.id,
    rawId: toB64(cred.rawId),
    type: cred.type,
    response: { attestationObject: toB64(r.attestationObject), clientDataJSON: toB64(r.clientDataJSON), transports: r.getTransports?.() ?? [] },
    clientExtensionResults: cred.getClientExtensionResults(),
  };
}

export async function getPasskey(options: Json) {
  const o = options as Json & { challenge: string; allowCredentials?: { id: string; type: string }[] };
  const publicKey = { ...o, challenge: toBuffer(o.challenge), allowCredentials: descriptors(o.allowCredentials) ?? [] } as unknown as PublicKeyCredentialRequestOptions;
  const cred = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Abgebrochen.');
  const r = cred.response as AuthenticatorAssertionResponse;
  return {
    id: cred.id,
    rawId: toB64(cred.rawId),
    type: cred.type,
    response: { authenticatorData: toB64(r.authenticatorData), clientDataJSON: toB64(r.clientDataJSON), signature: toB64(r.signature), userHandle: toB64(r.userHandle) },
    clientExtensionResults: cred.getClientExtensionResults(),
  };
}

/** Friendly German text for the typical browser errors. */
export function passkeyError(e: unknown) {
  if (e instanceof DOMException) {
    if (e.name === 'NotAllowedError') return 'Abgebrochen oder Zeit abgelaufen. Bitte erneut versuchen und den Schlüssel berühren.';
    if (e.name === 'InvalidStateError') return 'Dieser Schlüssel ist für dieses Konto bereits eingerichtet.';
    if (e.name === 'SecurityError') return 'Passkeys funktionieren nur über HTTPS mit der richtigen Server-Adresse.';
  }
  return e instanceof Error ? e.message : String(e);
}
