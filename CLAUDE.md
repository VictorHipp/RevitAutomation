# CLAUDE.md (repository root)

The project lives in `codigo/`. Its rules, build commands, tests and architecture are in `codigo/CLAUDE.md`:

@codigo/CLAUDE.md

## Repository notes

- This is a **public** GitHub repository under the MIT licence (© 2026 Victor Hipp Murcia). Never commit client drawings, reports, project or people's names, `Pruebas/` or `FIRMAS_*.json`.
- `codigo/familias_uniones/*.rfa` are Autodesk library families and are ignored by Git. On a fresh clone, `codigo/crear_instalador.ps1` looks for the missing ones (`-Familias <folder>`, `REVITAUTOMATION_FAMILIAS`, then `C:\ProgramData\Autodesk\RVT <year>\Libraries`), copies what it finds into `familias_uniones`, and if any are still missing warns and builds the installer without them (see `codigo/familias_uniones/README.md`). Its check for this PC's data in the package skips generic account names such as «Administrador», which appear in the installer's own texts.
- `Instalar_RevitAutomation.exe` is generated at the repository root and ignored by Git. Do not publish it while it embeds Autodesk families.
- User documentation lives in `docs/en/` and `docs/es/`. When behaviour changes, update both languages together; specifications stay in `codigo/docs/ESPEC_*.md` (Spanish).
- Committing a clean state before editing code gives a restorable point in Git history, in addition to the backup step described in `codigo/CLAUDE.md`.
