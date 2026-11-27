# Security policy

Yubikey Lifecycle Manager manages security keys and talks to Microsoft Entra with far-reaching application permissions. We take security reports seriously and are grateful for them.

## Reporting a vulnerability

**Please do not open a public issue, discussion or pull request for security problems.**

Report them privately via GitHub: [Report a vulnerability](https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager/security/advisories/new) (tab *Security* → *Report a vulnerability*). Only the maintainers can see the report.

Please include, as far as you can:

- the affected component (server, web UI, agent, tray app, installer scripts, Docker images) and version
- steps to reproduce or a proof of concept
- the impact you expect (for example: who could do what without being allowed to)

English or German is fine.

## What happens next

- We confirm receipt within **5 working days**.
- We assess the report, keep you informed and agree on a timeline with you. Our goal is a fix within 90 days, much sooner for critical issues.
- We publish a fixed release and a GitHub security advisory. With your consent we credit you in the advisory.

This is a volunteer project without a bug bounty.

## Supported versions

Only the **latest release** receives security fixes. The platform shows available updates under *Globale Einstellungen → Version und Updates* and can update itself with one click; the agent updates itself.

## Scope

In scope: the code in this repository and the release artifacts built from it (Docker images on `ghcr.io/panpakapancake`, agent MSI and executables on the releases page, the install scripts).

Out of scope: vulnerabilities in Microsoft Entra, Microsoft Graph, YubiKey firmware or third-party dependencies themselves (please report those to the respective vendor; we are still glad to hear if our project is affected), installations that were exposed to the internet against the documentation, and the [demo](docs/DEMO.md), which deliberately has no authentication.

Known YubiKey firmware advisories from Yubico are shown in the app under *Sicherheitshinweise*.
