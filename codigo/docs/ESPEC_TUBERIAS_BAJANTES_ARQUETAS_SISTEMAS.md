# Especificación · Botón «Tuberías»: bajantes, arquetas y sistema de cada red

Fecha: 18-11-2026 · Análisis: Claude (solo lectura) · A implementar por: Codex

**REGLA PRINCIPAL: todo GENERAL.** Debe funcionar con planos de cualquier oficina.
- Prohibido depender de nombres concretos de capa (`P-PIPE`, `P-SANR-FIXT`…), de familias (`XX_…`) o de coordenadas de los planos de prueba.
- Los umbrales van en mm (con `UnitToMm`), como constantes con nombre.
- Los planos de prueba solo sirven para comprobar.

## 0. Contexto y reglas (leer antes)

- Leer `README.md`, `LEEME_VERSION_COMBINADA.txt` (novedades del 16-17/11), `TRASPASO_PROYECTO.txt`, `docs/ESPEC_TUBERIAS_PENDIENTES_SIN_UNIONES.md` y `Pruebas/RESULTADO_PENDIENTES_SIN_UNIONES_2026-11-17.txt`.
- **No romper lo que ya funciona:**
  - Tuberías NO crea uniones (codos y tes los pone el técnico).
  - Las pendientes se leen del plano (`SlopeAnnotations`/`SlopeSolver`).
  - Las reglas `CleanDrawing` de `OutlineDetector` son solo para conductos y bandejas.
  - Conductos, Bandejas, Luminarias y Equipos y PCI deben dar EXACTAMENTE lo mismo que antes. Comprobarlo con la firma o simulación por botón de los planos de prueba, antes y después; la única excepción es la arqueta eléctrica del punto 2.5.
- **La carpeta no es git.** Ya existe la copia de seguridad `C:\CAD-REVIT\CadMepRevit_Combinado_Prueba_copia_20261118`. Comprueba que es igual al código actual; si no, haz otra copia nueva con sufijo `_b`.
- **SDK .NET 10:** `%USERPROFILE%\.dotnet\dotnet.exe` (no está en el PATH). Revit no está instalado.
- **Compilar:** `dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true`. Debe dar 0 errores y 0 avisos en net8.0-windows y net10.0-windows.
- **Pruebas:** `dotnet run --project tests/CadMepRevit.Tests -c Release -- ejemplos`, con `CADMEP_PLANOS=C:\CAD-REVIT\Pruebas`. Hoy pasan 302.
- **NO ejecutar `instalar.cmd`.**
- No inventar API de Revit: si no compila, no existe.
- Mensajes en español que digan cómo corregir.
- Lo supuesto se marca SUPUESTO en el informe.
- Distinguir lo probado fuera de Revit de lo no verificado en Revit.

## 1. Bajantes y montantes (tuberías verticales dibujadas como CÍRCULO)

### Diagnóstico (medido con los planos de `Pruebas`)

En los planos exportados de Revit, una tubería vertical vista en planta es un **CIRCLE** en la capa de tuberías, normalmente varios concéntricos (tubo y embocadura). `CadReader` solo convierte LINE, LWPOLYLINE, POLYLINE y ARC: los CIRCLE se cuentan como «no convertidos».

| Plano | CIRCLE en capa de tubería | Posiciones distintas | En el EXTREMO de un tramo horizontal | Sobre un tramo | Sueltos |
|---|---|---|---|---|---|
| SANEAMIENTO_S1_COLGADA | 203 (Ø110×96, Ø40×50, Ø135×34, Ø32×20, Ø125×2) | 87 | 76 | 8 | 3 |
| SANEAMIENTO_S1_ENTERRADA | 100 (Ø125×55, Ø40×31, Ø110×14) | 12 | 8 | 0 | 4 |
| SANEAMIENTO_PB | 347 (Ø110×233, Ø40×108, Ø32×6) | 134 | 101 | 0 | 33 |
| FONTANERIA_PB | 116 | 35 | 33 | 0 | 2 |
| FONTANERIA_S1 | 65 | 18 | 13 | 5 | 0 |
| PCI_S1 | 14 (Ø38) | 7 | 7 | 0 | 0 |

SANEAMIENTO_PB es casi todo bajantes: hoy solo salen 110 trocitos que suman 14 m.

