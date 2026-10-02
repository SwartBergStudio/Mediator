# Security Policy

## Supported versions

Security fixes are released for the latest minor version of the current major version.

| Version | Supported |
|---|---|
| 3.x (latest minor) | Yes |
| < 3.0 | No; please upgrade |

## Reporting a vulnerability

**Please don't report security vulnerabilities through public issues, discussions or pull requests.**

Report them privately with GitHub's **[Report a vulnerability](https://github.com/SwartBergStudio/Mediator/security/advisories/new)** form (repository **Security** tab → **Advisories**).

Please include:

- the affected package and version;
- a description of the issue and its impact;
- steps or code to reproduce it.

You can expect an acknowledgement within a few days. A fix, and a published advisory with credit if you want it, follow once the issue is confirmed.

## Scope notes

- **Notification persistence** stores notifications as JSON. `FileNotificationPersistence` writes them to a local directory (by default `mediator-notifications` in the working directory). Protect that directory like any other application data, and don't persist secrets in notifications.
- **Deserialization** of persisted notifications resolves types by name. Only point persistence at storage that your application controls.
