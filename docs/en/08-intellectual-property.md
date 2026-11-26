# 8. Intellectual property

[← Index](../../README.md) · [Español](../es/08-propiedad-intelectual.md)

## 8.1 Copyright and licence

**Copyright © 2026 Victor Hipp Murcia.** All source code, scripts and documentation in this repository, except the third-party items listed below, are the work of the author and are released under the **MIT licence** ([`LICENSE`](../../LICENSE)).

In short, the MIT licence allows anyone to use, copy, modify, merge, publish, distribute, sublicense and sell copies of the software, **provided the copyright notice and the licence text are kept** in all copies or substantial portions. The software is provided «as is», without warranty of any kind; the author is not liable for any damage arising from its use. The binding text is the English one in `LICENSE`.

The copyright holder and licence are also embedded in the metadata of the compiled DLLs and of the installer (`Directory.Build.props`).

## 8.2 Third-party components

These components are **not** covered by this project's licence; each keeps its own.

| Component | Use | Licence | Distributed? |
|---|---|---|---|
| [ACadSharp](https://github.com/DomCR/ACadSharp) 3.8.35 | Reading DWG/DXF | MIT | Yes, inside the installer |
| [PolySharp](https://github.com/Sergio0694/PolySharp) 1.15.0 | C# language attributes for .NET Framework 4.8 | MIT | No (build-time source generator) |
| [System.Text.Json](https://github.com/dotnet/runtime) 8.0.5 | JSON on .NET Framework 4.8 | MIT | Yes, in the 2023/2024 builds |
| Revit API (`RevitAPI.dll`, `RevitAPIUI.dll`), via the installed Revit or the `Nice3point.Revit.Api.*` NuGet packages | Compile-time reference | © Autodesk, Inc. (see each package's licence) | **No**: removed from the package by `crear_instalador.ps1` |
| Revit library families in `codigo/familias_uniones/*.rfa` | Fallback fittings | © Autodesk, Inc. | **Not in the repository.** They are embedded in an installer you build locally |

## 8.3 Autodesk families and the installer

The 12 fallback fitting families come from the Autodesk Revit content library. They are excluded from this public repository (`.gitignore`), and you must supply them locally to build the installer (see [`codigo/familias_uniones/README.md`](../../codigo/familias_uniones/README.md)).

Because a locally built `Instalar_RevitAutomation.exe` embeds those families, do not publish it (for example as a GitHub Release) unless you have the right to redistribute them, or replace them with families of your own.

## 8.4 Trademarks

Autodesk, Revit, AutoCAD, DWG and DWF are registered trademarks or trademarks of Autodesk, Inc. and/or its subsidiaries in the USA and other countries. They are used here only to describe compatibility. **This project is not affiliated with, endorsed by or sponsored by Autodesk, Inc.** Other names may be trademarks of their respective owners.

## 8.5 Confidential project data

No client drawings, reports or project names are included in this repository. The sample drawings in `codigo/ejemplos` and `codigo/samples` are synthetic. Contributors must keep it that way (see [Testing § 6.4](06-testing.md#64-real-drawings-confidential)).

## 8.6 Contributions

Unless stated otherwise, any contribution submitted to this repository is accepted under the same MIT licence.
