# CAD MEP para Revit · Inspección previa al rediseño en dos botones

Fecha: 13-11-2026 · Rama `main` (último commit `f2a0728`) · Solo lectura: este documento no cambia código ni configuración.

## 0. Estado comprobado

| Comprobación | Resultado |
|---|---|
| Compilación `dotnet build CadMepRevit.sln -c Release` (SDK 10.0.404) | Correcta: Revit 2025 (`net8.0-windows`) y Revit 2027 (`net10.0-windows`), 0 avisos, 0 errores |
| Pruebas `tests/CadMepRevit.Tests` (fuera de Revit) | Todas superadas, también con los planos reales de `planos_reales` |
| Pruebas dentro de Revit | **No ejecutadas** en esta sesión |
| Cambios sin commit | `ImportCadCommand.cs`, `MepBuilder.cs`, `RevitCatalog.cs`, `ImportForm.cs`, `HelpText.cs`, `Guia_CadMepRevit.txt`, `Headless.cs` (nuevo) y `Pruebas/`, de una sesión anterior (12-11). Compilan y pasan las pruebas |

## 1. Cómo se lanza

1. `instalar.cmd` (Revit cerrado): pasa las pruebas, compila en Release y copia el complemento en `%APPDATA%\Autodesk\Revit\Addins\<2025|2027>\CadMepRevit\` con su `.addin`.
2. En Revit, pestaña **CAD MEP**, panel **CAD → MEP** (`App.cs`), hay 3 botones:

| Botón | Clase | Qué hace |
|---|---|---|
| Importar CAD y construir MEP | `Commands/ImportCadCommand` | Asistente de 3 pasos: archivo → mapeo → previsualización → construir |
| Cambiar diámetro | `Commands/ChangeDiameterCommand` | Pide en mm el diámetro, o el ancho y el alto, de los elementos seleccionados |
| Ayuda | `Commands/HelpCommand` | Texto de ayuda y carpeta del plano de ejemplo |

3. Sin ventanas existe `Headless.BuildFromCad(doc, ruta, opciones)` (sin commit). Construye con lo que propondría el asistente. No tiene botón: es para pruebas desde una macro.

## 2. Flujo del botón «Importar CAD y construir MEP»

1. **Requisitos previos** (solo lectura): proyecto abierto, que no sea una familia ni sea de solo lectura; al menos 1 nivel y algún tipo MEP. Si no hay tipos de tubería ni de conducto, pide confirmación para seguir solo con muros y bloques.
2. **Paso 1**: elegir DWG o DXF.
3. **Lectura** (`Core/CadReader`, ACadSharp 3.8.35, solo espacio modelo):
   - Convierte en tramos rectos: LINE, LWPOLYLINE, POLYLINE 2D/3D y ARC (en cuerdas de 90° como máximo; las curvas de hasta 100° guardan su esquina para hacer de codo).
   - Guarda los INSERT del nivel superior como bloques con su huella real.
   - Avisa de las XREF sin cargar.
   - **El resto de entidades (TEXT, MTEXT, DIMENSION, HATCH, CIRCLE…) no se leen: solo se cuentan.**
4. **Paso 2** (`Ui/ImportForm`): ajustes generales, tabla de capas y tabla de bloques (ver apartado 3). Se valida y se señala la celda con el problema.
5. **Preparación del plan** (`ImportForm.CreatePlanOrReport`):
   - En las capas a doble línea, las líneas se sustituyen por los ejes de `OutlineDetector`.
   - `NetworkBuilder` limpia, parte en las tes, une colineales y clasifica las uniones.
   - Los muros no llevan piezas de unión.
6. **Paso 3** (`Ui/PreviewForm`): dibujo y resumen; el usuario confirma.
7. **Confirmación final**, y después `MepBuilder.Build` (apartado 4).
8. **Informe**: ventana final, `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt` y una copia con fecha en `informes\`.

## 3. Configuración existente (valores que el código fija)

No hay archivos de configuración (JSON, INI…). Toda la configuración son **constantes y propuestas en el código**. El usuario puede cambiar muchas en el Paso 2.

### 3.1 Ajustes generales (Paso 2)

| Ajuste | Valor que propone el código | Dónde | ¿Se edita en la ventana? |
|---|---|---|---|
| Unidades | `$INSUNITS` (mm, cm, m, in, ft). Si no se reconoce, **mm** | `CadReader.Extract`, `ImportForm.FillSettings` | Sí. Solo avisa si el dibujo mide < 2 m o > 5000 km |
| Nivel | El primero por cota (el más bajo) | `ImportForm.FillSettings` | Sí |
| Altura sobre el nivel | **2500 mm**: eje de tuberías y conductos, y altura de los bloques | `ImportForm._height` | Sí |
| Diámetro por defecto (tubería, tubo eléctrico) | 25 mm | `_defaultDiameter`, `MepKinds.DefaultSizeMm` | Sí |
| Conducto rectangular por defecto | **300 × 200 mm** (`Headless` usa 200 × 200) | `_defaultDuctWidth/Height` | Sí |
| Conducto redondo | Ø 200 mm | `MepKinds.DefaultSizeMm` | Sí (por capa) |
| Bandeja | 200 × 60 mm | `MepKinds` | Sí (por capa) |
| Muro: alto | Distancia al nivel superior (si está entre 500 y 20 000 mm); si no, 3000 | `ImportForm.NextLevelHeightMm`, `MepKinds` | Sí |
| Tolerancia de unión | 10 mm | `_tolerance` | Sí |
| Longitud mínima | 50 mm | `_minLength` | Sí |
| «Este plano es de» | Automático (según el nombre de la capa) | `_planKind` | Sí |
| Crear uniones / Conectar automáticamente | Sí / Sí | `_fittings`, `_autoConnect` | Sí |
| Usar la Z del CAD | No (todo a la misma altura) | `_useZ` | Sí |
| Acercar al origen | Automático si el dibujo está a más de 10 km; redondeado a metros | `UpdateTranslateSuggestion`, `ComputeTranslation` | Sí |
| Leer líneas dentro de bloques | No | `_insideBlocks` | Sí |

### 3.2 Constantes internas (no se ven en la ventana)

- **Red** (`NetworkBuilder.Settings`): colineal ≤ 1°; curva que hace de codo si la prolongación es ≤ 1500 mm.
- **Doble línea** (`OutlineDetector.Settings`):
  - Conductos y bandejas: ancho de 80 a 2500 mm, hueco ≤ 1,5 × ancho y ≤ 1500 mm, 1°.
  - Muros: ancho de 50 a 1000 mm, hueco ≤ 1200 mm, se descartan los pilares.
  - Se propone doble línea si la capa tiene de 4 a 20 000 líneas y al menos el 60 % emparejado. Los muros, siempre.
- **Lector**: hasta 300 000 entidades; 16 niveles de bloques anidados.
- **Conexión automática**: conector a ≤ 500 mm y giro < 10°. Las rejillas apoyadas en un conducto se unen con `ConnectAirTerminalOnDuct`.

### 3.3 Mapeo de capas → tipo de elemento (`MepKinds.Suggest`, en este orden)

| Orden | Palabras en el nombre de la capa | Resultado |
|---|---|---|
| 1 | TEXT, COTA, ROTUL, CAJETIN, SIMBOLOG, LEYENDA, ANOTA, EJES, REJILLA CAZ, BASE ARQ, MAA, EQUIPOS | No importar |
| 2 | MURO, WALL, TABIQUE, CERRAMIENTO, FACHADA, PARED, FABRICA, LADRILLO, TRASDOS | Muro |
| 3 | BANDEJA, TRAY, REJIBAND | Bandeja |
| 4 | CONDUIT, TUBO_ELEC, TUBOELEC, CORRUGADO | Tubo eléctrico |
| 5 | CONDUCTO, DUCT | Conducto |
| 6 | TUBERIA, PIPE, FONT, SANEAM, PLUVIAL, FECAL, RIEGO, CALIENTE, FRIA, RACS, AFS, AFC, BIE, ROCIA, SPRINK, CALEF, RADIADOR, SUELO_RAD, AGUA, _IS ; palabras sueltas AF, ACS, GAS, PCI | Tubería |
| 7 | CLIMA, HVAC, VENTIL, IMPULSION, RETORNO, EXTRACC, _CL_, _VT_, AIRE | Conducto |
| 8 | _IE_, ELECTR, FUERZA, ALUMBR, _TEL, TELECOM, ICT, DATOS, BAJA TENSION, BT_ | Tubo eléctrico |
| — | Cualquier otra | No importar |

### 3.4 Tipo de Revit y tipo de sistema por capa

- **Tipo**: el primero, por orden alfabético, que tiene todas sus familias de unión en las preferencias de enrutamiento. Si no hay ninguno, el primero.
- **Conductos**: antes se elige la sección. Se propone la primera disponible en este orden: rectangular, redonda, oval.
- **Muros**: «automático por espesor», el tipo básico de espesor más parecido.
- **Tipo de sistema** (solo tuberías y conductos, `ImportForm.SuggestSystem`): reglas por palabras. Por ejemplo RACS→RETORNO, ACS→CALIENTE, AF→FRÍA, SANEAM→SANIT, PLUVIAL, VENTILACI, PCI→INCEND, IMPUL, RETORNO, EXTRAC, CALEF→IDA. Si ninguna coincide, el primer sistema por orden alfabético.

### 3.5 Bloques → familias

- **Función del bloque** (`BlockNames.Role`, según el nombre y, si no, la capa): conector, eléctrico, sanitario, pieza de unión, terminal, dispositivo, accesorio, equipo u otro.
- **Familia**: se busca una cargada con el mismo nombre (`RevitCatalog.MatchBlock`). Primero familia y tipo exactos; si no, la familia con cualquier tipo. Solo en las categorías de `RevitCatalog.PlaceableCategories`.
- **Si no hay familia**:
  - Pieza de unión: la crea Revit con las uniones.
  - Con huella y función conocida: marcador 3D (DirectShape) de la categoría que corresponde a su función.
  - En otro caso: no se coloca.
- **Marcador 3D**:
  - Alto: el escrito en el nombre («600x100» → 100). Si no lo tiene, un valor SUPUESTO: 300 mm en equipos, 50 en conectores y 100 en el resto.
  - Altura: la general, 2500 mm.

### 3.6 Reglas de colocación actuales (`MepBuilder`)

| Elemento | Regla |
|---|---|
| Tuberías, conductos, bandejas y tubos | **Eje** a cota del nivel + Altura sobre el nivel (2500) + Z del CAD (0 si no se usa la Z). No se consultan forjados |
| Tamaño | Doble línea: el ancho del CAD. Si no, el de la capa. Tras regenerar, se avisa si Revit aplicó otro |
| Muros | Por el eje detectado. Base en el nivel; alto de la capa |
| Familias | En el nivel, con su elevación. Si la inserción del CAD cae fuera de su huella, en el centro de la huella; se gira |
| Marcadores | Prisma con la huella del CAD, centrado en la altura indicada |

### 3.7 Orden de construcción

Todo se hace en **una única transacción** («CAD MEP: construir desde CAD») con `WarningDismisser`:

1. **Tramos y muros**, en el orden de la red: el del dibujo después de limpiar. **No se agrupan por sistema ni por tipo.**
2. Una `Regenerate` y la comprobación de tamaños.
3. **Uniones**: una SubTransaction por unión y 2 intentos (orden natural y orden alternativo de los conectores).
4. **Bloques**: familias o marcadores. Se regenera si hay que activar un símbolo.
5. **Conexiones automáticas**, con una `Regenerate` previa y otra cada vez que se estira un tramo.

## 4. Inventario de los planos de prueba (leído con ACadSharp, fuera de Revit)

| Plano | $INSUNITS | Cotas DIMENSION | Textos | Observaciones |
|---|---|---|---|---|
| CLIMA_VIVIENDA (y copia) | m | 0 | 0 | 24 líneas, 14–15 bloques |
| CLI_A | mm | 0 | 59 | Esquema (notas) |
| CLI_B / CLI_C | m | 0 | 45 / 80 | Rótulos «200x200» |
| FON_A | m | 0 | 100 | Rótulos «AFS:Ø16», «ACS:Ø20» |
| SAN_A | m | **1** (alineada, capa VENTILACIÓN, 4,847 m) | 86 | Rótulos «Ø100», «Ø50» |
| VEN_A | m | 0 | 48 | Rótulos «Ø100», «Ø200» |
| ELE_A / ELE_B / ELE_C / PCI_A / PCI_B | m | 0 | — | — |
| TEL_A / TEL_B | — | — | — | ACadSharp no los lee (Vertex2D → Vertex3D) |

- Ningún plano de prueba tiene varias plantas en el mismo dibujo. Los textos «planta» son notas de montantes («Baja a planta inferior»).
- Casi no hay cotas DIMENSION: el botón 2 no se puede verificar con estos planos tal como está descrito. Las medidas están en **textos de rótulo** (Ø16, 200x200).
- `Pruebas/Proyecto1.rvt` se creó con la plantilla de Arquitectura (ver `Pruebas/DIAGNOSTICO_2026-11-12.txt`). No se sabe si tiene forjados.

## 5. Qué pide el nuevo diseño y no existe hoy

| Requisito | Situación actual |
|---|---|
| Inventario de capas, bloques, textos, cotas y plantas | Solo capas, líneas y bloques. Textos y cotas se cuentan pero no se leen |
| Detectar varias plantas | No existe. El nivel se elige en una lista de niveles de Revit |
| Modelar por sistemas, en un orden | No hay orden por sistemas: los tramos salen en el orden del dibujo |
| Una transacción por sistema dentro de un grupo; regenerar al final de cada sistema | Una sola transacción |
| PLACEMENT_RULE «bajo_forjado» | No existe. La altura es fija sobre el nivel. No se buscan forjados ni huecos |
| Leer DIMENSION y su valor medido | No se leen (ACadSharp sí ofrece `Dimension.Measurement`, `DimensionAligned.FirstPoint/SecondPoint`, `Text`) |
| Cuadre inventario = modelados + no modelados | No existe. El informe da recuentos, avisos y problemas |
| Sin campos ni ajustes manuales | Hoy el Paso 2 tiene unos 15 ajustes y dos tablas editables, y «Cambiar diámetro» pide medidas |

## 6. Contradicciones y dudas detectadas (bloquean el diseño)

1. **Qué proyecto**: la sesión tiene dos carpetas.
   - CadMepRevit (este).
   - otro complemento, con su propio asistente CAD → Revit («Del plano al modelo» y «CAD → Tuberías»). Su `AGENTS.md` exige estabilizar **cinco** botones de colocación y una ventana no modal, lo que choca con «solo dos botones».
2. **«Generatriz inferior tangente a la cara inferior del forjado»**: con esa regla, el tubo quedaría dentro del canto del forjado. Además, la cara inferior del forjado del nivel elegido está por debajo de ese nivel (es el techo de la planta inferior).
3. **«Ajustar dimensiones»**: no está claro qué se ajusta. Puede ser el tamaño (diámetro o ancho × alto), la posición o la longitud. Las cotas DIMENSION miden la misma geometría que ya se ha modelado, así que en principio coincidirían siempre.
4. **«Plantas»**: no está claro si son los niveles de Revit o varias plantas dibujadas en el mismo DWG. El código actual no detecta plantas en el DWG.
5. **«Resultados iguales a la versión actual»**: «bajo_forjado» cambia por fuerza la cota Z respecto a los 2500 mm actuales. Se anotará como diferencia esperada.
6. **«Orden de sistemas que fije el código»**: el código no fija ninguno.
7. **Valores por defecto duplicados**: `ImportForm` propone 300 × 200 para el conducto rectangular y `Headless`, 200 × 200. `Headless` elige además el tipo de conducto entre todas las secciones; `ImportForm`, solo entre las de la sección propuesta.
8. **Unidades erróneas en la cabecera** (p. ej. «plano de muros»: dice mm pero está en metros): hoy se corrigen a mano en el Paso 2. Sin campos, se usará `$INSUNITS` tal cual y el plano saldrá mal escalado.

## 6 bis. Respuestas del usuario (13-11-2026)

| Duda | Respuesta |
|---|---|
| Proyecto | CadMepRevit |
| Regla «bajo_forjado» | Tubo o conducto colgado del techo: su generatriz **superior** es tangente a la cara inferior del forjado que cubre la planta (el del nivel inmediatamente superior al elegido) |
| «Ajustar dimensiones» | Ajusta **tamaños** (diámetro o ancho × alto): primero las cotas DIMENSION; como respaldo, los rótulos de tamaño junto al tramo |
| Plantas | Lista = niveles de Revit. Se pregunta una vez si hay más de uno |

## 7. Decisiones por defecto aplicadas (el informe las repite con su valor)

| Tema | Propuesta |
|---|---|
| Configuración | Mover a una clase compartida (p. ej. `Revit/DefaultPlan.cs`) lo que hoy propone `ImportForm` al abrirse, con los **mismos valores** (los de `ImportForm` prevalecen sobre los de `Headless`). Las dos interfaces la leerían igual |
| Orden de sistemas | El de `MepKinds.All`: tubería, conducto, bandeja, tubo eléctrico y muro. Dentro de cada uno, por tipo de sistema y capa |
| Sin forjado encontrado | Crear el elemento con la regla actual (eje a 2500 mm sobre el nivel) y señalarlo en el informe |
| «Cambiar diámetro» y «Ayuda» | Quitarlos de la cinta y conservar su código. Las ventanas del asistente siguen en el código, pero sin uso |
| Transacciones | Un `TransactionGroup` «CAD MEP: modelar desde CAD» con una `Transaction` por sistema y `Assimilate`, para que un solo Ctrl+Z lo deshaga todo |
| Verificación | Comparar, fuera de Revit, el plan (tramos, uniones, bloques, tipos propuestos) del flujo nuevo con el del asistente actual aceptado sin cambios, en todos los planos de prueba. La parte de Revit (forjados, cotas aplicadas) solo se puede verificar dentro de Revit |
| Red conectada | Un eje común por red (para que encajen codos y tes). Queda tangente el elemento más alto; si la cara inferior varía bajo la red, se toma la más baja |
| Huecos | Se comprueba el forjado cada 500 mm a lo largo de cada tramo. Los huecos se señalan y la colocación no cambia |
| Medida para la generatriz superior | Diámetro exterior de la tubería, diámetro del conducto redondo o alto del rectangular/oval. Sin aislamiento |
| Cotas: asociación | Una cota DIMENSION lineal o alineada se aplica a un tramo si mide en perpendicular a él (±5°), con un punto a cada lado del eje, centrada y separada lo que mide (tolerancia de unión, 10 mm) |
| Rótulos: asociación | Tramo compatible más cercano a menos de 1000 mm; el segundo debe estar al menos 1,5 veces más lejos. Un prefijo («ACS:») tiene que aparecer como palabra completa en el nombre de la capa. Las medidas de los rótulos se leen en mm |
| Unidades | `$INSUNITS`; si no está definida, mm (ya era el comportamiento del código) |
| Registro del modelado | JSON en `%LOCALAPPDATA%\CadMepRevit\modelos`. No se escribe nada oculto en el modelo |

## 8. Diseño aplicado

| Pieza | Archivo | Qué hace |
|---|---|---|
| Cinta | `App.cs` | Solo «Modelar desde CAD» y «Ajustar dimensiones». El resto de comandos se conserva en el código |
| Configuración existente | `Core/PlanDefaults.cs` | Los mismos valores que proponía el asistente. `ImportForm` los lee de aquí (sin cambiar ninguno) |
| Decisiones por capa sin Revit | `Core/PlanDefaults.cs` (`LayerPlanning`) | Doble línea, desplazamiento, aviso de unidades y entrada de la red. Agrupa por capa una sola vez |
| Plan sin ventanas | `Revit/AutoPlanner.cs` | Reproduce la propuesta inicial del asistente: tipo de elemento, sección, tipo, sistema, medidas, alturas y bloques |
| Inventario | `Core/CadReader.cs`, `Core/CadModel.cs` | Numera cada entidad y guarda textos y cotas. Lo que ya leía no cambia (verificado) |
| Construcción por sistemas | `Revit/MepBuilder.cs` (`BuildBySystems`) | `TransactionGroup` con una transacción por sistema. Regenera al terminar los tramos de cada sistema (y otra vez si se han movido redes bajo forjado). Después, bloques y conexiones automáticas. `Build` mantiene el orden de siempre |
| Regla de colocación | `Config/PlacementConfig.cs` (configuración aislada) y `Revit/UnderSlabPlacer.cs` (cálculo) | `PLACEMENT_RULE = "bajo_forjado"` |
| Cuadre | `Core/ModelInventory.cs` | Cada entidad queda como modelada o no modelada, con su motivo |
| Cotas y rótulos | `Core/SizeAnnotations.cs` | Lectura, asociación, contradicciones y ambigüedades |
| Informes | `Revit/ModelReport.cs` | Las cinco secciones pedidas, con el detalle técnico anterior a continuación |

## 9. Verificación realizada (13-11-2026)

| Comprobación | Resultado | Dónde se ejecutó |
|---|---|---|
| Compilación de la solución contra la API real de Revit 2025 y 2027 | 0 avisos, 0 errores | Fuera de Revit |
| Pruebas existentes y nuevas (sección 2i) | Todas superadas | Fuera de Revit |
| Firma completa del lector y de la red del asistente (tramos, bloques, avisos, uniones), núcleo de `f2a0728` frente al nuevo, en 25 planos de prueba | **Idénticas** en los 23 legibles. TEL_A y TEL_B fallan igual en las dos versiones (límite de ACadSharp) | Fuera de Revit |
| Red de la ruta sin ventanas (`LayerPlanning`) frente a la lógica del asistente | Idéntica en los 23 planos | Fuera de Revit |
| Cuadre del inventario en los dos sentidos (suponiendo que Revit crea todos los tramos previstos) | Cada entidad tiene un solo destino en los 23 planos. Cada elemento creado lo explica una entidad en 22. En PCI_A avisa de muros que la red prolonga donde el plano no tiene líneas: es un aviso real, no un error | **Simulación** fuera de Revit |
| Asociación de rótulos y cotas | FON_A: 31 de 63 rótulos aplicables; el resto, con motivo. Corregido: «ACS:» se asociaba a «RACS_Retorno». Pruebas con cota de pared a pared y rótulo de estructura: no se aplican | **Simulación** y pruebas fuera de Revit |
| Revisión independiente del código de Revit (subagente, solo lectura) | 6 hallazgos. Corregidos 5: (1) la conexión automática ya no estira ni inclina tramos colgados del forjado; (2) cotas solo si sus puntos están en el contorno del tramo y en rango; rótulos solo de capas relacionadas; (3) el cuadre comprueba los dos sentidos y puede fallar; (4) si el elemento quedaría por debajo del suelo de su planta no se cuelga y se avisa; (5) excepciones controladas al leer los forjados y después del modelado. El 6 (alto de muros con el nivel elegido) se mantiene y el informe lo anota | Revisión estática |
| Los dos botones dentro de Revit (creación, forjados, huecos, cambio de tamaño, informe, Ctrl+Z) | **NO ejecutado** | Pendiente |

### Diferencias esperadas frente a la versión actual

- La cota Z de tuberías y conductos: ahora cuelgan del forjado, salvo que no haya forjado; antes, el eje estaba a 2500 mm sobre el nivel.
- La conexión automática no estira tramos colgados del forjado: los equipos siguen a 2500 mm, así que esas conexiones quedan por hacer a mano. El informe las enumera.
- El alto de los muros se calcula desde el nivel elegido. El asistente usaba el nivel más bajo aunque se eligiera otro; con el nivel más bajo coinciden.
- «Ajustar dimensiones» no recoloca una red que alguien haya movido a mano después de modelarla, ni una red conectada a un equipo. Lo anota en el informe.
- Sin ventana no se pueden corregir a mano:
  - las unidades mal declaradas en el archivo (p. ej. «plano de muros», que dice mm y está en metros);
  - las capas con nombres que no dicen lo que son (p. ej. la capa «0»).

  Se modelan como propone la configuración (unidades del archivo; la capa «0» no se construye), y el informe lo explica.

### Cómo validarlo dentro de Revit

1. Ejecutar `instalar.cmd` con Revit cerrado.
2. Abrir un proyecto de plantilla «Sistemas» con dos niveles y suelos en el nivel superior.
3. Pulsar «Modelar desde CAD» con `clima_vivienda.dwg`, y después con `FON_A.dwg`.
4. Comprobar en una sección que la parte superior de tuberías y conductos toca la cara inferior del suelo superior.
5. Leer `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt`: secciones 3 (avisos) y 5 (cuadre).
6. Pulsar «Ajustar dimensiones» con FON_A y comparar los diámetros cambiados con los rótulos Ø16/Ø20/Ø25. Leer `ultimo_ajuste_dimensiones.txt`.
7. Pulsar Ctrl+Z dos veces: se deshace el ajuste y después todo el modelado.
8. Repetir sin suelos en el nivel superior: las redes deben quedar a 2500 mm y aparecer como «forjado no encontrado».
