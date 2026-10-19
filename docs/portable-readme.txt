ABC Converter - portable Windows x64 build

Requirements: Windows 10 22H2 or Windows 11, x64.
Extract the entire ZIP to a writable folder, then run ABC.Converter.App.exe.
Keep the extracted files together. No .NET installation or SDK is needed.

Add CBZ files or folders, choose an output folder, then select Start.
Source comics remain read-only. JPEG, PNG, and WebP pages are supported.

Queue and settings are stored outside this folder at:
%LOCALAPPDATA%\ABC Converter\abc-converter.db
ComicVine keys are stored in Windows Credential Manager.
Close the app before replacing its folder with a newer extracted build.
Keep a backup of the database before upgrading. Deleting the extracted folder
will not remove the queue, settings, credentials, or converted EPUBs.

This is an unsigned preview build. Manual desktop testing remains pending.
The ZIP includes the project's MIT LICENSE and third-party package notices
under ThirdPartyLicenses. Dependencies retain their own licenses.
ThirdPartyLicenses/packages.txt lists the restored package identities.
Before sharing a build, verify its ZIP and matching SHA-256 sidecar together.

Source and issue tracker: https://github.com/JonesPride/ABC-Converter

