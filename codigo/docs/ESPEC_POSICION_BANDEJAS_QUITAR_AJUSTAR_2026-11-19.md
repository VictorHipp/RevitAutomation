# Encargo · 19-11-2026: familias sobre el CAD, codos en bandejas, sin «Ajustar dimensiones»

Proyecto: `CadMepRevit_Combinado_Prueba`, un complemento de Revit 2025 (.NET 8) y 2027 (.NET 10) en C#.

Antes de empezar, lee:
- `README.md`, `LEEME_VERSION_COMBINADA.txt` y `TRASPASO_PROYECTO.txt`;
- las especificaciones anteriores de `docs/`, para no romper las pendientes, el modelado sin uniones de Tuberías y
  Conductos ni las bajantes y arquetas.

## Reglas del encargo (obligatorias)

- **Reglas generales.** Cada botón tiene que funcionar con cualquier plano, de cualquier oficina. Nada ajustado a los
  planos de prueba: ni nombres de archivo, ni capas concretas, ni coordenadas.
- **Copia de seguridad antes de tocar nada:** `CadMepRevit_Combinado_Prueba_copia_20261119`, con `robocopy` (hay rutas
  de más de 260 caracteres). Comprueba que coincide.
- **No ejecutes `instalar.cmd`.**
- **No escribas en unidades de red**: solo lectura.
- **SDK:** `%USERPROFILE%\.dotnet\dotnet.exe` (no está en el PATH; para ejecutar, `DOTNET_ROOT` = esa carpeta).
- **Compilación:** `dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true` debe salir con 0 errores y
  0 avisos. Esa orden genera las versiones 2025 y 2027.
- **Pruebas:** `dotnet run --project tests/CadMepRevit.Tests -c Release -p:BuildForRevit2027=true --no-build`, con
  `CADMEP_PLANOS=C:\CAD-REVIT\Pruebas`. Al empezar pasan 406; al acabar deben pasar todas.
