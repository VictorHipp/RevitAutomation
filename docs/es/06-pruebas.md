# 6. Pruebas

[← Índice](../../README.es.md) · [English](../en/06-testing.md)

Todas las pruebas automáticas se ejecutan **fuera de Revit**. Comprueban el motor CAD y la lógica pura de cada herramienta, no la API de Revit. Una función solo está verificada cuando se ha probado dentro de Revit.

## 6.1 Pruebas del motor (`codigo/tests/CadMepRevit.Tests`)

Es un programa de consola (no xUnit/NUnit). Muestra cada comprobación y devuelve un código de salida distinto de cero si algo falla.

```powershell
$scratch = Join-Path $PWD 'tests\CadMepRevit.Tests\obj\test-scratch'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:TEMP = $scratch; $env:TMP = $scratch
dotnet run --project tests/CadMepRevit.Tests -c Release -- $scratch
```

Pase una carpeta temporal dentro de `obj` para que los DWG/DXF y los informes de pendientes que se generan no acaben en `ejemplos`.

| Archivo | Qué cubre |
|---|---|
| `Program.cs` | Lectura CAD, unidades, topología, cotas y rótulos, DWF, inventario, rendimiento (3000 tramos, 12 000 líneas) y mensajes de error |
| `SlopeTests.cs` | Rótulos de pendiente, flechas, votación, cotas |
| `PipeFeatureTests.cs` | Bajantes, arquetas, sistemas de tubería |
| `LuminaireTests.cs` | Luminarias dibujadas con líneas |
| `PlacementTests.cs` | Encaje del CAD (vínculos movidos y girados) |
| `TrayTests.cs` | Uniones de bandeja |
| `NetworkFittingTests.cs` | Uniones, huecos y accesorios |
| `CeilingTests.cs` | Perfil «sigue el techo» |

Datos de prueba: `codigo/samples/*.dxf` (casos sintéticos de uniones de bandeja, tubería y conducto) y `codigo/ejemplos/**` (casos sintéticos de pendientes y bajantes). Las pruebas también generan sus propios planos. Último resultado registrado: **382 comprobaciones superadas, 0 fallidas** (25-11-2026).

`tests/CadMepRevit.SlopeTests` es un programa anterior de pruebas de pendientes, también incluido en la solución.

## 6.2 Pruebas de las herramientas (`codigo/tests/RevitAutomation.Pruebas`)

Un programa de consola (harness) por herramienta. Cada uno compila solo los archivos de lógica pura de su herramienta y muestra `PASS`/`FAIL`.

| Harness | Herramienta |
|---|---|
| `AislamientoHarness` | Aislamiento RITE |
| `LlavesLocalHumedoHarness` | Llaves de local húmedo |
| `PatinilloHarness` | Patinillo de fontanería |
| `PdfPublicationHarness` | Exportar PDF |
| `PipeTagHarness` | Etiquetado |
| `PlanosHarness` | Planos |
| `RegionHarness` | Región de sectorización |
| `SalasTecnicasHarness` | Salas técnicas |

```powershell
dotnet run -c Release --project tests/RevitAutomation.Pruebas/PipeTagHarness
```

`crear_instalador.ps1` pasa las pruebas del motor y todos los harness antes de compilar.

## 6.3 Comparar planes antes de aceptar un cambio

`tests/CadMepRevit.PlanComparison` ejecuta el núcleo real y `AutoPlanner` contra un catálogo de Revit simulado y escribe una firma de cada plan (clasificación, geometría, medidas, sistemas, pendientes, uniones y bloques) para **todos los planos y los cinco botones**. Compare una copia de seguridad (antes) con la candidata (después):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/CadMepRevit.PlanComparison/Compare-Plans.ps1 `
    -Antes <copia>\codigo -Planos <carpeta temporal con DWG> -AntesConUnionesActuales
```

- `-AntesConUnionesActuales` es obligatorio cuando la copia ya tiene `BuildPlan.AllowsFitting`.
- `-Calibrar` muestra además los datos de calibración de huecos y accesorios.
- El script escribe `Pruebas/FIRMAS_*.json` en la candidata. **Contienen nombres de planos: bórrelos después**, junto con la copia y los planos temporales.

Con Git, una copia cómoda de la versión anterior es un checkout limpio del commit previo: `git worktree add ..\antes HEAD~1`.

## 6.4 Planos reales (confidenciales)

Los planos reales de proyectos de clientes son opcionales y **confidenciales**. Nunca se guardan en el repositorio, nunca se nombran en el código, las pruebas, la documentación ni los informes, y no se conservan después de probar.

```powershell
$env:CADMEP_PLANOS  = '<carpeta temporal con DWG>'   # opcional
$env:CADMEP_PRUEBAS = $env:CADMEP_PLANOS
```

Con `CADMEP_PLANOS`, `SlopeTests.RunReal` y `PlacementTests.RunReal` también se ejecutan con cada DWG de esa carpeta, se llame como se llame. Sin ella, esas comprobaciones se omiten. Borre la carpeta temporal al terminar.

## 6.5 Verificar dentro de Revit

Use un proyecto creado con la plantilla «Sistemas», ejecute cada botón y lea las secciones del informe indicadas en [Guía de uso § 3.7](03-guia-de-uso.md#37-el-informe). Lo que falta por comprobar está en [Estado y limitaciones](07-estado-y-limitaciones.md).
