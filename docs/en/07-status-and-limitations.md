# 7. Status and limitations

[← Index](../../README.md) · [Español](../es/07-estado-y-limitaciones.md)

Status as of **25-11-2026** (version 1.0.0).

## 7.1 Verified

| Check | Result | Where |
|---|---|---|
| Build of RevitAutomation + CAD-REV for Revit 2023, 2024, 2025, 2026 and 2027 against each version's API | 0 errors, 0 warnings | Outside Revit |
| Engine tests (`CadMepRevit.Tests`) | 382 passed, 0 failed | Outside Revit |
| Tool harnesses (tagging, insulation, sheets, region, PDF, plant rooms, shaft, valves) | All passed (≈ 830 logic checks in total with the engine) | Outside Revit |
| Installer in a scratch folder (`/destino:`) | Installs the versions, removes old add-ins, keeps unrelated ones | Outside Revit |
| Earlier CAD MEP versions | Add-in loads; ducts created from a real HVAC drawing | Inside Revit 2025 (older version) |

## 7.2 Pending verification inside Revit

The 19-11-2026 and 25-11-2026 updates still need to be checked inside Revit 2023–2027:

- The tab, pulldowns and copied tools appear and work in every version.
- Actual creation of elbows, tees, crosses and reducers; pipe slopes unchanged after trimming; four-way pipe junctions reported; gap closure.
- In 3D: valves and dampers cut the runs; grilles at duct elevation, oriented and connected at the end or side.
- Cable-tray fittings.
- Alignment over a moved and rotated linked CAD (`Posición` line in the report).
- Placement of generic families.
- The ceiling-following rule: risers with two elbows, rectangular riser alignment, suspended ceilings, slabs in linked models.

Check `Posición`, `UNIONES`, «Pendiente antes y después», «Huecos cerrados», «ALTURA SEGÚN EL TECHO» and «ACCESORIOS, TERMINALES Y EQUIPOS DE RED» in `ultimo_informe.txt`.

## 7.3 Known limitations

- Only **model space** is read. Arcs and curved polylines are approximated by chords. Unloaded XREFs are reported, not read.
- Fittings depend on the type's Routing Preferences; Revit can reject runs shorter than the fitting.
- An off-centre transition (axes neither intersect nor align) is left unjoined.
- Four-way pipe junctions and electrical conduit junctions are left for the technician.
- 3D markers (`DirectShape`) have no connectors: they do not connect or form systems.
- Face-based or hosted families are not placed directly (ceiling families are hosted on a horizontal reference plane «CAD-REV · techo …»); otherwise a marker is created and reported.
- Doors, windows, floors and electrical circuits are not created.
- Heights not shown in plan are `SUPUESTO` and must be reviewed.
- Only model-space DWF 6 is supported (not DWFx).
- Very dense areas (bathrooms, next to fixtures) can leave some 5- or 6-pipe junctions unjoined; the report lists them.

## 7.4 Possible next steps

Taken from the project's hand-over notes (`codigo/TRASPASO_PROYECTO.txt`, Spanish):

1. Run the full verification inside Revit listed in 7.2 and fix what the reports show.
2. Import several drawings at once (architecture + services) or resolve XREFs.
3. Connect runs to equipment when the connector is offset (short run or elbow instead of rejecting).
4. Electrical circuits from sockets to a chosen panel.
5. Doors and windows in wall openings.
6. Optionally publish the installer as a GitHub Release once it no longer embeds Autodesk families.
