# RevitAutomation

**English** · [Español](README.es.md)

Add-in for **Autodesk Revit 2023, 2024, 2025, 2026 and 2027** that adds a single ribbon tab, **«RevitAutomation»**, with tools for MEP (building services) design. Its main engine, **CAD-REV**, turns a CAD drawing (DWG/DXF) into a Revit model: pipes, ducts, cable trays, fittings, lighting fixtures and devices.

> The user interface, reports and messages of the add-in are in **Spanish**. This documentation is available in English and Spanish.

## What it does

| Panel | Pulldown | Buttons |
|---|---|---|
| CAD-REV | CAD-REV | Pipes · Ducts · Cable trays · Lighting · Equipment & fire protection (from DWG/DXF to Revit) |
| Instalaciones | Salas técnicas | Water meters · Electrical · Fire protection · Plumbing |
| Instalaciones | Fontanería | Plumbing shaft · RITE pipe insulation · Wet-room valves |
| Etiquetado | Etiquetado | Tag selected pipes · ducts · cable trays · families · lighting fixtures |
| Documentación | Planos | Plumbing · HVAC · Electrical · Lighting · Generic & fire protection · Full set |
| Documentación | Exportar | Export PDF · Export CAD |
| Región | Región | Fire-compartment regions |

Every operation is wrapped in a single undo step (**Ctrl+Z** undoes the whole run) and ends with a report of what was modelled, what was not, and why.

## Documentation

| | |
|---|---|
| [1. Overview](docs/en/01-overview.md) | What each tool does and how the pieces fit together |
| [2. Download and installation](docs/en/02-installation.md) | Downloading the project, setting up the PC, creating the installer; installing, updating and uninstalling the add-in |
| [3. User guide](docs/en/03-user-guide.md) | How to use CAD-REV and the other tools; reports and settings files |
| [4. Development](docs/en/04-development.md) | For people who change the code: Git, building each Revit version, coding rules |
| [5. Architecture](docs/en/05-architecture.md) | Projects, processing pipeline and source map |
| [6. Testing](docs/en/06-testing.md) | Automated tests outside Revit, tool harnesses, plan comparison |
| [7. Status and limitations](docs/en/07-status-and-limitations.md) | What is verified, what is pending inside Revit, known limits |
| [8. Intellectual property](docs/en/08-intellectual-property.md) | Copyright, licence, third-party components and trademarks |

Detailed design specifications and change history (Spanish only) live in [`codigo/docs/`](codigo/docs/), [`codigo/LEEME_VERSION_COMBINADA.txt`](codigo/LEEME_VERSION_COMBINADA.txt) and [`codigo/TRASPASO_PROYECTO.txt`](codigo/TRASPASO_PROYECTO.txt).

## Quick start

1. Install **Git**: <https://git-scm.com/download/win>.
2. Open PowerShell in the folder where you want to keep the project (in File Explorer, type `powershell` in the address bar) and type:

   ```powershell
   git clone https://github.com/VictorHipp/RevitAutomation.git
   ```

3. In `RevitAutomation\codigo`, double-click **`PREPARAR_PC.cmd`**: it installs the .NET 10 and 8 SDKs.
4. Recommended: copy the 12 fitting families into `codigo\familias_uniones\`. If they are missing, `CREAR_INSTALADOR.cmd` looks for them in the PC's Revit libraries (or in the folder given with `-Familias`) and, if it cannot find them, warns and creates the installer without them.
5. Double-click **`CREAR_INSTALADOR.cmd`**: it creates `Instalar_RevitAutomation.exe` next to the `codigo` folder.
6. Close Revit and double-click **`Instalar_RevitAutomation.exe`**.

Step by step: [Download and installation](docs/en/02-installation.md). To change the code: [Development](docs/en/04-development.md).

## Repository layout

```
RevitAutomation/
├── README.md / README.es.md     Front page (English / Spanish)
├── LICENSE                      MIT licence
├── docs/
│   ├── en/                      English documentation
│   └── es/                      Spanish documentation
└── codigo/                      Source code (Visual Studio solution CadMepRevit.sln)
    ├── src/                     Add-in projects (RevitAutomation, CadMepRevit, CadMepRevit.Core)
    ├── tests/                   Console test programs (run outside Revit)
    ├── instalador/              Installer project (Instalar_RevitAutomation.exe)
    ├── docs/                    Specifications and end-user guide (Spanish)
    ├── ejemplos/, samples/      Synthetic DWG/DXF test drawings
    └── familias_uniones/        Fallback fitting families (not in Git: Autodesk content)
```

## Licence

Copyright © 2026 Victor Hipp Murcia. Released under the [MIT licence](LICENSE). Third-party components keep their own licences; see [Intellectual property](docs/en/08-intellectual-property.md).

*Autodesk, Revit, AutoCAD, DWG and DWF are registered trademarks or trademarks of Autodesk, Inc. This project is not affiliated with, endorsed by or sponsored by Autodesk.*