Las ELLIPSE de esas capas (1729 en la colgada, 2823 en la enterrada) son todas ARCOS, no elipses completas: son dibujo de piezas o pendientes, no bajantes. No hay que tocarlas.

### Solución (decidida con el usuario)

1. **Lectura.** `CadReader` guarda los CIRCLE en una lista nueva de `CadDrawing` (centro, radio, capa, índice de entidad), sin cambiar lo que ya produce. Revisar que el inventario y el cuadre (`ModelInventory`) los cuenten como modelados cuando se modelen.
2. **Candidatos.** Son círculos de capas que se modelan como Tubería. Los concéntricos (centros a menos de tolerancia + 5 mm) se agrupan en UNA bajante.
   - **Diámetro:** el del rótulo de tamaño asociado si lo hay (`SizeAnnotations`, mismas reglas). Si no, el **menor** del grupo, buscando el nominal cuyo exterior coincide, como ya se hace con las tuberías a doble línea.
   - **Descartar:** círculos de diámetro absurdo para una tubería (fuera de 10–400 mm, como `ForPipes`); círculos dentro de bloques; círculos de capas que no son de tubería.
3. **Geometría** (SUPUESTA y avisada en el informe):
   - **Base:** la cota del eje del extremo de la tubería horizontal donde está el círculo (la que ya calcula `SlopeSolver`, con pendiente). Si está sobre un tramo y no en un extremo, la cota de ese punto del tramo. Si está suelto, la cota de referencia de su red, o la por defecto (2500 colgada / −600 enterrada).
   - **Red colgada** (lo normal): la bajante SUBE desde ese punto hasta la cota del **nivel superior** (cota del nivel siguiente − cota del nivel elegido; sin nivel superior, 3000 mm supuestos).
   - **Planta con aparatos o red enterrada** (por ejemplo planta baja y enterrada): la bajante BAJA desde la cota del tramo hasta el **nivel inferior** (sin nivel inferior, −3000 mm supuestos).
   - **Cómo se decide subir o bajar (reglas generales, en este orden):**
     1. Un texto cercano (≤ 1500 mm) con BAJA / BAJANTE / «BAJA A» / DOWN → baja. SUBE / MONTANTE / «SUBE A» / UP → sube.
     2. Red enterrada (`SlopeSolver.IsBuried`) → sube (de la red enterrada hacia la planta).
     3. Red colgada sin texto → sube hasta el nivel superior (decisión del usuario).
     4. Planta sin red colgada ni enterrada identificada (solo aparatos y trocitos, como la planta baja) → baja hasta el nivel inferior (decisión del usuario).
   - Constantes con nombre en `PlanDefaults`/`PlacementConfig`.
4. **Revit.** Crear un tramo vertical con `Pipe.Create(doc, sistema, tipo, nivel, a, b)` con a y b en la misma X,Y y distinta Z. Mismo tipo y sistema que la red a la que llega. Sin uniones (las pone el técnico). El extremo comparte punto exacto con el extremo del tramo horizontal.
5. **Informe.** Sección «BAJANTES Y MONTANTES»:
   - cuántas se leyeron, modeladas, descartadas (y por qué);
   - para cada una: diámetro, cota inferior y superior, «SUPUESTA», si sube o baja y por qué regla.

## 2. Arquetas, pozos, sumideros, botes sifónicos, separadores

### Diagnóstico

Se reconocen bien: `BlockNames.Role` da `Sanitario` y `ModelScope` Tuberías los incluye.
- **Enterrada:** 28 `Arqueta_Paso` 400×400, 11 de 800×800, 2 sifónicas, 27 sumideros, pozos `ArquetaCircular` 1100, arqueta de bombeo, hidrocarburos, arenero, canaleta, separador de grasas y pozo de bombeo 1500×1500×2800.
- **Planta baja:** 128 + 28 sumideros lineales, 29 sumideros y 20 botes sifónicos.

