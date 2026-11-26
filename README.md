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
| [2. Installation](docs/en/02-installation.md) | Installing, updating and uninstalling the add-in |
| [3. User guide](docs/en/03-user-guide.md) | How to use CAD-REV and the other tools; reports and settings files |
| [4. Development](docs/en/04-development.md) | Prerequisites, building for each Revit version, generating the installer, **setting up a new PC** |
| [5. Architecture](docs/en/05-architecture.md) | Projects, processing pipeline and source map |
| [6. Testing](docs/en/06-testing.md) | Automated tests outside Revit, tool harnesses, plan comparison |
| [7. Status and limitations](docs/en/07-status-and-limitations.md) | What is verified, what is pending inside Revit, known limits |
| [8. Intellectual property](docs/en/08-intellectual-property.md) | Copyright, licence, third-party components and trademarks |

Detailed design specifications and change history (Spanish only) live in [`codigo/docs/`](codigo/docs/), [`codigo/LEEME_VERSION_COMBINADA.txt`](codigo/LEEME_VERSION_COMBINADA.txt) and [`codigo/TRASPASO_PROYECTO.txt`](codigo/TRASPASO_PROYECTO.txt).

## Quick start (developers)

```powershell
git clone https://github.com/VictorHipp/RevitAutomation.git
cd RevitAutomation\codigo
# 1. Install the .NET 10 SDK and Git            → docs/en/04-development.md § 4.1
# 2. Copy the 12 fitting families into codigo\familias_uniones\   → § 4.2
.\CREAR_INSTALADOR.cmd     # runs all tests, builds the 5 Revit versions and creates ..\Instalar_RevitAutomation.exe (§ 4.3)
```

Step by step: [tools to download](docs/en/04-development.md#41-tools-to-download) · [getting the 12 families](docs/en/04-development.md#42-getting-the-12-fitting-families) · [creating the installer](docs/en/04-development.md#43-creating-the-installer-step-by-step) · [setting up a new PC](docs/en/04-development.md#44-setting-up-a-new-pc).

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
