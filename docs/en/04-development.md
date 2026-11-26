# 4. Development

[← Index](../../README.md) · [Español](../es/04-desarrollo.md)

All commands are run in **PowerShell** from the `codigo` folder unless stated otherwise.

## 4.1 Tools to download

**The easiest way, no console:** double-click **`codigo\PREPARAR_PC.cmd`**. It checks what is missing from the table below (.NET 10 and 8 SDKs, Git for Windows and GitHub CLI), skips what is already installed and installs the rest with no further clicks: Windows asks for administrator permission once (click «Yes»). It uses `winget` when the PC has it; otherwise it downloads the official installer from its direct link, checks that it is signed by its publisher and runs it. At the end it enables Git long paths and shows «TODO LISTO» (all done) or what failed and how to fix it.

If you prefer to install them by hand, these are the tools:

| Tool | Needed for | Required? | Official download | Or with winget |
|---|---|---|---|---|
| **.NET 10 SDK** (the *SDK*, not only the runtime) | Building the add-in for Revit 2027 (`net10.0-windows`) and creating the installer. It also builds the .NET Framework 4.8 versions (2023/2024): their reference assemblies come from NuGet | **Yes** | <https://dotnet.microsoft.com/download/dotnet/10.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` |
| **.NET 8 SDK** | The versions before 2027: Revit 2025 and 2026 (`net8.0-windows`) and the engine tests (`tests/CadMepRevit.Tests`, .NET 8). It is installed alongside the .NET 10 SDK, not instead of it | **Yes** | <https://dotnet.microsoft.com/download/dotnet/8.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.8 -e --source winget` |
| **Git for Windows** | Downloading (`git clone`) and uploading (`git push`) the repository | **Yes** (to work with GitHub) | <https://git-scm.com/download/win> | `winget install --id Git.Git -e --source winget` |
| GitHub CLI | Signing in to GitHub from the terminal (`gh auth login`) | Optional | <https://cli.github.com> | `winget install --id GitHub.cli -e --source winget` |
| Visual Studio 2022 or later (Community is free) with the *.NET desktop development* workload, or VS Code / Rider | Editing and debugging the code. Open `codigo\CadMepRevit.sln` | Optional | <https://visualstudio.microsoft.com/downloads/> | `winget install --id Microsoft.VisualStudio.2022.Community -e --source winget` |
| Autodesk Revit 2023–2027 | Testing inside Revit. **Not needed to build**: without it the Revit API is taken from NuGet | Optional | Your Autodesk Account | — |

Notes:

- Both .NET SDKs must be in the same folder (normally `C:\Program Files\dotnet`). The official installers, `winget` and `PREPARAR_PC.cmd` all put them there.
- `winget` comes with Windows 10/11 («App Installer»). Run the commands in a normal PowerShell window; some installers show a Windows permission prompt.
- Close and reopen PowerShell after installing so the new commands are found.
- Check the installation:

  ```powershell
  dotnet --list-sdks     # must list a 10.x and an 8.x version
  git --version
  ```

- The first build needs **Internet** (NuGet downloads ACadSharp, the Revit API packages, etc.). In an offline shell set `$env:NuGetAudit = 'false'`.

## 4.2 Getting the 12 fitting families

