# Especificación · Botón «Tuberías»: sin uniones y con pendientes del plano

Fecha: 17-11-2026 · Autor del análisis: Claude (solo lectura) · A implementar por: Codex

**REGLA PRINCIPAL: todo debe ser GENERAL.** Debe funcionar con planos de cualquier oficina, no solo con los de prueba.
- Prohibido depender de nombres concretos de capa (`G-ANNO-DIMS`, `P-PIPE`…), de valores concretos (1 %, 1,25 m…) o de coordenadas.
- Los planos de prueba sirven para comprobar, no para definir reglas.
- Los umbrales van en mm (convertidos con `UnitToMm`) o relativos a la altura del texto, y siempre como constantes con nombre en un único sitio.

## 0. Contexto y reglas del proyecto (leer antes)

- Leer `README.md`, `LEEME_VERSION_COMBINADA.txt`, `TRASPASO_PROYECTO.txt` (apartado 8, «Consejos») y `docs/INSPECCION_DOS_BOTONES.md`.
- La carpeta **NO es un repositorio git**. Antes de tocar nada, copiar `src/` y `tests/` a `..\CadMepRevit_Combinado_Prueba_copia_20261117\`.
- **SDK .NET 10:** `%USERPROFILE%\.dotnet\dotnet.exe` (no está en el PATH).
- **Revit:** no está instalado en este PC.
  - Compilar con el paquete NuGet que ya prevé el csproj (2025: `Nice3point.Revit.Api` 2025.4.60).
  - Para 2027: `-p:BuildForRevit2027=true`.
  - **No ejecutar `instalar.cmd`.**
- **Pruebas:** `dotnet run --project tests/CadMepRevit.Tests -c Release -- ejemplos`.
  - Planos reales en `C:\CAD-REVIT\Pruebas`.
  - Pasar esa ruta con la variable `CADMEP_PLANOS` o añadirla a la búsqueda.
- **Reglas del proyecto:**
  - No inventar API de Revit: si no compila, no existe.
  - No inventar medidas. Lo supuesto se marca como SUPUESTO en el informe.
  - Mensajes en español que digan qué falló y cómo corregirlo.
  - Distinguir lo probado fuera de Revit de lo no verificado en Revit.
- **Alcance:** solo cambia el botón **Tuberías** (`ModelPipesCommand`, `ModelScope.Tuberias`). Conductos, Bandejas, Luminarias y Equipos/PCI deben dar **exactamente** lo mismo que antes. Comprobarlo con la simulación por botón de los planos de prueba, antes y después.

## 1. Quitar TODAS las uniones de tuberías

- **`Revit/MepBuilder.cs`:** no crear codos, tes, cruces ni transiciones cuando la unión es de tramos `MepKind.Tuberia`. Conductos, bandejas y tubos eléctricos siguen igual.
- **Preparación del proyecto:** `ModelFromCadCommand.PrepareProject` y `FittingSetup.Complete` no deben cargar ni asignar piezas de unión a los tipos de tubería cuando el botón es Tuberías.
- **Geometría:** cada tramo recto termina exactamente en el punto de encuentro, compartido con el tramo vecino (mismo X, Y y Z). Así el técnico puede poner la pieza en Revit. No acortar ni solapar tramos.
- **Informe:**
  - Las uniones de tuberías ya no son «no creadas» ni «no admitidas».
  - Una sola línea: «Uniones de tubería: no se crean; las pone el técnico (N encuentros: X codos, Y tes, Z cruces)».
  - El cuadre del inventario debe seguir cuadrando.
- **Conexión automática** con equipos y aparatos, en tuberías:
  - Solo conectar si el conector ya coincide con el extremo del tubo (distancia ≤ tolerancia de unión y misma dirección).
  - **Nunca estirar, mover ni inclinar** una tubería, porque rompería la pendiente.
  - Las conexiones no hechas se listan en el informe.

## 2. Pendientes leídas del plano

### 2.1 Cómo vienen en los planos de prueba (solo como referencia; NO codificar estos valores)

Comprobado con ACadSharp 3.8.35 en `Pruebas\SANEAMIENTO_S1_COLGADA.dwg` (30 etiquetas) y `Pruebas\SANEAMIENTO_S1_ENTERRADA.dwg` (70 etiquetas). Son etiquetas de pendiente exportadas de Revit:

- **Texto:** MTEXT «1%», «2.5%», «0.5%», «0.52%», «0.21%», «2%». Altura 0,25 y giros 0°, ±45°, 90°, −69,8°, 61,5°…
- **Flecha:**
  - un trazo LINE de 1,25;
  - una punta formada por 3 LINE cortas (0,378 / 0,099 / 0,378) y un HATCH sólido, todo en la misma capa de anotación;
  - la punta (el vértice del triángulo) coincide con un extremo del trazo, y ese extremo es hacia donde baja el agua.
- **Distancias:** el texto queda a ≤ 0,52 de su flecha. La flecha es paralela a la tubería y está a 0,06–0,87 de su borde.
- **Tuberías:** a doble línea, con Z = 0.
- **Cotas de altura:** ninguna en estos dos planos. En `FONTANERIA_S1.dwg` aparece el formato `h: -0.62 m` y `h: 1.00 m` (MTEXT en la capa de rótulos de tubería).
- **Lector actual:** `CadReader` ya guarda TODAS las LINE y LWPOLYLINE como `CadSegment` con su capa (las de capas de anotación no se construyen, pero están). Los HATCH, SOLID y bloques de flecha hoy no se guardan como geometría.

### 2.2 Lectura general de etiquetas de pendiente (núcleo, sin Revit: `Core/SlopeAnnotations.cs`)

**Textos de pendiente.** Fuentes: TEXT, MTEXT, atributos y textos de bloque, MLEADER (las mismas que `SizeAnnotations`, y más textos del DWF si lo hay).
- **Formatos admitidos:**
  - «1%», «1 %», «2.5%», «2,5 %»;
  - «i=2%», «i: 2 %», «P=1,5%», «Pte. 2%», «pendiente 2%»;
  - «2‰» (por mil);
  - las dos comas decimales.
- **Rango válido:** de 0,1 % a 15 %. Fuera de rango o sin flecha asociada: no se usa, y el informe lo dice si estaba cerca de una tubería.
- Un mismo texto con diámetro y pendiente («DN110 2%») alimenta a los dos lectores.

**Flechas.** Se reconocen por **geometría**, en cualquier capa que no se construya y sea de anotación, o en la propia capa del texto. Formas admitidas:
- **(a)** Trazo recto con una punta en un extremo, formada por:
  - dos o más segmentos cortos que se unen en ese extremo, o
  - un triángulo cerrado (LWPOLYLINE, SOLID o HATCH) cuyo vértice coincide con ese extremo.
- **(b)** LWPOLYLINE con ancho variable (cabeza de flecha por ancho).
- **(c)** LEADER y MLEADER: su punta de flecha.
- **(d)** Bloque (INSERT) cuyo contenido es una flecha (trazo + punta), transformado a coordenadas del dibujo.

Sentido: del extremo sin punta hacia la punta, que es aguas abajo. Las longitudes se miden en mm. La punta debe ser corta respecto al trazo (por ejemplo, < 0,6 × trazo). No exigir valores absolutos de los planos de prueba.

Si hace falta, ampliar `CadReader` para guardar las puntas (HATCH y SOLID triangulares, anchos de polilínea y geometría de bloques de flecha) en una lista nueva de `CadDrawing`, por ejemplo `Arrows`. **Sin cambiar** lo que ya produce: tramos, bloques, textos e inventario. Comprobarlo con la firma de los planos.

**Asociaciones.** Reutilizar el estilo de `SizeAnnotations`: el más cercano gana, el segundo debe estar al menos 1,5 veces más lejos y, si no, hay duda.
- **Texto → flecha:**
  - la flecha más cercana, a ≤ max(1000 mm, 6 × altura del texto);
  - si el texto tiene directriz, vale la flecha o la tubería que señala.
- **Flecha → tramo de tubería:**
  - tramos cuya dirección forma < 5° con la flecha;
  - que se solapan a lo largo de su recorrido;
  - a ≤ 1000 mm del eje.
- **Duda:** si dos tubos quedan casi igual de cerca, no se aplica y el informe lo dice.
- **Textos sin flecha:** si hay un texto de pendiente junto a una tubería pero sin flecha, se queda horizontal y se avisa.

### 2.3 De etiquetas a cotas (núcleo, sin Revit: `Core/SlopeSolver.cs`)

Datos de entrada: la red ya preparada de la capa (`NetworkBuilder`: tramos y uniones) y los componentes conectados (`NetworkComponents`).

1. **Recorridos.** Un recorrido es una cadena de tramos unidos por codos o en línea, hasta una te, una cruz o un extremo libre (la misma noción de recorrido que usa la propagación del diámetro). Una etiqueta vale para **todo su recorrido**.
2. **Votación por recorrido:**
   - **Sentido.** Cada flecha vota un sentido a lo largo del recorrido.
     - Si hay mayoría clara y solo 1 voto en contra (con al menos 3 votos), el voto en contra se descarta como error de dibujo y se anota.
     - Si hay empate, o más de un voto en contra: el recorrido queda **horizontal** y se avisa.
   - **Valor.** Si las etiquetas de un recorrido dan valores distintos, cada tramo usa la etiqueta de su propio tramo y, si no tiene, la más cercana a lo largo del recorrido. Se anota.
3. **Recorridos sin etiqueta:** horizontales, y se avisa.
4. **Cotas por red.** En cada componente conectado:
   - **Punto de partida:** se elige una referencia (ver 2.4) y se propaga con cota(aguas arriba) = cota(aguas abajo) + pendiente × longitud en planta. Los tramos horizontales conservan la cota.
   - **Una sola cota por nudo:** las cotas son únicas en cada nudo, para que los extremos que se tocan coincidan.
   - **Sentidos incompatibles en los nudos:** se detectan, por ejemplo una te a la que todos los ramales llegan «saliendo», o dos salidas sin unión posible. Esa red queda **horizontal** y se avisa.
   - **Anillos:** si un anillo no cierra (diferencia > 10 mm), esa red queda **horizontal** y se avisa.
   - **Desnivel en un nudo:** si un recorrido horizontal une dos nudos que la pendiente obliga a tener a cotas distintas, se respeta la pendiente de los etiquetados, el horizontal toma la cota del lado aguas abajo y se avisa del desnivel.
5. Todo esto es determinista y se prueba sin Revit.

### 2.4 Cota de referencia

**Cotas escritas en el plano.** Si las hay, se leen y mandan.
- **Formatos:**
  - «h: -0.62 m», «h=1,00», «Z=+2,50», «cota +1.20», «C.E.», «CT», «C.I.», «cota inferior», «solera», «fondo», «generatriz inferior»;
  - con o sin unidad (m o mm).
  - Sin unidad: si |valor| < 50 se toma en m; si no, en mm. Se anota.
- **Significado:**
  - se toman como relativas al nivel elegido;
  - por defecto indican el **eje** del tubo;
  - si el texto dice inferior, solera, fondo, C.I. o «generatriz inferior», indican la parte inferior: eje = cota + diámetro exterior / 2.
- **Asociación:** al tramo más cercano, con la misma regla que los rótulos. La cota fija el punto del tramo más cercano al texto o a la punta de su directriz.
- **Varias cotas en la misma red:**
  - la más aguas abajo fija la red;
  - las demás se comprueban;
  - si difieren > 20 mm de lo calculado, se avisa con las dos cifras (no se fuerza nada).

**Sin cotas en el plano** (es el caso de los dos planos de saneamiento de prueba). Valores SUPUESTOS, que el informe marca para revisar:
- **Red colgada (por defecto):** el punto más alto de cada red (su inicio aguas arriba) con el eje a `PlanDefaults.HeightAboveLevelMm` (2500 mm, la altura de siempre) sobre el nivel; desde ahí baja.
- **Red enterrada:**
  - Se detecta como tal si el nombre de la capa, el del archivo o un texto de leyenda o título contiene ENTERRAD, BURIED o «RED ENTERRADA».
  - Detectarlo así sirve para cualquier plano, no para uno concreto.
  - El punto más alto de cada red se coloca con el eje a `BuriedTopAxisMm` = −600 mm, como constante con nombre, SUPUESTO.
- Las dos constantes van juntas en `Config/PlacementConfig.cs` o en `PlanDefaults`, con un comentario.

### 2.5 Aplicar en Revit (`Revit/MepBuilder.cs`)

- Crear cada tubería con `Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, a, b)`, con `a.Z ≠ b.Z` según el resultado de `SlopeSolver`. No escribir el parámetro de pendiente: Revit la deduce de la geometría.
- **No verificado en Revit:**
  - comprobar en el diagnóstico la pendiente que Revit da a cada tramo (de 1 a 3 muestras por red);
  - anotarla junto a la prevista.
- Las tuberías sin pendiente siguen exactamente como ahora.

## 3. Medidas al modelar (sin necesidad de «Ajustar dimensiones»)

- El modelado ya aplica los rótulos Ø/DN (`AutoPlanner`, alrededor de la línea 327). Debe aplicar lo mismo que hoy aplica `AdjustDimensionsCommand`:
  - cotas DIMENSION;
  - rótulos;
  - la medida para todo el recorrido hasta la siguiente te;
  - textos del DWF.
- Hacerlo **antes** de crear las tuberías, para que el cálculo de cotas use ya el diámetro definitivo.
- Reutilizar `SizeAnnotations`; no duplicar lógica.
- Mantener el botón «Ajustar dimensiones» en la cinta como repaso opcional. Sobre un modelo nuevo no debe cambiar nada y debe decir «0 cambios».
- Si `AdjustDimensionsCommand` cambia el diámetro de una tubería con pendiente, debe conservar las cotas de sus extremos (no recolocar en Z).

## 4. Informe (`Revit/ModelReport.cs` y `ultimo_informe.txt`)

Nueva sección **«PENDIENTES»**:
- Recuentos: etiquetas leídas, asociadas, descartadas (con su motivo), recorridos con pendiente, recorridos horizontales sin etiqueta y redes dejadas horizontales por conflicto.
- Para cada red: referencia usada (cota del plano o SUPUESTA, con su valor) y cota del eje en su punto más alto y en su salida.
- Lista de avisos con coordenadas, con el formato de los avisos actuales.

## 5. Pruebas (en `tests/CadMepRevit.Tests`, todas fuera de Revit)

**Sintéticas** (generar DXF como ya hacen las pruebas):
- Flecha de cada forma: (a) líneas, (a) triángulo cerrado, (b) polilínea con ancho, (c) directriz, (d) bloque.
- Textos «2%», «i=1,5 %», «2‰».
- Red en L y en T:
  - cotas correctas en los nudos;
  - extremos compartidos;
  - recorrido sin etiqueta horizontal.
- Votación:
  - 3 a favor y 1 en contra: se ignora el voto en contra y se avisa;
  - 1 a 1: queda horizontal.
- Errores de red: anillo que no cierra y te con todos los ramales saliendo, los dos horizontales.
- Cotas «h: -0.62 m» y «C.I. 1,20»: el eje correcto en cada caso.
- Plano en m y en mm: mismo resultado.
- Texto «100%» de una leyenda, sin flecha: no se usa.
- Sin uniones de tubería: el plan de Tuberías no crea uniones; Conductos sigue creándolas.

**Planos reales** (`Desktop\CAD-REVIT\Pruebas`):
- RED COLGADA: 30 de 30 etiquetas asociadas.
- RED ENTERRADA: 70 de 70 etiquetas asociadas.
- Cotas coherentes en cada nudo (diferencia 0).
- Informar de los recorridos horizontales y de los conflictos encontrados.

**No regresión:**
- El resto de planos de `Pruebas` da 0 pendientes inventadas.
- Conductos, Bandejas, Luminarias y Equipos/PCI dan la misma firma que antes del cambio.

## 6. Entrega

- Compilar 2025 y 2027 con 0 errores y 0 avisos. Pasar todas las pruebas.
- Escribir `Pruebas\RESULTADO_PENDIENTES_SIN_UNIONES_2026-11-17.txt` con:
  - resultados por plano;
  - qué está probado fuera de Revit;
  - qué queda por verificar en Revit: pendiente real de `Pipe.Create`, conexión sin estirar y comportamiento de «Ajustar dimensiones» con pendiente.
- Actualizar `README.md`, `LEEME_VERSION_COMBINADA.txt` y `docs/Guia_CadMepRevit.txt` (sección Tuberías).