Pero:
1. `BlockElevations.For` no trata `Sanitario`: cae en el caso por defecto, `HeightAboveLevelMm` = **2500 mm**, como valor no supuesto. Arquetas y sumideros salen a 2,50 m, en el techo.
2. Sus familias del plano no están ni en la biblioteca (`Pruebas\indice_familias.txt`: 0 coincidencias de `XX_MEP_Arqueta_Paso`, `XX_UNT_ArquetaCircular`, `XX_APS_Sumidero…`, `XX_APS_BoteSifonico`…) ni hay genérica de `Sanitario` en `BlockFamilies.GenericFamiliesFor`. Resultado: **marcador 3D de 100 mm SUPUESTOS**, a 2,5 m.
3. Si la familia SÍ está en el proyecto y necesita anfitrión (basada en suelo), en Tuberías no se sustituye (`BlockFamilies`: `substitute` solo para Luminarias y EquiposPci), y la colocación falla.
4. `XX_APE_ArquetaEnterrada` en la capa `E-ELEC-FIXT` es una arqueta ELÉCTRICA, pero la coloca Tuberías.

### Solución (decidida con el usuario: como FAMILIA)

1. **Familia.** Si no está la del bloque, usar una **familia genérica de arqueta de la biblioteca** (`FamilyLibrary`, bibliotecas en `%APPDATA%\CadMepRevit\bibliotecas.txt`; en el índice hay por ejemplo «Arqueta.rfa», «Arqueta cuadrada.rfa», «Arqueta cuadrada sin conexiones.rfa», «Arqueta de bombeo.rfa», «F-EVRO-Arqueta Sifonica.rfa», «F-EVRO-Arqueta Sumidero.rfa», «F-EVRO-Arqueta Acometida.rfa», «E-EVRO-Pozos.rfa»).
   - Elegir por palabras del nombre del bloque (SIFON → sifónica, BOMBEO → bombeo, POZO → pozo, SUMIDERO → sumidero, si no → arqueta) en `GenericFamiliesFor(Sanitario, …)`, con una lista ordenada de candidatas que se buscan por nombre en la biblioteca (`library.Ensure`).
   - Ajustar ancho, largo y profundidad si la familia tiene parámetros de tamaño; si no se puede, avisar.
   - Solo si no hay ninguna familia, marcador 3D, avisado.
2. **Familias con anfitrión.** Permitir la sustitución por una genérica colocable también en el botón Tuberías, solo para bloques `Sanitario`.
3. **Altura** (nueva regla en `BlockElevations` para `Sanitario`):
   - Arquetas, pozos, sumideros, sumideros lineales, botes, canaletas y separadores: a la **cota del suelo del nivel** (0 mm), con la tapa enrasada.
   - Inodoros, lavabos y demás aparatos: a 0 (apoyados en el suelo).
   - **Profundidad:** la del nombre («A1500xL1500xA2800mm» → 2800; `BlockNames.SizeInName`) o SUPUESTA (por ejemplo 600 mm arqueta, 1500 mm pozo), avisada.
   - Que el informe lo diga.
4. **Huella.** Los bloques ya tienen huella medida (`Footprint`): usarla para tamaño y giro.
5. **Arquetas eléctricas.** Un bloque «Arqueta» en una capa claramente eléctrica (prefijo E-, ELEC, BT, TELECOM…, como `LayerMapping` reconoce ya las capas eléctricas) es `Electrico` y lo coloca «Equipos y PCI», no Tuberías. Esta es la única diferencia admitida en las firmas de otros botones.

## 3. Sistema (y tipo) de cada red de tubería

### Diagnóstico

- `AutoPlanner` asigna el sistema de la capa con `ImportForm.SuggestSystem(layer, sistemas)`. Para «P-PIPE» ninguna regla coincide y devuelve **`names[0]`, el primer sistema por orden alfabético** (normalmente «Agua caliente…»). En el informe de ventilación del usuario pasó lo mismo: la capa «0» salió como «Aire de retorno».
- `AssignSystemsFromLabels` solo usa el PREFIJO pegado a un rótulo de tamaño («AFS Ø32»).
  - En saneamiento los rótulos van SUELTOS: «SAN.FEC» ×28/9, «SAN. PLUV» ×22/26, «DRENAJE» ×34. No se usan.
  - Aunque se usaran, «FEC» y «PLUV» no se reconocen (las reglas piden «FECAL», «PLUVIAL») y no hay regla de drenaje.
- Resultado: fecales, pluviales y drenaje salen con un sistema de AGUA.
- El tipo de tubería también es el primero alfabético con uniones completas (puede ser acero o cobre en vez de PVC).

