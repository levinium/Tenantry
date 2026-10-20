# Tenantry

A desktop app for administering Microsoft 365 tenants on Windows, macOS and Linux. It covers the
work normally spread across the Microsoft 365 admin center, Entra, Exchange, SharePoint, Purview
and Intune portals, through their APIs, with fast local search and list and record pages for
every kind of object.

**Status: early development.** The app currently runs against a generated demo tenant only.

## Building

Requires the .NET 10 SDK.

```sh
dotnet test Tenantry.slnx
dotnet run --project src/Tenantry.App
```

## Contributing: the leak guard

Tenantry is developed against real tenants, and nothing from a real tenant may enter this
repository. Git hooks in `tools/hooks` refuse any commit or push that contains a term from a
private denylist kept **outside** the repository (the terms themselves are sensitive).

One-time setup after cloning:

```sh
git config core.hooksPath tools/hooks
```

Then create the denylist, one case-insensitive term per line (organisation names, domains, tenant
IDs). An empty file is allowed; a missing one blocks commits.

| OS | Location |
|---|---|
| Windows | `%APPDATA%\tenantry-dev\denylist.txt` |
| macOS, Linux | `~/.config/tenantry-dev/denylist.txt` |

Set `TENANTRY_DENYLIST` to use a different path.

Other rules:

- Test fixtures and screenshots use the demo tenant or reserved `.example` domains only.
- Tenant settings, caches and logs live in the OS app-data folder, never in the repo.

## License

MIT