The installer needs the fallback fitting families in `codigo\familias_uniones\`. They are **Autodesk content and are not in the repository**, so you must put them there yourself. The list of the 12 files is in [`codigo/familias_uniones/README.md`](../../codigo/familias_uniones/README.md).

Choose one of these options (in order of preference):

**A. From your own copy (fastest).** Keep a private copy of the `familias_uniones` folder (USB drive, private cloud storage) and copy the 12 `.rfa` files back into `codigo\familias_uniones\` after cloning.

**B. Download the Revit content library from your Autodesk Account.** You need a Revit licence.

1. Go to <https://manage.autodesk.com> and sign in.
2. **Products and Services** (left menu, or your avatar at the top right) → **Revit** → choose the **year** → **Libraries**.
3. Download the content pack for **Spain** (Spanish / *Español*; the exact name of the pack may vary) and run the downloaded `.exe`.
4. The content is installed in `C:\ProgramData\Autodesk\RVT <year>\Libraries\` (the `ProgramData` folder is hidden: type the path in the Explorer address bar).
5. Find the files by name and copy them into `codigo\familias_uniones\`:

   ```powershell
   $lib  = 'C:\ProgramData\Autodesk\RVT 2027\Libraries'          # change the year
   $dest = '.\familias_uniones'
   'M_Codo - Genérico','M_Te - Genérica','M_Cruz - Genérica','M_Transición - Genérica',
   'M_Codo rectangular - Radio 1','M_Te rectangular - Recto','M_Cruz rectangular -  Recta','M_Transición rectangular - Ángulo 2',
   'M_Codo redondo 1','M_Te redonda - Recto','M_Cruz redonda - Recto','M_Transición redonda - Ángulo' |
     ForEach-Object { Get-ChildItem $lib -Recurse -Filter "$_.rfa" | Select-Object -First 1 | Copy-Item -Destination $dest -Verbose }
   ```

**C. From inside Revit, one by one (Revit 2021.1 or later).** Open any project → **Insert** tab → **Load Autodesk Family** → search for the family name (e.g. `M_Codo - Genérico`) → **Load**. Then, in the **Project Browser** → *Families*, right-click the family → **Save…** and save it as `.rfa` into `codigo\familias_uniones\` with the exact file name.

**D. Use equivalent families.** Any elbow, tee, cross and transition families for pipe, rectangular duct and round duct also work. CAD-REV recognises fitting families by the meaning of their name (`Core/FittingNames.cs`) and checks their category and part type when loading them.

`CREAR_INSTALADOR.cmd` stops with the message «No se encuentran las familias de uniones» if the folder has no `.rfa` file.

## 4.3 Creating the installer, step by step

1. Install the **.NET 10 and .NET 8 SDKs**: double-click `codigo\PREPARAR_PC.cmd` ([4.1](#41-tools-to-download)). If you forget, `CREAR_INSTALADOR.cmd` detects it and asks whether to install them.
2. Put the **12 families** in `codigo\familias_uniones\` ([4.2](#42-getting-the-12-fitting-families)).
3. Open the `codigo` folder in Explorer and **double-click `CREAR_INSTALADOR.cmd`**. Or, from PowerShell:

   ```powershell
   cd <repository folder>\codigo
   .\CREAR_INSTALADOR.cmd
   ```

4. Wait. It runs all the tests, then builds the five Revit versions. The first time it takes several minutes because it downloads the NuGet packages.
5. When it finishes it shows `INSTALADOR CREADO correctamente`, the path, the size and the SHA-256 of the file. The installer is written to the **repository root**, next to the `codigo` folder:

   ```
   RevitAutomation\Instalar_RevitAutomation.exe
   ```

6. To install it, close Revit and double-click the `.exe` (see [Installation](02-installation.md)). Copy it to any other PC with Revit to install it there; that PC does not need .NET or Git.

Options:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -SinPruebas          # skip the tests
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Salida C:\Temp\out  # write the .exe elsewhere
```

`CREAR_INSTALADOR.cmd` does not install the add-in on the PC (only the .NET SDKs, if they are missing and you answer «S»). `instalar.cmd` does the same and then installs the add-in silently for the current user (close Revit first).

### What the script does

1. Finds a .NET folder with the SDK 10 (or later) and the SDK 8 (`DOTNET_ROOT`, `%USERPROFILE%\.dotnet`, `C:\Program Files\dotnet` or `PATH`). If one is missing it asks «¿Instalarlo ahora? (S/N)» (install it now?) and, on «S», runs `preparar_pc.ps1 -SoloDotnet`.
2. Runs the engine tests (`tests/CadMepRevit.Tests`) and every tool harness in `tests/RevitAutomation.Pruebas`.
3. Builds `RevitAutomation.csproj` for 2023, 2024 (`net48`), 2025, 2026 (`net8.0-windows`) and 2027 (`net10.0-windows`) **without debug symbols**, each in its own `obj\paquete\<year>` folder, and removes `RevitAPI*.dll` and `.pdb` files.
4. Adds the user guide, the sample drawings and the fallback `Familias`.
5. Checks that no file in the package contains the PC's user name, profile path or computer name.
6. Zips the package, embeds it in `instalador/InstaladorCadMep.csproj` (.NET Framework 4.8 WinForms) and writes `Instalar_RevitAutomation.exe`.

