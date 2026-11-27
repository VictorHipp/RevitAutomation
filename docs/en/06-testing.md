# 6. Testing

[← Index](../../README.md) · [Español](../es/06-pruebas.md)

All automated tests run **outside Revit**. They check the CAD engine and the pure logic of each tool, not the Revit API itself. A feature is only verified once it has been tried inside Revit.

## 6.1 Engine tests (`codigo/tests/CadMepRevit.Tests`)

A console program (not xUnit/NUnit). It prints each check and returns a non-zero exit code on failure.

```powershell
$scratch = Join-Path $PWD 'tests\CadMepRevit.Tests\obj\test-scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch; $env:TMP = $scratch
dotnet run --project tests/CadMepRevit.Tests -c Release -- $scratch
```

Pass a scratch folder under `obj` so generated DWG/DXF files and slope reports do not land in `ejemplos`.

| File | Covers |
|---|---|
| `Program.cs` | CAD reading, units, topology, dimensions and labels, DWF, inventory, performance (3000 runs, 12 000 lines) and error messages |
| `SlopeTests.cs` | Slope labels, arrows, voting, elevations |
| `PipeFeatureTests.cs` | Risers, chambers, pipe systems |
| `LuminaireTests.cs` | Line-drawn luminaires |
| `PlacementTests.cs` | CAD registration (moved/rotated links) |
| `TrayTests.cs` | Cable-tray fittings |
| `NetworkFittingTests.cs` | Fittings, gaps and accessories |
| `CeilingTests.cs` | Ceiling-following profile |
| `PipeOutlineTests.cs` | Double-line pipes exported from Revit (crossings, end-cap remnants, edge pairs, drawn fitting bodies) and risers drawn as circles |

Fixtures: `codigo/samples/*.dxf` (synthetic tray/pipe/duct fitting cases) and `codigo/ejemplos/**` (synthetic slope and riser cases). The suite also generates its own CAD fixtures. Last recorded result: **382 checks passed, 0 failed** (25-11-2026).

`tests/CadMepRevit.SlopeTests` is an older standalone slope test program, also in the solution.

## 6.2 Tool harnesses (`codigo/tests/RevitAutomation.Pruebas`)

One console harness per tool. Each compiles only the pure-logic files of its tool and prints `PASS`/`FAIL`.

| Harness | Tool |
|---|---|
| `AislamientoHarness` | RITE insulation |
| `LlavesLocalHumedoHarness` | Wet-room valves |
| `PatinilloHarness` | Plumbing shaft |
| `PdfPublicationHarness` | PDF export |
| `PipeTagHarness` | Tagging |
| `PlanosHarness` | Sheets |
| `RegionHarness` | Fire-compartment regions |
| `SalasTecnicasHarness` | Plant rooms |

```powershell
dotnet run -c Release --project tests/RevitAutomation.Pruebas/PipeTagHarness
```

`crear_instalador.ps1` runs the engine tests and every harness before building.

## 6.3 Plan comparison before accepting a change

`tests/CadMepRevit.PlanComparison` runs the real Core and `AutoPlanner` against a simulated Revit catalogue and writes a signature of each plan (classification, geometry, sizes, systems, slopes, fittings and blocks) for **all drawings and all five buttons**. Compare a backup copy (before) with the candidate (after):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/CadMepRevit.PlanComparison/Compare-Plans.ps1 `
    -Antes <backup>\codigo -Planos <temporary folder with DWG> -AntesConUnionesActuales
```

- `-AntesConUnionesActuales` is required when the backup already has `BuildPlan.AllowsFitting`.
- `-Calibrar` also prints gap and accessory calibration data.
- The script writes `Pruebas/FIRMAS_*.json` in the candidate. **They contain drawing names: delete them afterwards**, together with the backup and the temporary drawings.

With Git, a convenient backup is a clean checkout of the previous commit: `git worktree add ..\antes HEAD~1`.

## 6.4 Real drawings (confidential)

Real drawings from client projects are optional and **confidential**. They are never stored in the repository, never named in code, tests, documentation or reports, and never kept after testing.

```powershell
$env:CADMEP_PLANOS  = '<temporary folder with DWG>'   # optional
$env:CADMEP_PRUEBAS = $env:CADMEP_PLANOS
```

With `CADMEP_PLANOS` set, `SlopeTests.RunReal` and `PlacementTests.RunReal` also run on every DWG in that folder, whatever its name. Without it those checks are skipped. Delete the temporary folder when done.

## 6.5 Verifying inside Revit

Use a project created from the «Sistemas» template, run each button and read the report sections listed in [User guide § 3.7](03-user-guide.md#37-the-report). The list of pending checks is in [Status and limitations](07-status-and-limitations.md).