### Solución (el usuario lo deja a nuestro criterio)

1. **Reglas por palabra** (capa, rótulo o archivo; palabras completas o abreviaturas, sin acentos, mayúsculas):
   - FECAL, FEC, RESIDUAL, SANEAM, SANIT, SAN, SANR, NEGRAS, WC → sistema sanitario (SANIT, RESIDUAL, SANITARY).
   - PLUVIAL, PLUV, STORM, STRM, LLUVIA → pluvial (PLUVIAL, STORM; si no hay, sanitario).
   - DRENAJE, DREN, DRAIN, DRN → drenaje: sistema cuyo nombre contenga DREN o DRAIN; si no, pluvial; si no, sanitario.
   - Las de agua y PCI actuales (AFS, ACS, RACS, PCI…) se mantienen.
2. **Por red** (`NetworkComponents`), en este orden:
   1. capa;
   2. rótulos con o sin tamaño asociados a sus tramos: texto en la capa de rótulos de tuberías cercano (≤ 1000 mm) al tramo, o con directriz, por mayoría;
   3. aparatos sanitarios o sumideros conectados a la red → sanitario;
   4. nombre del archivo (SANEAMIENTO → sanitario; PLUVIAL → pluvial; FONTANERIA → agua fría; PCI → incendios).
3. **Nunca más `names[0]` en silencio.** Si nada decide, el sistema más probable según el nombre del archivo o, si no, el de la capa, con aviso «sistema SUPUESTO».
4. **Tipo de tubería coherente con el sistema.** Para sanitario, pluvial y drenaje, preferir el tipo cuyo nombre contenga PVC, SANEAM o el material del rótulo; para agua y PCI, el actual. Siempre entre los que tienen preferencias de enrutamiento completas, como hoy. Avisar de la elección.
5. **Informe.** Por red: sistema elegido y por qué regla.

## 4. Pruebas (en `tests/CadMepRevit.Tests`, fuera de Revit)

**Sintéticas:**
- Círculos concéntricos en el extremo de un tramo → 1 bajante con el Ø menor.
- Círculo suelto → bajante con cota de red.
- Texto «BAJA» o «SUBE» → sentido.
- Red enterrada → sube; red colgada → sube hasta el nivel superior; planta sin red → baja.
- Bloque «Arqueta …» → Sanitario a cota 0 con profundidad del nombre.
- «Arqueta» en capa eléctrica → Eléctrico, de Equipos y PCI.
- Capa «P-PIPE» + rótulos «SAN.FEC» → sanitario.
- «SAN. PLUV» → pluvial.
- «DRENAJE» → drenaje/pluvial.
- Sin pistas → nunca el primero alfabético sin aviso.

**Planos reales:** cifras antes y después de bajantes, arquetas y sistemas por red en los 6 planos de la tabla del punto 1.

**No regresión:**
- Pendientes (30/30 y 70/70 etiquetas; extremos compartidos con diferencia 0).
- Tuberías sin uniones.
- Firmas de los otros botones iguales, salvo la arqueta eléctrica.

## 5. Entrega

1. Compilar 2025 y 2027 con 0/0. Todas las pruebas superadas.
2. Actualizar la carpeta `C:\CAD-REVIT\COMPILADO_CadMepRevit_2026-11-17`:
   - copiar `CadMepRevit.dll`, `.pdb`, `CadMepRevit.Core.dll`, `.pdb`, `ACadSharp.dll` y `CadMepRevit.deps.json`;
   - de `src/CadMepRevit/bin/Release/net8.0-windows` a «Revit 2025/CadMepRevit»;
   - de `net10.0-windows` a «Revit 2027/CadMepRevit»;
   - no tocar `instalar`/`desinstalar` ni los `.addin`;
   - actualizar su `LEEME.txt` (sección QUÉ CAMBIA).
3. Añadir una sección a `LEEME_VERSION_COMBINADA.txt`.
4. Informe en `Pruebas\RESULTADO_TUBERIAS_BAJANTES_ARQUETAS_2026-11-18.txt`:
   - causa;
   - cambios;
   - cifras antes y después por plano;
   - lo pendiente de verificar en Revit: `Pipe.Create` vertical, colocación y profundidad de las familias de arqueta, sistemas y tipos elegidos.