### If something fails

| Message | Cause and fix |
|---|---|
| «Falta el SDK de .NET 10 / 8» or «No se encuentra el SDK de .NET …» | One of the two SDKs is missing, only the runtime is installed, or they are in different folders. Answer «S» to the question or double-click `PREPARAR_PC.cmd` ([4.1](#41-tools-to-download)). |
| `PREPARAR_PC.cmd`: «No se ha podido abrir la instalación como administrador» | You clicked «No» in the Windows prompt, or your user cannot install programs. Run it again and click «Yes», or ask IT to install what it lists. |
| `PREPARAR_PC.cmd`: «La firma digital del archivo descargado no es válida…» | The downloaded file is not the original (interrupted download or a proxy altering it). It is not run. Try again or download it from the official link shown. |
| «Las pruebas automáticas han fallado» | A test failed; read the lines above. To build anyway: `crear_instalador.ps1 -SinPruebas`. |
| «La compilación para Revit <year> ha fallado» | Usually no Internet for NuGet, or a code error. Check the connection and the compiler messages. |
| «No se encuentran las familias de uniones» | `codigo\familias_uniones\` has no `.rfa` ([4.2](#42-getting-the-12-fitting-families)). |
| «… contiene '…' (datos de este equipo)» | A file of the package contains your user/PC name. Build without debug symbols (the script does it by default). |
| SmartScreen blocks the `.exe` | It is not signed: «More info» → «Run anyway». |

## 4.4 Setting up a new PC

Yes: you can download the whole project from GitHub and keep working. Everything is in the repository **except** the items in [4.5](#45-what-is-not-in-the-repository).

**No console:** if you do not have the repository or Git yet, on the GitHub page click **Code › Download ZIP**, unzip it and double-click `codigo\PREPARAR_PC.cmd`. It installs the .NET 10 and 8 SDKs, Git and GitHub CLI, and enables Git long paths. Then clone the repository (step 3) and delete the ZIP folder.

**With the console:**

```powershell
# 1. Tools (see 4.1). Or, in the codigo folder, .\PREPARAR_PC.cmd
winget install --id Git.Git -e --source winget
winget install --id Microsoft.DotNet.SDK.10 -e --source winget   # Revit 2027, the installer and 2023/2024
winget install --id Microsoft.DotNet.SDK.8 -e --source winget    # Revit 2025/2026 (versions before 2027) and tests
winget install --id GitHub.cli -e --source winget      # optional
git config --global core.longpaths true                # avoids problems with long Windows paths

# 2. Identity for your commits. Use your GitHub "noreply" address to keep your e-mail private:
#    GitHub → Settings → Emails → "Keep my email addresses private" shows it.
git config --global user.name  "Victor Hipp Murcia"
git config --global user.email "<id>+VictorHipp@users.noreply.github.com"

# 3. Download the repository
cd $HOME\Desktop
git clone https://github.com/VictorHipp/RevitAutomation.git
cd RevitAutomation\codigo

# 4. Put the 12 fitting families back (see 4.2)

# 5. Check that everything works and create the installer (see 4.3)
.\CREAR_INSTALADOR.cmd
```

To push changes from the new PC, sign in once with `gh auth login` (or Git will open the browser the first time you run `git push`).

Daily routine on any PC:

```powershell
git pull                      # before you start: fetch the latest changes
# ... work ...
git add -A
git commit -m "Describe the change"
git push                      # when you finish: upload your changes
```

> Without Git: on the GitHub page, **Code › Download ZIP** downloads the current files, but without history and without the ability to push changes back.

`CLAUDE.md` (root) and `codigo/CLAUDE.md` are in the repository, so Claude Code gets the project rules and architecture on any PC.

## 4.5 What is not in the repository

| Item | Why | How to get it back |
|---|---|---|
| `codigo/familias_uniones/*.rfa` (12 fitting families) | Autodesk library content, not covered by this project's licence | [4.2](#42-getting-the-12-fitting-families) |
| `Instalar_RevitAutomation.exe` | Generated file | [4.3](#43-creating-the-installer-step-by-step) |
| `bin/`, `obj/` | Build output | Built automatically |
| `codigo/ejemplos/ejemplo_mep.dwg` | Generated by the tests and different on every run; the DXF version is kept | Not needed |
| Real client drawings, `Pruebas/`, `FIRMAS_*.json` | Confidential | Never stored; see [Testing](06-testing.md#64-real-drawings-confidential) |

## 4.6 Building by hand

Build one Revit version:

```powershell
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2023
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2024
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2025
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2026
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net10.0-windows -p:RevitYear=2027
```

When building several years one after another, give each its own intermediate folder, e.g. `-p:IntermediateOutputPath=obj\y2026\`.

Build the solution (2025 + 2027 when Revit 2027 is installed or `BuildForRevit2027=true`):

```powershell
dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true
```

### How the Revit version is chosen (`src/Revit.props`)

- `RevitYear` selects the target framework: 2023/2024 → `net48`, 2025/2026 → `net8.0-windows`, 2027 → `net10.0-windows`. Without `RevitYear`, `net48` → 2024, `net8.0-windows` → 2025, `net10.0-windows` → 2027.
- It references `C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` / `RevitAPIUI.dll` if present; otherwise the NuGet packages `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` (`2023.4.0`, `2024.3.0`, `2025.4.60`, `2026.4.10`, `2027.4.0`), compile-time only.
- A `ValidateRevitApiVersion` target fails the build if the referenced API major version does not match the year.
- It defines the constant `REVIT<year>` (e.g. `REVIT2026`) for conditional code.

### Shared settings (`Directory.Build.props`)

C# `latest`, nullable reference types, implicit usings, version and copyright metadata. SourceLink and source-control queries are disabled (they failed on a network drive). For `net48` it adds PolySharp, `System.Text.Json` 8.0.5 and a global `using CadMepRevit.Compat`.

## 4.7 Coding rules

These rules come from [`codigo/CLAUDE.md`](../../codigo/CLAUDE.md) and apply to every change:

- Recognition and geometry rules must work for drawings from **any office**: never tailor them to a test file name, exact layer, coordinates or a single drawing.
- Only use Revit API types, methods and parameters that **compile against every supported version**. If a call does not compile, treat it as unavailable. Revit 2023 has no `ElementId.Value` or `new ElementId(long)`: use `RevitIds.FromValue(long)` (`src/CadMepRevit/Compat/RevitApiCompat.cs`).
- Write against .NET 8. If a `net48` build fails on a missing BCL member, add it to `src/CadMepRevit.Core/Compat/NetFrameworkCompat.cs` instead of changing shared code.
- Take sizes from the drawing or another identifiable source; mark fallbacks `SUPUESTO` in the report.
- Keep all model edits of one ribbon click inside one assimilated `TransactionGroup` (one Ctrl+Z).
- Ribbon labels, dialogs, reports, user guidance and **source comments are written in Spanish**. Error messages say what failed **and how to fix it**.
- Keep **ACadSharp at 3.8.35** (other add-ins load the same DLL inside Revit; a different version can clash).
- WinForms and Revit share type names (`Form`, `Control`, `TaskDialog`…): use aliases such as `using Form = System.Windows.Forms.Form;`.
- Report build results, offline tests and verification inside Revit as **separate** evidence.
- Keep specifications in `codigo/docs/ESPEC_*.md`.
- Before accepting a change to the engine, compare plans before/after (see [Testing](06-testing.md#63-plan-comparison-before-accepting-a-change)).
