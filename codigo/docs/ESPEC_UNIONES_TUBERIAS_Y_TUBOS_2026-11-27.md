# Uniones de tuberías y tubos eléctricos, bajantes y familias de reserva (27-11-2026)

## 1. Decisiones del usuario

| Tema | Decisión |
|---|---|
| Tubos eléctricos | Llevan **codos, tes, cruces y reducciones**, en el botón **Bandejas** (antes quedaban rectos). |
| Rótulo «Ø» en tuberías | Es el diámetro **nominal** (DN), como ya hacía CAD-REV. Sin cambios. |
| Reducciones de saneamiento | **Concéntricas** de momento (la excéntrica necesita una familia que no hay). |
| Círculo pequeño sobre un tubo | Solo es bajante o montante si tiene un rótulo **SUBE/BAJA** cerca. Si no, **no se modela** y el informe lo lista. |
| Familias de uniones de reserva | Son de Autodesk: no van en GitHub. Cada uno las pone en su PC. **PREPARAR_PC** y **CREAR_INSTALADOR** las detectan, buscan y avisan si faltan. |
| Datos de proyectos | La prueba se hizo con un plano real copiado a una carpeta temporal y borrado después; aquí no se cita. |

## 2. Qué fallaba (prueba real en Revit 2025)

1. **No se creó ninguna unión (0 de 69).** El tipo de tubería del proyecto no tenía piezas, la biblioteca de Revit de ese PC
   no tenía ninguna de unión y el instalador se había generado sin las 12 familias de reserva. Por eso los tubos se
   solapaban en las esquinas. (Arreglo: detectar las familias al preparar el PC y al crear el instalador; ver 5.)
2. **Uniones que no existen en planos exportados de Revit.** Revit dibuja los cruces cortando el tubo de debajo justo lo
   que tapa el de encima. CAD-REV lo leía como unión: cruces y tes falsas, codos convertidos en tes, tramos de 50–100 mm
   entre piezas (donde no caben las piezas) y redes distintas unidas (una cota escrita en una pasaba a todas).
3. **Tubos fantasma** de anchos que no existen (82,5, 127,5, 45,6 mm…) entre bordes de tubos distintos, y reducciones
   absurdas entre ellos.
4. **Bajantes con el diámetro mitad** (Ø12,5 en un tubo Ø25): Revit dibuja la vertical con un círculo de su diámetro y
   otro de la mitad, tangente por dentro, y se tomaba el menor.

## 3. Cambios en el detector de doble línea (solo tuberías: `OutlineDetector.Settings.ForPipes`)

| Ajuste | Qué hace | Por qué |
|---|---|---|
| `MinLineMm = 5` | Descarta líneas de menos de 5 mm antes de emparejar. | Restos de testa de 0,5–4 mm rompían el emparejado: tubos duplicados y partidos. |
| `KeepPairs` | Dos bordes que ya formaban un tubo siguen siendo pareja mientras continúen los dos. Las líneas sueltas se emparejan entre sí, nunca por encima de un tubo ya formado. | El emparejado 1ª-2ª, 3ª-4ª se rehacía en cada intervalo con todas las paralelas del plano: un borde cortado desplazaba la cuenta y salían tubos fantasma. |
| `PipeCrossings` (cruce) | Dos trozos alineados del mismo ancho, con un hueco del ancho de otro tubo que pasa de largo por en medio (o con el otro tubo cortado también ahí), se unen en recto y no se unen al que pasa. | Es un cruce a distinta cota, no una unión. |
| `PipeCrossings` (esquinas) | Al cerrar un hueco no se arrastra el extremo de otro tramo que tiene su propia esquina más cerca. | El codo de un tubo pasaba a ser una te con otro tubo vecino. |
| `PipeCrossings` (cuerpos) | Un trozo más corto que 3/4 de su ancho no sale a buscar uniones por sí mismo. | Cuerpos de te o válvula dibujados con líneas, leídos de través, se alargaban hasta otro tubo: cruz y te inexistentes. |

Conductos, bandejas y muros no cambian (allí un ramal enfrentado a otro sí es una cruz). El informe dice cuántos cruces se
han dejado rectos (con ejemplos de coordenadas) y cuántas líneas de menos de 5 mm se han descartado.

## 4. Bajantes y montantes dibujados con círculos (`PipeRisers`)

- **Diámetro:** de los círculos del grupo, el que más se parece al ancho dibujado del tubo al que llega (antes, el menor).
  Sin tubo a doble línea, el menor, como antes.
- **Círculo pequeño** (menos del 80 % del ancho del tubo, `PlanDefaults.SmallRiserCircleFraction`) sin rótulo SUBE/BAJA a
  menos de 1,5 m: no se modela; el informe lo lista en «BAJANTES Y MONTANTES» como «REVISAR … rotule SUBE o BAJA y repita».

## 5. Tubos eléctricos y familias de reserva

- `ModelScopes.AllowsFitting`: el botón Bandejas crea codos, tes, cruces y reducciones también en tubos eléctricos. Las
  piezas salen del tipo del proyecto o de la biblioteca de Revit (`FittingSetup`, familias «M_Codo de tubo…», «M_Caja de
  conexiones de tubo…»); el informe dice las que falten. En los empalmes en línea se mira el tamaño real de Revit: si
  difiere, se crea la reducción.
- `buscar_familias.ps1` (nuevo, UTF-8 con BOM): la lista de las 12 familias y su búsqueda (familias_uniones, `-Familias`,
  `REVITAUTOMATION_FAMILIAS`, bibliotecas de Revit del PC). Lo usan `crear_instalador.ps1` y `preparar_pc.ps1`.
- `preparar_pc.ps1`: al final comprueba las 12 familias, copia las que encuentre y avisa de las que falten (no es un
  error). Admite `-Familias <carpeta>`.

## 6. Verificación

| Comprobación | Resultado | Dónde |
|---|---|---|
| Plano real de la prueba (fontanería exportada de Revit), botón Tuberías | Tramos 121 → 93; tes 12 → 6; cruces 8 → 0; extremos de unión en tramos de menos de 150 mm 58 → 30; bajantes con el diámetro de su tubo | Fuera de Revit (planificador real con catálogo simulado) |
| Comparación de planes antes/después (`Compare-Plans.ps1`) | Solo cambia el botón Tuberías del plano real (previsto). Los demás botones y los DXF sintéticos, idénticos | Fuera de Revit |
| Pruebas automáticas (grupos 2s y 2t nuevos, 2o y 2a/2q ampliados) | 395 superadas | Fuera de Revit |
| Las pruebas nuevas, con las reglas desactivadas | 7 fallan (detectan el fallo antiguo) | Fuera de Revit |
| Creación real de codos, tes y reducciones de tubería y tubo eléctrico | **NO verificado** | Pendiente dentro de Revit |

Pendiente: la zona de equipos dibujada con detalle (filtros, válvulas y bridas con líneas sueltas) aún da dos piezas de
anchos raros. No se ha forzado con reglas que podrían romper tubos reales de Ø125.

## 7. Cómo verificarlo dentro de Revit

1. Con las 12 familias en su sitio (PREPARAR_PC dice `[OK]`), genere e instale el instalador.
2. En un proyecto con un tipo de tubería «Por defecto» sin piezas, ejecute **Tuberías** con un plano exportado de Revit.
3. Compruebe que los tubos que se cruzan siguen rectos y sin unir, que los codos y tes se crean y que «UNIONES» del informe
   no tiene fallos por falta de familia.
4. Ejecute **Bandejas** con un plano con tubos eléctricos: compruebe sus codos, tes y reducciones.
