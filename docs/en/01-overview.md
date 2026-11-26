# 1. Overview

[← Index](../../README.md) · [Español](../es/01-vision-general.md)

RevitAutomation is a C# add-in for Autodesk Revit 2023–2027. When Revit starts, it creates one ribbon tab, **«RevitAutomation»**. Each section of the tab is a panel holding a pulldown menu with its buttons.

The add-in is aimed at MEP technicians (plumbing, drainage, HVAC, electrical, lighting and fire protection). Its interface, dialogs and reports are in Spanish.

## 1.1 The ribbon tab

| Panel | Pulldown | Buttons | Source |
|---|---|---|---|
| CAD-REV | CAD-REV | Tuberías · Conductos · Bandejas · Luminarias · Equipos y PCI | `src/RevitAutomation/Commands/CadRevCommands.cs` → `src/CadMepRevit` |
| Instalaciones | Salas técnicas | 1 · Contadores de agua · 2 · Electricidad · 3 · PCI · 4 · Fontanería | `Commands/SalasTecnicas` |
| Instalaciones | Fontanería | Patinillo de fontanería · Aislamiento RITE · Llaves de local húmedo | `Commands/Patinillo`, `Commands/Aislamiento`, `Commands/LlavesLocalHumedo` |
| Etiquetado | Etiquetado | Tuberías · Conductos · Bandejas · Familias · Luminarias (selected) | `Commands/PipeTagging` |
| Documentación | Planos | Fontanería · Climatización · Electricidad · Iluminación · Genéricas y PCI · Conjunto completo | `Commands/Planos` |
| Documentación | Exportar | Exportar PDF · Exportar CAD | `Commands/PdfExportCommand.cs`, `Commands/CadExportCommand.cs` |
| Región | Región | Región de sectorización | `Commands/Regiones` |

## 1.2 CAD-REV: from a CAD drawing to a Revit model

CAD-REV (formerly «CAD MEP») reads a DWG or DXF drawing and models one building service per button. Each button reads the whole drawing but models **only its own scope**, so several buttons can be run on the same drawing without duplicating elements.

| Button | Models |
|---|---|
| **Tuberías** (Pipes) | Pipes and vertical risers, sanitary fixtures, inspection chambers, water equipment and pipe accessories. Creates elbows, tees and reducers. Reads slopes (`%`, `‰`, arrows, elevation marks). Four-way pipe junctions are reported for the technician to resolve. |
| **Conductos** (Ducts) | Ducts with elbows, tees, crosses and transitions. Grilles, diffusers, dampers and air equipment placed at the duct's elevation and orientation. |
| **Bandejas** (Cable trays) | Cable trays with their fittings. Electrical conduits are modelled as straight runs. |
| **Luminarias** (Lighting) | Lighting fixtures (downlights, LED strips, emergency lights…), switches and presence detectors, including fixtures drawn with loose lines instead of blocks. |
| **Equipos y PCI** (Equipment & fire protection) | Extinguishers, hose reels, detectors, call points, sirens, signs, electrical panels, sockets and other devices. |

Key behaviour:

- **Placement over the CAD.** If the same drawing is linked or imported in the project, everything is placed on top of it, even if the link was moved or rotated. If it is missing, the add-in links it.
- **Sizes from the drawing.** Dimensions, `Ø`/`DN` labels, leaders and an optional DWF of the same drawing are used to size runs.
- **Ceiling-following rule.** Suspended ducts, trays, conduits and horizontal pipes without a written elevation keep their top 100 mm below the slab above (including slabs in linked models), stepping up or down with two elbows where the slab changes.
- **No invented values.** When the drawing does not give a value (e.g. the height of a rectangular duct), a default is used and flagged as `SUPUESTO` (assumed) in the report.
- **Learning unknown items.** Unknown layers and blocks trigger a «¿Qué es esto?» dialog; the answers are remembered for future drawings.
- **One undo step and a report.** All edits go into one `TransactionGroup`; a report is shown and saved to `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt`.

## 1.3 Other tools

| Tool | What it does |
|---|---|
| **Salas técnicas** (Plant rooms) | Models a plant room from its linked CAD in the active plan view: every closed outline with a label becomes the equipment named by the label (tanks, pumps, panels, meters…), with its shape and usual height, plus its pipes or trays. Four variants: water meters (1.00 m), electrical (2.70 m), fire-protection pumps (2.70 m), plumbing (2.50 m). |
| **Patinillo de fontanería** (Plumbing shaft) | Models the vertical cold/hot water shaft: risers from the ground-floor meters and, on each floor, the branches to each dwelling under the slab. Shows a preview matrix, creates a 3D view and an elevation. |
| **Aislamiento RITE** (Pipe insulation) | Insulates the selected pipes, fittings and accessories according to the Spanish RITE regulation (IT 1.2.4.2.1.2, simplified procedure): identifies the service from the Revit system, computes the thickness from diameter, temperature and conductivity and rounds it to a commercial thickness. Never reduces existing insulation. |
| **Llaves de local húmedo** (Wet-room valves) | Places valve families on the block insertions of the linked CAD (block + layer chosen by the user), honouring the link's offset and rotation. Placement only, no hydraulic connection. |
| **Etiquetado** (Tagging) | Tags selected pipes, ducts, trays, families or lighting fixtures with the model's own tags, avoiding overlaps (moves the tag or adds a leader; reports what does not fit). |
| **Planos** (Sheets) | Generates discipline plan views per level, system colours, tags, schedules, legends and sheets with title block; shows a summary first and saves a combined PDF on the Desktop. Does not modify model elements. |
| **Exportar PDF / CAD** | Exports sheet sets or selected views to a combined PDF, or to DWG, DXF or DGN. |
| **Región de sectorización** (Fire compartments) | Traces the coloured zones of the linked CAD as filled regions in the active plan: each closed polyline becomes a region with the colour of its layer. |

The tools in *Instalaciones*, *Etiquetado*, *Documentación* and *Región* were copied unchanged in behaviour from an earlier RevitAutomation project; their settings live in `Documents\RevitAutomation\<tool>`.

## 1.4 Supported Revit versions

| Revit | .NET target | Notes |
|---|---|---|
| 2023, 2024 | .NET Framework 4.8 (`net48`) | Compatibility layer for missing .NET APIs and 32-bit `ElementId` |
| 2025, 2026 | .NET 8 (`net8.0-windows`) | 2026 loads the add-in in an isolated context |
| 2027 | .NET 10 (`net10.0-windows`) | Isolated context |

The tool logic is the same in all five versions. See [Architecture](05-architecture.md) for how this is achieved.
