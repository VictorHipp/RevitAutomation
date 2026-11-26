# 2. Installation

[← Index](../../README.md) · [Español](../es/02-instalacion.md)

## 2.1 Installing with `Instalar_RevitAutomation.exe`

The installer is **not** stored in the repository: generate it with `codigo\CREAR_INSTALADOR.cmd` (step by step in [Development § 4.3](04-development.md#43-creating-the-installer-step-by-step); it needs the .NET 10 SDK and the 12 fitting families). It is written next to the `codigo` folder.

1. Close Revit.
2. Double-click `Instalar_RevitAutomation.exe`.
3. The window lists Revit 2023–2027 and ticks the versions found on the PC (`C:\Program Files\Autodesk\Revit <year>\Revit.exe`).
4. Optionally choose your office's `.rfa` family folder. It is added to `%APPDATA%\CadMepRevit\bibliotecas.txt` and can be edited later.
5. Click **Instalar**.
6. Open Revit. The first time, Revit asks whether to load «RevitAutomation»: choose **Always Load** («Cargar siempre»).

Facts about the installation:

- It installs **for the current user only** and does not need administrator rights.
- Destination: `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitAutomation\` plus the manifest `RevitAutomation.addin`.
- It appears in Windows «Installed apps» so it can be uninstalled from there.
- Windows SmartScreen may warn because the `.exe` is not signed: «More info» → «Run anyway».

### What it removes first

Before installing, and for the current user only, it removes the **old RevitAutomation** (and its «CAD → Revit» module) and the **old CAD MEP** add-in, recognised by their manifest id or class. It does **not** touch other add-ins, the tool settings in `Documents\RevitAutomation`, or the CAD-REV learnt rules in `%APPDATA%\CadMepRevit`. If one of the old add-ins is installed for all users it only warns, because removing it needs administrator rights.

## 2.2 Silent installation (no window)

```powershell
Instalar_RevitAutomation.exe /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"C:\Office\Families"]
Instalar_RevitAutomation.exe /desinstalar /silencioso
```

- Exit code `0` means success.
- A log is written to `%LOCALAPPDATA%\RevitAutomation\instalacion.txt`.
- `/destino:"folder"` is for testing only: it installs into that folder instead of Revit and does not touch the registry.

## 2.3 Uninstalling

Any of these:

- Windows «Installed apps» → RevitAutomation → Uninstall.
- Run `Instalar_RevitAutomation.exe` and click **Desinstalar**.
- `codigo\desinstalar.cmd` (uses the copy of the installer saved during installation in `%LOCALAPPDATA%\RevitAutomation\Instalador\`).

Uninstalling never modifies your Revit projects or the tool settings.

## 2.4 Installing straight from the source code

`codigo\instalar.cmd` generates the installer (tests + build + package) and runs it silently. Close Revit first. It accepts an optional version list:

```powershell
.\instalar.cmd 2025,2027
```

## 2.5 Files the add-in uses on the PC

| Path | Content |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitAutomation\` | Installed add-in (DLLs, guide, examples, fallback `Familias`) |
| `%APPDATA%\CadMepRevit\bibliotecas.txt` | Family libraries searched by CAD-REV, one folder per line (network libraries are read-only) |
| `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` | Layers and blocks learnt through «¿Qué es esto?»; delete it to start over |
| `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt` | Last CAD-REV report (dated copies in `informes\`) |
| `%LOCALAPPDATA%\CadMepRevit\indice_familias.txt` | Cached index of the family libraries |
| `%LOCALAPPDATA%\RevitAutomation\instalacion.txt` | Log of the last silent install/uninstall |
| `Documents\RevitAutomation\<tool>\` | Settings, inputs and reports (`output\`) of the copied tools |

## 2.6 Troubleshooting

| Problem | Fix |
|---|---|
| The «RevitAutomation» tab does not appear | Close Revit, run the installer again, open Revit and choose «Always Load». |
| «No se encuentra Revit…» | Revit is not in `C:\Program Files\Autodesk`. Tick the version anyway or use `/versiones:`. |
| Fittings are not created | The pipe/duct type has no fittings in its Routing Preferences. The add-in tries to assign them from the project, the libraries and the fallback `Familias` folder; the report says which one is missing. Use a project created from the «Sistemas» (Systems) template. |
