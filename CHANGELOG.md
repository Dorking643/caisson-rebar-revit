# Changelog

## R36.Share1 / project version 36.1.0 — prepared 2026-10-29

This is the preparation date of the local distribution, not a claim that a public GitHub release already exists.

### Software distribution

- Organized the R36 source as the portable R36.Share1 distribution.
- Replaced development-machine runtime paths with per-user local application data paths.
- Made the local Revit API directory a build parameter.
- Removed the original project-specific slab default parameter resource and automatic loading of that layout.
- Added per-user install/uninstall scripts with payload checksums and recoverable previous add-in manifests.

### Example models

- Included separate wall, slab and combined concrete shells for Revit 2025.
- Retained host dimensions, openings and relative placement.
- Removed reinforcement and previous design storage from all supplied models.
- Checked saved/reopened files, host geometry, metadata and used-family strings.

### GitHub repository preparation

- Reorganized the distribution into `src`, `tests`, `scripts`, `installer`, `models`, `docs` and `verification`.
- Added English/Chinese repository introductions, a quick-start guide, build instructions, contribution templates and publication notes.
- Set the confirmed CaissonRebar software title and ordered list of seven authors.
- Added the Apache-2.0 license, collective copyright notice, authors file and active CITATION.cff for version 36.1.0.
- Applied the same license to project-owned source, compiled programs, shell models, images and documentation.
- Added publication metadata and citation guidance without inventing a repository URL, DOI or public release date.
- Kept the shipped DLL and RVT files unchanged from the verified R36.Share1 distribution.

Only the recorded changes above are summarized here. A reconstructed history of earlier development versions is not included.
