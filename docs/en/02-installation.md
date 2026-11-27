# 2. Download and installation

[← Index](../../README.md) · [Español](../es/02-instalacion.md)

The installer `Instalar_RevitAutomation.exe` **is not published**, because it contains families from the Autodesk library. Each PC creates it from the source code. That is why there are two parts:

- **Part A. Pre-installation**: done **once per PC**. It downloads the project, installs what is needed and creates the installer.
- **Part B. Installation**: installing the add-in in Revit, updating it and uninstalling it.

Summary. You only type **one command**, in step 2; everything else is a double-click:

| Step | What to do | How |
|---|---|---|
| [2.1](#21-installing-git) | Install Git | Download it from the website and run it |
| [2.2](#22-downloading-the-repository) | Download the project | `git clone https://github.com/VictorHipp/RevitAutomation.git` |
| [2.3](#23-setting-up-the-pc-net-10-and-8-sdks) | Install the .NET 10 and 8 SDKs (and .NET Framework 4.8 if missing) | Double-click `codigo\PREPARAR_PC.cmd` |
| [2.4](#24-getting-the-12-fitting-families) | Add the 12 fitting families (recommended) | Copy them into `codigo\familias_uniones\`, or let `CREAR_INSTALADOR.cmd` look for them |
| [2.5](#25-creating-the-installer) | Create the installer | Double-click `codigo\CREAR_INSTALADOR.cmd` |
| [2.6](#26-installing-in-revit) | Install in Revit | Double-click `Instalar_RevitAutomation.exe` |

## Part A. Pre-installation (once per PC)

### 2.1 Installing Git

Git downloads the project and, later, its updates. If you already have it, skip this step.

1. Go to <https://git-scm.com/download/win>. The download starts by itself (otherwise click *Click here to download*).
2. Run the downloaded file, click **Next** on every screen and then **Install**. The default options are fine.

### 2.2 Downloading the repository

1. In File Explorer, open the folder where you want to keep the project. Prefer a local folder that is **not** synced with OneDrive, because building creates many files.
2. Click the address bar, type `powershell` and press **Enter**. A blue window opens in that folder.
3. Type (or paste) this command and press **Enter**:

   ```powershell
   git clone https://github.com/VictorHipp/RevitAutomation.git
   ```

4. When it finishes, close the window. The `RevitAutomation` folder has been created; the code is in `RevitAutomation\codigo`.

### 2.3 Setting up the PC (.NET 10 and 8 SDKs)

Open the `RevitAutomation\codigo` folder and **double-click `PREPARAR_PC.cmd`**.

1. It shows what is installed (`[OK]`) and what is missing (`[FALTA]`): .NET 10 SDK, .NET 8 SDK, .NET Framework 4.8 (runtime), .NET Framework 4.8 Developer Pack and Git.
2. If GitHub CLI is missing, it asks: «¿Va a desarrollar el complemento o subir cambios a GitHub? (S/N)» (will you develop the add-in or push changes to GitHub?). To only install the add-in, answer **N**.
3. It installs what is missing with no further clicks. Windows asks for administrator permission **once**: click **«Yes»**. Progress is shown in another window.
4. At the end it shows **«TODO LISTO»** (all done), or what failed and how to fix it. Whatever is already installed is skipped, so you can run it as often as you like.

It uses `winget` when the PC has it. Otherwise it downloads the official installer from its direct link, checks that it is signed by its publisher and runs it. It also enables Git long paths (`core.longpaths`).

If you prefer to install them by hand, this is what is needed:

| Tool | Needed for | Required? | Official download | Or with winget |
|---|---|---|---|---|
| **.NET 10 SDK** (the *SDK*, not only the runtime) | Building the add-in for Revit 2027 (`net10.0-windows`) and creating the installer. It also builds the .NET Framework 4.8 versions (2023/2024): their reference assemblies come from NuGet | **Yes** | <https://dotnet.microsoft.com/download/dotnet/10.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` |
| **.NET 8 SDK** | The versions before 2027: Revit 2025 and 2026 (`net8.0-windows`) and the engine tests (.NET 8). It is installed alongside the .NET 10 SDK, not instead of it | **Yes** | <https://dotnet.microsoft.com/download/dotnet/8.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.8 -e --source winget` |
| **.NET Framework 4.8** (runtime) | Runs Revit 2023 and 2024 and the installer. It ships with Windows 10 (version 1903 or later) and Windows 11, so it is only installed if missing | **Yes** (normally already there) | <https://dotnet.microsoft.com/download/dotnet-framework/net48> → *Runtime* | — (winget only has 4.8.1, which does not support older Windows 10) |
| **.NET Framework 4.8 Developer Pack** | The 4.8 reference assemblies, to build 2023 and 2024 without relying on NuGet. Without it the build still works: the SDK downloads the same assemblies from NuGet. It must be **4.8** (4.8.1 does not work) | Recommended | <https://dotnet.microsoft.com/download/dotnet-framework/net48> → *Developer Pack* | `winget install --id Microsoft.DotNet.Framework.DeveloperPack_4 --version 4.8 -e --source winget` |
| **Git for Windows** | Downloading the project (`git clone`) and its updates (`git pull`) | **Yes** | <https://git-scm.com/download/win> | `winget install --id Git.Git -e --source winget` |
| GitHub CLI | Signing in to GitHub to push changes (`gh auth login`) | Only for development | <https://cli.github.com> | `winget install --id GitHub.cli -e --source winget` |

Notes:

- Both .NET SDKs must be in the same folder (normally `C:\Program Files\dotnet`). The official installers, `winget` and `PREPARAR_PC.cmd` all put them there.
- The first build needs **Internet**: NuGet downloads ACadSharp, the Revit API packages, etc.
- You do not need Revit to create the installer: without it the Revit API is taken from NuGet.

### 2.4 Getting the 12 fitting families

The installer carries whatever fallback fitting families are in `codigo\familias_uniones\`. They are **Autodesk content and are not in the repository**. Without them the installer is still created, but CAD-REV loses its last-resort fallback: in a project with no fitting families loaded, Revit does not create those fittings and the report says so. The list of the 12 files is in [`codigo/familias_uniones/README.md`](../../codigo/familias_uniones/README.md).

**Automatic search.** `CREAR_INSTALADOR.cmd` looks for any missing families and copies them to `codigo\familias_uniones\`. It searches, in this order:
1. the folder given with `-Familias <folder>`;
2. the `REVITAUTOMATION_FAMILIAS` environment variable;
3. the Revit libraries installed on the PC (`C:\ProgramData\Autodesk\RVT <year>\Libraries`, newest first).

If any are still missing, it **warns and creates the installer without them**. For an office, the easiest setup is to keep the 12 `.rfa` files once in a server folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Familias '<server folder>'
```

They cannot be downloaded automatically: Autodesk only provides them while you are signed in to your account, or from inside Revit.

If the search does not find them, choose one of these options (in order of preference):

**A. From your own copy (fastest).** Keep a private copy of the `familias_uniones` folder (USB drive, private cloud storage) and copy the 12 `.rfa` files into `codigo\familias_uniones\` after downloading the repository.

**B. Download the Revit content library from your Autodesk Account.** You need a Revit licence.

1. Go to <https://manage.autodesk.com> and sign in.
2. **Products and Services** (left menu, or your avatar at the top right) → **Revit** → choose the **year** → **Libraries**.
3. Download the content pack for **Spain** (Spanish / *Español*; the exact name of the pack may vary) and run the downloaded `.exe`.
4. The content is installed in `C:\ProgramData\Autodesk\RVT <year>\Libraries\` (the `ProgramData` folder is hidden: type the path in the Explorer address bar).
5. Find the 12 files by name (the Explorer search box works) and copy them into `codigo\familias_uniones\`. Or, in PowerShell from the `codigo` folder:

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

### 2.5 Creating the installer

1. In the `codigo` folder, **double-click `CREAR_INSTALADOR.cmd`**.
2. If one of the .NET SDKs is missing, it asks «¿Instalarlo ahora? (S/N)» (install it now?). Answer **S** and it carries on by itself.
3. Wait. It runs all the tests, then builds the five Revit versions. The first time it takes several minutes because it downloads the NuGet packages.
4. When it finishes it shows `INSTALADOR CREADO correctamente`, the path, the size and the SHA-256 of the file. The installer is written to the `RevitAutomation` folder, next to the `codigo` folder:

   ```
   RevitAutomation\Instalar_RevitAutomation.exe
   ```

You can copy that `.exe` to any other PC with Revit and install it there: that PC does not need .NET, Git or the pre-installation.

`CREAR_INSTALADOR.cmd` does not install the add-in (only the .NET SDKs, if they are missing and you answer «S»). Options, from PowerShell in the `codigo` folder:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -SinPruebas          # skip the tests
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Salida C:\Temp\out  # write the .exe elsewhere
```

#### If something fails

| Message | Cause and fix |
|---|---|
| `git` is not recognised as a command | Git is not installed, or the window was opened before installing it. Do step [2.1](#21-installing-git) and open a new window. |
| «Falta el SDK de .NET 10 / 8» or «No se encuentra el SDK de .NET …» | One of the two SDKs is missing, only the runtime is installed, or they are in different folders. Answer «S» to the question or double-click `PREPARAR_PC.cmd` ([2.3](#23-setting-up-the-pc-net-10-and-8-sdks)). |
| `PREPARAR_PC.cmd`: «No se ha podido abrir la instalación como administrador» | You clicked «No» in the Windows prompt, or your user cannot install programs. Run it again and click «Yes», or ask IT to install what it lists. |
| `PREPARAR_PC.cmd`: «La firma digital del archivo descargado no es válida…» | The downloaded file is not the original (interrupted download or a proxy altering it). It is not run. Try again or download it from the official link shown. |
| «Las pruebas automáticas han fallado» | A test failed; read the lines above. To build anyway: `crear_instalador.ps1 -SinPruebas`. |
| «La compilación para Revit <year> ha fallado» | Usually no Internet for NuGet, or a code error. Check the connection and the compiler messages. |
| «AVISO: Faltan N de las 12 familias de uniones» | They are not in `codigo\familias_uniones\` and the automatic search did not find them. The installer is still created, without them. To include them, add them ([2.4](#24-getting-the-12-fitting-families)) and create it again. |
| «… contiene '…' (datos de este equipo)» | A file of the package contains your user/PC name. Build without debug symbols (the script does it by default). |

## Part B. Installation

### 2.6 Installing in Revit

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

#### What it removes first

Before installing, and for the current user only, it removes the **old RevitAutomation** (and its «CAD → Revit» module) and the **old CAD MEP** add-in, recognised by their manifest id or class. It does **not** touch other add-ins, the tool settings in `Documents\RevitAutomation`, or the CAD-REV learnt rules in `%APPDATA%\CadMepRevit`. If one of the old add-ins is installed for all users it only warns, because removing it needs administrator rights.

### 2.7 Silent installation (no window)

```powershell
Instalar_RevitAutomation.exe /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"C:\Office\Families"]
Instalar_RevitAutomation.exe /desinstalar /silencioso
```

- Exit code `0` means success.
- A log is written to `%LOCALAPPDATA%\RevitAutomation\instalacion.txt`.
- `/destino:"folder"` is for testing only: it installs into that folder instead of Revit and does not touch the registry.

### 2.8 Updating and uninstalling

**Updating to a new version:** fetch the changes with `git pull` in the `RevitAutomation` folder (open PowerShell there as in [2.2](#22-downloading-the-repository)). Then double-click `CREAR_INSTALADOR.cmd` and the new `Instalar_RevitAutomation.exe`. The new installation replaces the previous one; you do not need to uninstall first.

**Uninstalling**, with any of these:

- Windows «Installed apps» → RevitAutomation → Uninstall.
- Run `Instalar_RevitAutomation.exe` and click **Desinstalar**.
- `codigo\desinstalar.cmd` (uses the copy of the installer saved during installation in `%LOCALAPPDATA%\RevitAutomation\Instalador\`).

Uninstalling never modifies your Revit projects or the tool settings.

### 2.9 Installing straight from the source code

`codigo\instalar.cmd` generates the installer (tests + build + package) and runs it silently. Close Revit first. It accepts an optional version list:

```powershell
.\instalar.cmd 2025,2027
```

### 2.10 Files the add-in uses on the PC

| Path | Content |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitAutomation\` | Installed add-in (DLLs, guide, examples, fallback `Familias`) |
| `%APPDATA%\CadMepRevit\bibliotecas.txt` | Family libraries searched by CAD-REV, one folder per line (network libraries are read-only) |
| `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` | Layers and blocks learnt through «¿Qué es esto?»; delete it to start over |
| `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt` | Last CAD-REV report (dated copies in `informes\`) |
| `%LOCALAPPDATA%\CadMepRevit\indice_familias.txt` | Cached index of the family libraries |
| `%LOCALAPPDATA%\RevitAutomation\instalacion.txt` | Log of the last silent install/uninstall |
| `Documents\RevitAutomation\<tool>\` | Settings, inputs and reports (`output\`) of the copied tools |

### 2.11 Troubleshooting

| Problem | Fix |
|---|---|
| The «RevitAutomation» tab does not appear | Close Revit, run the installer again, open Revit and choose «Always Load». |
| «No se encuentra Revit…» | Revit is not in `C:\Program Files\Autodesk`. Tick the version anyway or use `/versiones:`. |
| Fittings are not created | The pipe/duct type has no fittings in its Routing Preferences. The add-in tries to assign them from the project, the libraries and the fallback `Familias` folder; the report says which one is missing. Use a project created from the «Sistemas» (Systems) template. |
| SmartScreen blocks the `.exe` | It is not signed: «More info» → «Run anyway». |
