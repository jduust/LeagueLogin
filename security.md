# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in League Login, please **open a [GitHub issue](https://github.com/jduust/LeagueLogin/issues)** describing the problem.

Because this application stores Riot account credentials, security reports are taken seriously and will be prioritised.

Please include in your report:
- A description of the vulnerability
- Steps to reproduce it
- The version of League Login you are using

## Credential Storage

Credentials are stored in the **Windows Credential Manager** using `CRED_TYPE_GENERIC` and `CRED_PERSIST_LOCAL_MACHINE`. They are encrypted by Windows via DPAPI and are never written to disk in plain text by this application. You can inspect, edit, or delete them at any time via **Control Panel → Credential Manager → Windows Credentials**.

## Supported Versions

Only the latest release is actively maintained.