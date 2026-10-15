# Security policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions or pull requests.**

Report them privately through GitHub's private vulnerability reporting:

1. Go to the repository's [**Security** tab](https://github.com/tolgaki/federated-connector/security).
2. Select **Report a vulnerability** and fill in the advisory form.

Please include as much of the following as you can:

- the type of issue (for example authentication bypass, permission leak between users, handle forgery, injection)
- the affected files, version or commit
- steps to reproduce, and a proof of concept if you have one
- the impact, and how an attacker might exploit it

You should receive an acknowledgement within a few working days. We'll keep you informed as we investigate and fix
the issue, and credit you in the advisory unless you prefer otherwise.

## Scope

This project is a blueprint that deployers run in their own tenants. Issues in the code here (the host, providers,
conformance kit, scripts and templates) are in scope. Issues in a specific deployment, or in the Microsoft 365, Azure or
data-source services it calls, should be reported to the operator or service owner.

Areas we care most about:

- a user obtaining results or documents they are not permitted to see
- calls reaching a source without the user's identity
- forging, replaying or transferring result handles between users
- leaking secrets, tokens or source response bodies through errors, logs or telemetry

## Supported versions

Only the latest commit on `main` is supported while the project is pre-1.0.
