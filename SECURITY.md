# Security Policy

## Supported Versions

ABC Converter is under development. Security fixes target the latest code on
`main`. Older commits and development branches do not receive separate security
backports. Update to the latest `main` before checking whether a problem persists.

## Reporting a Vulnerability

Use GitHub's private vulnerability reporting option on the repository's
[Security page](https://github.com/JonesPride/ABC-Converter/security) when available.
If that option is unavailable, open an issue asking the maintainer for a private
reporting channel. Do not include exploit details or affected private files in
that public issue.

A private report should include the affected commit, reproduction steps, expected
and actual behavior, and potential impact. Use a minimal synthetic CBZ or response
fixture where possible. Never include API keys, credentials, or copyrighted comics.

Reports are reviewed on a best-effort basis; no response-time commitment is made.
Coordinate public disclosure with the maintainer after a fix is available.

## Security Checks

Windows CI restores packages with warnings treated as errors, audits direct and
transitive NuGet dependencies, and builds and tests the solution. Dependabot checks
NuGet packages and GitHub Actions weekly. Do not suppress vulnerability warnings
to make a build pass; update the affected dependency and rerun the checks.
