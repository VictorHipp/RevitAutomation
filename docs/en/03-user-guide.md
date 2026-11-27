# 3. User guide

[← Index](../../README.md) · [Español](../es/03-guia-de-uso.md)

The short end-user guide that ships with the add-in is [`codigo/docs/Guia_CadMepRevit.txt`](../../codigo/docs/Guia_CadMepRevit.txt) (Spanish). This page summarises it in English.

## 3.1 Before you start

- Use a Revit **project** created from the «Sistemas» (Systems) or «Mecánica» template: it has pipe, duct, tray and conduit types and their systems. The «Arquitectura» template has no MEP types.
- In the CAD drawing, each network should be on its own layer (`AF`, `ACS`, `SANEAMIENTO`, `P-PIPE`, `M-HVAC-DUCT`, `E-POWR-CNDT`…) and drawn with LINE or POLYLINE in model space.
- Recommended: link the same DWG in the project (Insert › Link CAD) so the model stays aligned with the architecture.

## 3.2 Running a CAD-REV button

1. Click the button for the building service (RevitAutomation › CAD-REV › …).
2. Choose the `.dwg` or `.dxf` file.
3. Only for Tuberías, Conductos and Bandejas: optionally choose the **DWF of the same drawing** (Cancel if you have none). Its texts are merged with the DWG's to read written sizes. Only model-space DWF 6 (not DWFx).
4. If the project has several levels, choose the **level**. This is the only question.
5. At the end a report is shown and the created elements stay selected. **Ctrl+Z** undoes the whole run.

If the drawing has layers or blocks the add-in does not recognise, a «¿Qué es esto?» dialog asks what each one is. The answers are stored in `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` and reused for later drawings.

## 3.3 Elevations

- **Pipes:** slopes and written elevations from the drawing, relative to the chosen level. Invert/soffit elevations (C.I., solera, fondo) are converted to the axis using the outer radius. Without any written elevation, the highest point is placed at +2500 mm (suspended) or −600 mm (buried) and flagged `SUPUESTO`.
- **Ducts, trays, conduits:** axis at 2500 mm above the level, then the **ceiling-following rule** places the top 100 mm under the slab above.
- **Grilles, accessories, equipment:** at the elevation of their run.
- **Other devices** use usual heights, flagged `SUPUESTO`: lighting 2700 mm (wall/emergency 2200), switches 1100, presence detectors 2700, sockets 300, panels 1500, extinguishers 1000, hose reels 1500, call points 1200, sirens 2300, signs 2100, fire detectors 2700.

**Fittings and crossings.** Pipes: elbows, tees and reductions (four-way crosses are left for the technician). Ducts, cable trays and electrical conduits: elbows, tees, crosses and reductions. In drawings exported from Revit, a pipe cut where another passes is a crossing at a different level: it is modelled straight and unjoined, and the report says so. Risers drawn as circles take the diameter of their pipe; a circle smaller than its pipe with no SUBE or BAJA label nearby is not modelled and appears in the report as «REVISAR».

## 3.4 Slopes

Draw the slope as text (`2 %`, `i=1,5 %`, `2 ‰`…) with an arrow pointing downhill. Arrows made of lines, triangles, hatches, variable-width polylines, leaders and blocks are recognised. A label applies to the whole run up to the next tee or cross. Without a valid label, or with ties or conflicting votes, the run stays horizontal and the report says what to check and where.

## 3.5 Families for CAD blocks

1. A family whose name matches the block (drawings exported from Revit are named «Family - Type-…»), from the project or the libraries.
2. Otherwise a **generic** Revit family of the same class (grille, downlight, switch, extinguisher, smoke detector…), preferring families that need no host.
3. Otherwise a 3D marker (`DirectShape`) with the CAD footprint and no connectors.

Libraries are listed in `%APPDATA%\CadMepRevit\bibliotecas.txt` (one folder per line).

## 3.6 Layer recognition

| Layer name contains | Built as |
|---|---|
| AF, ACS, RACS, SANEAMIENTO, PLUVIALES, FONTANERÍA, PCI, P-PIPE | Pipe (Tuberías) |
| CONDUCTO, CLIMA, IMPULSIÓN, RETORNO, EXTRACCIÓN, VENTILACIÓN, M-HVAC-DUCT | Duct |
| BANDEJA, TRAY, E-CABL-TRAY | Cable tray |
| CONDUIT, CORRUGADO, E-POWR-CNDT, FUERZA | Electrical conduit |
| -IDEN, -ANNO, -NOTE, TEXTOS, COTAS, -RISE | Not built (annotations) |

A layer called `0`, or with a name that says nothing, is not built: rename it. Double-line (outline) drawings are detected automatically and modelled as one run along the axis with the drawn width.

## 3.7 The report

Shown after every button and saved to `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt`, with a dated copy in `informes\`:

1. Elements modelled, by system, with family and type.
2. Elements not modelled, with the reason (including «modelled by button …»).
3. Warnings: failed fittings, sizes not applied, DWF not used…
4. Defaults and `SUPUESTO` values.
5. Balance: drawing entities = modelled + not modelled.

Useful sections when checking a result: `Posición`, `UNIONES`, «Pendiente antes y después», «Huecos cerrados», `PENDIENTES`, «ALTURA SEGÚN EL TECHO» and «ACCESORIOS, TERMINALES Y EQUIPOS DE RED».

## 3.8 Other tools

- **Salas técnicas:** open the plan with the plant-room DWG linked (pick it if there are several) and click the variant. Each closed outline with a label inside or next to it becomes the labelled equipment (circle → cylinder, otherwise box), with the usual height or the `H=…` label. Re-running replaces only what this button created. Keys and heights are editable in `Documents\RevitAutomation\SalasTecnicas`.
- **Patinillo:** select the shaft (room or line outline) or draw it; all values are edited in the dialog and saved. Report and deviations in `Documents\RevitAutomation\Patinillo\output`.
- **Aislamiento RITE:** select pipes (or pick them); it only asks whether they run inside or outside the building. Report in `Documents\RevitAutomation\Aislamiento\output`.
- **Llaves de local húmedo:** choose block and layer, a level-based pipe-accessory family, units, height (default 1000 mm) and extra rotation. Positions already occupied (< 1 mm) are skipped. CSV report in `Documents\RevitAutomation\LlavesLocalHumedo\output`.
- **Etiquetado:** select elements in the active view and click the matching button. Straight runs longer than 15 cm get a tag 20 cm above the axis, moved up to 100 cm with a leader, or below the axis if needed.
- **Planos:** shows a summary (views, sheets, levels, available tags, missing data) before creating anything; re-running replaces only what it generated before.
- **Región de sectorización:** uses the selected CAD (or the only one in the view); each closed polyline becomes a filled region of type «REG - <layer>».

## 3.9 Common problems

| Message / symptom | Fix |
|---|---|
| «En este plano no hay nada que modele el botón …» | The drawing belongs to another service; the detail says which button models each layer and block. |
| «El proyecto no tiene tipos de …» | Use the «Sistemas» template, or Manage › Transfer Project Standards. |
| Everything is tiny or huge | Fix the drawing units (UNITS / INSUNITS). |
| Fittings fail | The report says which fitting family the type is missing. |
