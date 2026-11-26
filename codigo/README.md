# RevitAutomation para Revit 2023, 2024, 2025, 2026 y 2027

> **Versión 2023–2027 (25-11-2026).** Copia del proyecto de 2025–2027 que añade Revit 2023 y 2024. Esas dos versiones usan
> .NET Framework 4.8: `src/Revit.props` las compila con `net48`, `src/CadMepRevit.Core/Compat/NetFrameworkCompat.cs` aporta
> (solo en `net48`) los miembros de .NET 8 que faltan, `src/CadMepRevit/Compat/RevitApiCompat.cs` traduce `ElementId` de
> 64 bits a los de 32 bits de Revit 2023, y `RevitAutomationApplication` resuelve desde su carpeta las bibliotecas que
> acompañan al DLL (System.Text.Json…). La lógica de las herramientas es la misma en las cinco versiones.

Complemento de Revit con **una sola pestaña, «RevitAutomation»**, ordenada por secciones. Cada sección es un panel con su
desplegable:

| Panel | Desplegable | Botones |
|---|---|---|
| CAD-REV | CAD-REV | Tuberías · Conductos · Bandejas · Luminarias · Equipos y PCI (del plano DWG/DXF a Revit) |
| Instalaciones | Salas técnicas | Contadores de agua · Electricidad · PCI · Fontanería |
| Instalaciones | Fontanería | Patinillo de fontanería · Aislamiento RITE · Llaves de local húmedo |
| Etiquetado | Etiquetado | Tuberías · Conductos · Bandejas · Familias · Luminarias (seleccionadas) |
| Documentación | Planos | Fontanería · Climatización · Electricidad · Iluminación · Genéricas y PCI · Conjunto completo |
| Documentación | Exportar | Exportar PDF · Exportar CAD |
| Región | Región | Región de sectorización |

- **CAD-REV** (antes «CAD MEP») lee el plano y modela cada instalación con codos, tes y reducciones. Las redes colgadas
  siguen el techo: 100 mm bajo el forjado, subiendo o bajando con codos. Detalle en
  [docs/ESPEC_ALTURA_TECHO_Y_UNIONES_2026-11-25.md](docs/ESPEC_ALTURA_TECHO_Y_UNIONES_2026-11-25.md).
- Las demás herramientas vienen del proyecto RevitAutomation anterior, sin cambios de funcionamiento. Sus ajustes siguen en
  `Documentos\RevitAutomation\<herramienta>`. Detalle en
  [docs/ESPEC_REVITAUTOMATION_PESTANA_UNICA_2026-11-25.md](docs/ESPEC_REVITAUTOMATION_PESTANA_UNICA_2026-11-25.md).
- Guía de uso de CAD-REV: [docs/Guia_CadMepRevit.txt](docs/Guia_CadMepRevit.txt). Historia del motor:
  [LEEME_VERSION_COMBINADA.txt](LEEME_VERSION_COMBINADA.txt) y [TRASPASO_PROYECTO.txt](TRASPASO_PROYECTO.txt).

## Instalador (Instalar_RevitAutomation.exe)

- **Usarlo:** con Revit cerrado, doble clic en `Instalar_RevitAutomation.exe`. Detecta Revit 2023, 2024, 2025, 2026 y 2027, instala
  solo para el usuario (no pide administrador) en `%APPDATA%\Autodesk\Revit\Addins\<año>\RevitAutomation` y permite
  indicar la carpeta de familias de la oficina (`%APPDATA%\CadMepRevit\bibliotecas.txt`). Queda en «Aplicaciones
  instaladas» para desinstalarlo.
- **Quita lo antiguo:** antes de instalar quita, para este usuario, el RevitAutomation antiguo (y su módulo
  «CAD → Revit») y el CAD MEP antiguo, solo si reconoce sus manifiestos. No toca otros complementos, ni los ajustes de
  `Documentos\RevitAutomation`, ni las reglas aprendidas de CAD-REV. Si encuentra uno instalado para todos los usuarios,
  avisa (quitarlo necesita administrador).
- **Sin ventana:** `Instalar_RevitAutomation.exe /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"carpeta"]` y
  `Instalar_RevitAutomation.exe /desinstalar /silencioso`. Resultado en `%LOCALAPPDATA%\RevitAutomation\instalacion.txt`.
- **Generarlo:** doble clic en `CREAR_INSTALADOR.cmd` (hace falta el SDK de .NET 10). Pasa todas las pruebas, compila las
  tres versiones contra su API (la de Revit instalado o, si no, el paquete NuGet) sin símbolos de depuración, comprueba
  que no llevan rutas del equipo y deja el `.exe` en la carpeta que contiene la del código.