- **Comparación antes y después en todos los planos de `Pruebas\`.** Para cada botón y cada plano, compara qué capas
  modela, qué bloques coloca, la función de cada bloque y los tramos y uniones planificados. Solo puede cambiar lo que
  pide este encargo; cualquier otra diferencia es un fallo. La herramienta de comparación del 18-11 está en
  `rolecmp`, en el scratchpad de Claude. Si no la tienes, haz una equivalente.
- **Si algo de este documento no se puede hacer como dice, o hay una duda, para y pregunta.** No lo des por hecho.

## 1. Luminarias y Equipos y PCI: colocar siempre sobre el CAD

El usuario no pide elementos nuevos. Ya están en su sitio:
- **Luminarias:** luminarias, interruptores, conmutadores, cruces, pulsadores de luz y detectores de presencia
  (`ModelScopes.Includes`: `Luminaria`, `MandoIluminacion`).
- **Equipos y PCI:** extintores, BIE, detectores, pulsadores, sirenas, señales, rociadores, cuadros, tomas y
  dispositivos.
- **Familia genérica de respaldo:** `BlockFamilies.GenericFamiliesFor`. Las familias que nombra existen en la
  biblioteca de la empresa (comprobado el 19-11 en
  las carpetas de familias de la oficina).

Lo que pide: que esas familias queden **encima del plano CAD tal y como está en Revit**, esté donde esté el origen del
proyecto. Esto ya está hecho en parte:
- `Revit/CadPlacement.cs` y `Core/CadRegistration.cs` (18-11) sitúan el plano encajando los puntos del dibujo con los
  vértices del CAD vinculado;
- `MepBuilder` lleva cada bloque al proyecto con `Map(...)`, que usa `plan.CadToProject`, y gira con `rotOffset`.

Tareas:

1. **Revisa que TODOS los caminos de colocación usan `Map`/`ToXyz`, sin excepción**, y anota el resultado en el
   informe del encargo. Los caminos son:
   - tramos, bajantes, pendientes y muros;
   - familias por nivel, por punto o por plano horizontal (basadas en cara);
   - arquetas y su `SizeChamber`;
   - marcadores 3D (`DirectShape`);
   - consulta de techos (`CeilingFinder`);
   - detección de «ya colocado» (`ExistingBlocks`);
   - conexión automática;
   - luminarias dibujadas con líneas (`LooseFixtures`, que entran como bloques).

   Ningún sitio puede construir un `XYZ` en pies a partir de coordenadas del plano sin pasar por `Map`.
2. **Planos con muchos bloques y pocas líneas** (PCI y alumbrado). `CadPlacement` solo usa los extremos de las líneas
   rectas (`ArcId == 0`) y los centros de círculos. Hoy todos los planos de prueba tienen bastantes (el que menos, PCI
   planta baja, 71 puntos). Pero un plano hecho solo de bloques no tendría ninguno y se modelaría con las coordenadas
   del DWG. Añade también los **puntos de inserción de los bloques**:
   - **Del dibujo:** `drawing.Blocks` de profundidad 0.
   - **Del CAD en Revit:** recorre la geometría acumulando la transformación de forma explícita. Usa
     `GeometryInstance.GetSymbolGeometry()` y multiplica `T = Tpadre * gi.Transform` en cada nivel. El punto de
     inserción de cada bloque anidado es `T.Origin` de su `GeometryInstance`, y los vértices son `T.OfPoint(p)`.
   - **No uses `GetInstanceGeometry()` en los niveles anidados.** No está comprobado en qué coordenadas da sus
     transformaciones hijas.
   - Revisa también que `CadPoints` produce los mismos vértices con este recorrido.
3. **Pruebas fuera de Revit** (`tests/PlacementTests.cs`):
   - un plano sintético que solo tenga bloques, desplazado y girado: debe encontrarse;
   - con los planos reales `PCI_PB.dwg` e `ILUMINACION_PB.dwg`, simulando el vínculo centro a centro
     y girado, la posición sale a menos de 2 mm.
4. **En el informe de cada modelado**, la línea «Posición:» debe decir:
   - cuántos puntos de líneas y cuántos de bloques se han usado;
   - cuántos caen sobre el CAD.

   Así el usuario puede comprobarlo en Revit.

## 2. Bandejas: codos, tes y cruces en las bandejas; ninguna unión en los tubos eléctricos

Hoy el botón «Bandejas» intenta crear codos, tes, cruces y transiciones en las bandejas Y en los tubos eléctricos
(`ModelScopes.CreatesAutomaticFittings` devuelve true para Bandejas).

Lo que pide el usuario:
- **Bandejas de cables** (`MepKind.Bandeja`): uniones como ahora (codos, tes, cruces y reducciones).
- **Tubos eléctricos** (`MepKind.TuboElectrico`): solo tramos rectos, sin ninguna unión, igual que Tuberías y
  Conductos.

Tareas:

1. **Una propiedad nueva en `BuildPlan`**, `ConduitsWithoutFittings`, que `AutoPlanner` pone a true cuando el botón es
   Bandejas.
   - `WithoutFittings(kind)` la tiene en cuenta.
   - Los encuentros de tubos no se intentan y no salen como problema en el informe. Se explican igual que en
     Tuberías: «los encuentros los resuelve el técnico».
   - `FittingSetup` no completa las uniones de los tipos de tubo eléctrico en ese botón.
2. **Codos en bandejas.** Nadie ha visto todavía si salen dentro de Revit, y no hay plano real con bandejas: el
   usuario ha elegido **solo plano sintético**. Crea en `samples/` un DXF con:
   - bandejas a línea simple y a doble línea (capas con nombres de bandeja reconocibles, como «BANDEJA», «E-CABL-TRAY»);
   - esquinas en L, una te y una cruz;
   - al menos una esquina dibujada con arco o chaflán (es habitual en bandejas a doble línea).

   Comprueba con `NetworkBuilder` (Core) que salen:
   - un `Codo` en cada L, una `Te` y una `Cruz`;
   - con los tramos que llegan a ellos lo bastante largos.

   Si alguna no sale, corrige la detección para bandejas **sin cambiar lo que planifican tuberías y conductos**: la
   comparación antes y después lo tiene que demostrar. Las familias de unión de bandeja (`M_Codo horizontal de canal`,
   `M_Curvatura horizontal de escalera`, `M_Te horizontal de canal`…) existen en la biblioteca de la empresa.
3. **Diagnóstico para Revit.** El informe ya incluye una línea por unión, «Unión Codo … CREADA / NO creada → motivo».
   Comprueba que también sale para bandejas, para poder diagnosticar dentro de Revit.

## 3. Quitar «Ajustar dimensiones» por completo

Los botones de modelar ya aplican las medidas y las pendientes al modelar, y así deben seguir:
- `AutoPlanner.AssignSizesFromLabels` usa `SizeAnnotations.Associate`, con las cotas DIMENSION y los rótulos;
- las bajantes usan su rótulo;
- Tuberías usa `SlopeSolver`.

«Ajustar dimensiones» no hace nada que no haga ya el modelado. Hay que quitar:

- **El botón de la cinta** (`App.cs`).
- **El comando:** `Commands/AdjustDimensionsCommand.cs`.
- **El registro del modelado:**
  - `Revit/ModelRecord.cs`;
  - `ModelRecordStore`;
  - `BuildRecord` y `SaveMerged` en `ModelFromCadCommand`;
  - el aviso «… «Ajustar dimensiones» no encontrará este modelo».

  Comprueba antes con búsquedas que no lo usa nadie más. Si algo más lo usa, para y pregunta.
- **`Commands/ChangeDiameterCommand.cs`.** No está en la cinta y solo lo usaba `AdjustDimensionsCommand`
  (`DiameterParameter`, `WidthHeightParameters`). El usuario ha decidido borrarlo (19-11). Antes, comprueba con
  búsquedas que nada más lo usa; si algo lo usa, para y pregunta.
- **Todas las menciones a «Ajustar dimensiones»** en:
  - `ModelReport`, `ModelInventory`, `CadReader`, `AutoPlanner` y `MepBuilder`;
  - textos de ayuda;
  - `README.md`, `LEEME_VERSION_COMBINADA.txt`, `docs/Guia_CadMepRevit.txt` y `TRASPASO_PROYECTO.txt`.

  Deja los informes antiguos de `Pruebas\` como están: son históricos.
- **Las pruebas** de `tests/CadMepRevit.Tests/Program.cs` que dependan del registro o del comando. Explica en el
  informe cuáles se quitan y por qué.

Hay que mantener:
- el DWF opcional al modelar Tuberías, Conductos y Bandejas (`MergeDwf`), que no depende de este botón;
- el desinstalador. Si `desinstalar.cmd` o `instalar.cmd` nombran el registro, no hace falta tocarlos. Los archivos de
  registro que ya existan en `%APPDATA%\CadMepRevit` en el equipo del usuario se quedan como están: no se borran.

**Criterio de aceptación:** con los planos de prueba, cada botón planifica las mismas medidas (`SegmentSizes`), los
mismos sistemas y las mismas pendientes que antes del cambio. Demuéstralo con la comparación antes y después.

## 4. La capa «CUADROS ELECTRICOS» no es tubo eléctrico

En `ALUMBRADO_DIALUX.dwg`, `MepKinds.Suggest("CUADROS ELECTRICOS")` da `TuboElectrico` (80 tramos), y Bandejas los modelaría
como tubos. Son el dibujo de los cuadros.

- **Regla general** en `LayerMapping`: una capa cuyo nombre indica cuadros o paneles eléctricos (palabras completas
  CUADRO/CUADROS, PANEL/PANELS, CGP, CGMP, «SWITCHBOARD», o el sufijo -PANL de AIA, como «E-POWR-PANL») no es tubo ni
  bandeja: es `NoImportar` para los tramos.
- **Sus bloques** los sigue colocando «Equipos y PCI», como ahora.
- **Comparación antes y después:** en todos los planos solo pueden cambiar capas de ese tipo.
- **Pruebas:** añade una prueba con varios nombres de capa, por ejemplo:
  - no son tubo: «CUADROS ELECTRICOS», «E-POWR-PANL», «CUADRO GENERAL»;
  - siguen siendo lo que eran: «E-POWR-CNDT», «BANDEJA», «TUBO CUADRO»;
  - si una capa como «TUBO CUADRO» es dudosa, explica en el informe qué decide la regla y por qué.

## Al terminar

1. Compila: 0 errores y 0 avisos. Pasa las pruebas: todas.
2. Copia las DLL y los PDB a `CAD-REVIT\COMPILADO_CadMepRevit_2026-11-17\Revit 2025\CadMepRevit\` y
   `...\Revit 2027\CadMepRevit\`, y comprueba los hashes. Revisa que `instalar.cmd` y `desinstalar.cmd` de esa carpeta
   no nombren nada que se haya quitado.
3. Actualiza el `LEEME.txt` del compilado y `LEEME_VERSION_COMBINADA.txt`, con lo que cambia y con lo que **NO** se ha
   podido verificar dentro de Revit:
   - la posición con el CAD real vinculado;
   - las uniones de bandeja creadas por Revit;
   - las familias genéricas colocadas.
4. Deja el informe en `CadMepRevit_Combinado_Prueba\Pruebas\RESULTADO_POSICION_BANDEJAS_AJUSTAR_2026-11-19.txt`, con:
   - lo hecho en cada punto, por archivo;
   - la comparación antes y después;
   - las pruebas nuevas;
   - lo que queda por comprobar en Revit, diciendo qué línea del `ultimo_informe.txt` lo muestra.
