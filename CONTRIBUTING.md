# Contributing

Thank you for helping with Yubikey Lifecycle Manager! Bug reports, ideas, documentation and code are all welcome. English and German are both fine everywhere.

## Before you start

- **Security problems:** never as a public issue, see [SECURITY.md](SECURITY.md).
- **Bugs and ideas:** open an [issue](https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager/issues/new/choose). For larger changes, please open an issue first and describe the plan, so we can agree on the approach before you invest time.
- Be kind: see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

You need:

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [Node.js 22](https://nodejs.org/)
- PostgreSQL 16, for example in Docker:
  `docker run -d --name ylm-pg -e POSTGRES_USER=ylm -e POSTGRES_PASSWORD=ylm -e POSTGRES_DB=ylm -p 5432:5432 postgres:16`
- Windows only for the agent, tray app and installer. Server, web UI and all tests run on Linux, macOS and Windows.

Run the server and the web UI in two terminals:

```bash
# Terminal 1: backend on http://localhost:5080 (Development environment, fake login instead of Entra)
dotnet run --project src/Ylm.Server --launch-profile http

# Terminal 2: web UI with hot reload on http://localhost:5173 (proxies /api and /auth to the backend)
cd web && npm ci && npm run dev
```

In the Development environment the login page offers a *Entwicklungs-Anmeldung* (development login) without Entra. The first user becomes Owner.

To click through realistic data without Entra, start the server in **demo mode** instead (made-up tenants, see [docs/DEMO.md](docs/DEMO.md)). Use a separate, empty database for it:

```bash
ConnectionStrings__Postgres="Host=localhost;Database=ylm_demo;Username=ylm;Password=ylm" \
Ylm__DemoMode=true Ylm__DataDirectory=./data-demo \
dotnet run --project src/Ylm.Server --launch-profile http
```

A simulated agent (no YubiKey needed) runs with `dotnet run --project src/Ylm.Agent -- --simulate` on Windows.

## Tests

```bash
cd web && npm run build && cd ..          # type check and build of the web UI
dotnet build YubikeyLifecycleManager.slnx -c Release
YLM_TEST_POSTGRES="Host=localhost;Username=ylm;Password=ylm" \
dotnet test YubikeyLifecycleManager.slnx -c Release --no-build
```

The server tests create and drop their own databases on that PostgreSQL instance. They use a simulated YubiKey and a simulated Microsoft Graph, so no tenant is needed. Please add tests for new behavior, especially for anything touching permissions, tenants or the audit log.

## Pull requests

- Branch from `main`, keep a pull request focused on one topic.
- CI (build, tests, Docker and install script checks) must be green.
- Describe what changes for users and how you tested it. Screenshots help for UI changes.
- User-visible changes get a line in [`web/src/changelog.md`](web/src/changelog.md) under the next version (the app shows it as version history; German, everyday language). If you are unsure, the maintainer adds it.
- Do not commit secrets, tenant IDs, real names or other personal data, also not in screenshots or test data.
- By submitting a pull request you agree that your contribution is licensed under the project's license, [AGPL-3.0-only](LICENSE).

## Code style

- Follow the style of the surrounding code; nullable reference types are on for all projects (`Directory.Build.props`).
- User-facing texts are currently German. Code identifiers are English; comments may be German or English.
- Business logic without I/O belongs in `src/Ylm.Core`, everything that talks to the database or Graph in `src/Ylm.Server`.
- Access to Microsoft Graph goes through `IGraphClient`, so tests and the demo can replace it.

## Releases

Releases are tagged by the maintainer. The release workflow builds the Docker images and the agent MSI, which is signed via the SignPath Foundation (see [docs/SIGNIERUNG.md](docs/SIGNIERUNG.md)).
