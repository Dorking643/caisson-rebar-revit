# Verification records

These records describe local checks performed before the initial GitHub upload. Their upload/workflow status fields record that check stage, not the live repository status. The publication-stage metadata check is in `publication-staging-checks.json`.

- `software/` contains the original R36.Share1 build and offline-check records. The add-in DLL and C# implementation are unchanged in this repository layout.
- `models/` contains the save/reopen, host-geometry, zero-rebar and anonymization records for the three shell RVT files. Their bytes are unchanged.
- `repository-layout.json` records checks repeated after restructuring the repository. It does not represent a GitHub Actions run.
- `publication-metadata-checks.json` records author order, citation schema and Apache-2.0 text checks for the metadata update.
- `repository-final-checks.json` records the final content/link scan and byte comparisons after that update.

The previous delivery archive's manifest and package-scan result do not describe this new directory layout and are not reused here. A new root manifest is generated for the final repository archive.

To verify the prepared release files from the repository root, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1`. After intentional edits, the release maintainer must regenerate the manifest for the new release.

## Reproduce offline checks

Run from the repository root on Windows:

```powershell
dotnet run --project .\tests\Share1Checks\Share1Checks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Installer.ps1
```

The CI definition uses the same commands. The add-in itself requires Revit 2025 to compile and run; these offline checks have no Autodesk assembly dependency.