- Windows puede avisar con SmartScreen porque el `.exe` no está firmado: «Más información» → «Ejecutar de todas formas».
- `instalar.cmd` / `desinstalar.cmd`: lo mismo desde el código (generan el instalador y lo ejecutan sin ventana).

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Revit.props` | Versiones de Revit (2023 y 2024 con .NET Framework 4.8, 2025 y 2026 con .NET 8, 2027 con .NET 10) y referencia a su API. |
| `src/RevitAutomation` | El complemento: pestaña (`RevitAutomationApplication.cs`), iconos y herramientas (`Commands/…`). Botones de CAD-REV en `Commands/CadRevCommands.cs`. |
| `src/CadMepRevit` | Motor CAD-REV (del plano a Revit) con sus ventanas e informes. Lo carga RevitAutomation. |
| `src/CadMepRevit.Core` | Sin Revit: lectura DWG/DXF (ACadSharp 3.8.35), capas, bloques, topología, cotas, pendientes y altura bajo el techo. |
| `familias_uniones/` | Codos, tes, cruces y transiciones de reserva (se instalan en `Familias`). |
| `tests/CadMepRevit.Tests` | Pruebas del motor fuera de Revit. |
| `tests/RevitAutomation.Pruebas` | Pruebas de la lógica de cada herramienta (etiquetado, salas, patinillo, aislamiento, planos, región, llaves, PDF). |
| `instalador/` | Proyecto de `Instalar_RevitAutomation.exe` (.NET Framework 4.8). Se genera con `CREAR_INSTALADOR.cmd`. |
## Compilar y probar a mano

```
dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true
dotnet run --project tests/CadMepRevit.Tests -c Release -p:BuildForRevit2027=true --no-build
```

Las pruebas de pendientes están integradas en ese ejecutable. Si `CADMEP_PLANOS` (o `CADMEP_PRUEBAS`)
apunta a una carpeta con DWG, además se comprueban con ellos las pendientes y la colocación sobre el CAD,
sin depender de sus nombres. Para comparar una copia anterior con la actual en los cinco botones:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/CadMepRevit.PlanComparison/Compare-Plans.ps1 -Antes <copia anterior>\codigo -Planos <carpeta con DWG> -AntesConUnionesActuales
```

Los planos reales de clientes son confidenciales: cópielos a una carpeta temporal solo para la prueba y
bórrela después, junto con los archivos `FIRMAS_*.json` que genera la comparación en `Pruebas\`.

- Hace falta el SDK de .NET 10, que compila las cinco versiones (2023 y 2024 con .NET Framework 4.8, sin instalar nada más: los ensamblados de referencia se descargan de NuGet).
- Las referencias de la API se toman de la instalación de cada Revit o de los paquetes NuGet de la
  versión correspondiente. MSBuild comprueba la versión de la API.
- SourceLink está desactivado por problemas en unidades de red.
- **Planos reales de prueba (opcionales):** no se incluyen ni se citan por su nombre. Sin `CADMEP_PLANOS`,
  esas comprobaciones se omiten.
- `ejemplos/ejemplo_mep.dwg` lo generan las pruebas y sale distinto en cada ejecución.

## Qué está probado y qué no

- **Probado automáticamente (fuera de Revit):** lectura de DWG y DXF, unidades, capas, bloques,
  descarte de tramos cortos, líneas que se pisan, curvas que hacen de codo, partición en tes, unión de
  colineales, detección de codo/te/cruz/transición, rendimiento con 3000 tramos y mensajes de error de
  archivos inexistentes o dañados.
  Doble línea (`OutlineDetector`): ejes y anchos desde contornos, testas, rectángulos ambiguos, conductos
  paralelos cercanos, cierre de huecos de te/codo/transición, en el ejemplo y en el plano real de
  climatización (24 líneas → 4 conductos 600/400 mm con 1 te y 1 codo); 12 000 líneas en ~2 s.
  Muros: ejemplo sintético y plano real (2814 líneas → 1426 muros).
  Bloques: función deducida del nombre (equipo, terminal, conector, pieza de unión…), familia y tipo
  de los nombres exportados por Revit y huella real medida (también con punto de inserción falso).
- **Verificado solo por compilación:** todas las llamadas a la API de Revit 2025 y 2027 (`Pipe.Create`,
  `Duct.Create`, `CableTray.Create`, `Conduit.Create`, `Wall.Create`,
  `NewElbowFitting/NewTeeFitting/NewCrossFitting/NewTransitionFitting`, parámetros `RBS_*`, preferencias de
  enrutamiento, `DirectShape` para marcadores 3D, cinta) y las ventanas. Compilan contra las DLL reales de
  las dos versiones, con 0 avisos.
- **Botones nuevos, probado fuera de Revit:**
  - El lector y la red dan **exactamente** el mismo resultado que el núcleo del commit `f2a0728`: se ha comparado la firma completa en 25 planos de prueba.
  - La ruta sin ventanas (`LayerPlanning`) da la misma red que la lógica del asistente.
  - El inventario cuadra en todos los planos legibles.
  - Hay pruebas de cotas, rótulos, redes conectadas y cuadre (sección 2i de las pruebas).
- **Botones nuevos, verificado solo por compilación:** todo lo que usa Revit:
  - `TransactionGroup` con una transacción por sistema;
  - forjados con `Solid.IntersectWithCurve`;
  - movimiento de redes;
  - cambio de tamaño;
  - informe.
- **Pestaña única RevitAutomation (25-11-2026):** compila sin errores ni avisos para Revit 2025, 2026 y 2027 contra la API de
  cada versión; pasan las pruebas del motor y las de cada herramienta (unas 830 comprobaciones de lógica); el instalador
  se ha probado en una carpeta de prueba: instala las tres versiones, quita los complementos antiguos y respeta los ajenos.
- **Pendiente de comprobar dentro de Revit:** que la pestaña y sus desplegables aparecen en 2025, 2026 y 2027; las
  herramientas copiadas en su versión nueva; la regla «sigue el techo» y sus codos; la posición sobre un CAD real
  vinculado, las uniones de bandeja y la colocación de familias genéricas.
