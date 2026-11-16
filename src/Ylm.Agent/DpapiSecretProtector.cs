using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Ylm.Agent.Core;

namespace Ylm.Agent;

/// <summary>Protects the agent token with DPAPI (machine scope, only readable on this PC).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ylm.Agent.Token.v1");

    public string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));
}
