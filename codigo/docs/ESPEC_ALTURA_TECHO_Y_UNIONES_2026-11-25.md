# Altura según el techo y piezas de unión de reserva (25-11-2026)

## 1. Decisiones del usuario

| Tema | Decisión |
|---|---|
| El techo cambia de altura | El tramo **sigue el techo con codos** (sube o baja). La **planta es siempre la del CAD**. |
| Qué no se atraviesa | Forjados (suelos), del proyecto y de los **modelos vinculados**. |
| Falso techo | Si el tramo no cabe en el plénum, **se deja bajo el forjado y se avisa**. |
| Holgura | La parte de arriba del tramo queda a **100 mm** de la cara inferior del forjado. |
| Alcance | Conductos, tuberías horizontales sin cota en el plano, bandejas y tubos eléctricos. |
| Lo que fija el plano | Pendientes, cotas escritas, redes enterradas y bajantes **conservan su cota**. Solo se avisa si atraviesan el forjado. |
| Datos de proyectos | No se dejan copias de planos, informes ni nombres que identifiquen proyectos o clientes. Si hace falta un ejemplo, se usan nombres genéricos. |

## 2. Regla «sigue_techo» (`Config/PlacementConfig.cs`)

1. Los tramos se crean como antes, con el eje a 2500 mm sobre el nivel.
2. `MepBuilder.FollowCeiling` (`Revit/MepBuilder.Ceiling.cs`), antes de las uniones:
   - Elige los tramos que pueden seguir el techo. Una red unida a un tramo que conserva su cota del plano se queda entera como estaba.
   - Lee los forjados y los falsos techos con `CeilingFinder`, del proyecto y de los vínculos cargados, en la franja de la planta.
   - Cada 200 mm a lo largo del tramo, en el eje y en los dos bordes, calcula la cota máxima del eje: cara inferior del forjado − 100 mm − medio alto exterior.
   - `CeilingProfile.Solve` (núcleo, sin Revit) da los trozos horizontales de cada tramo:
     - Un escalón menor que la subida mínima (2·alto + 50 mm en conductos y bandejas, 3·Ø + 50 mm en tuberías y tubos) va por la cota más baja.
     - Un trozo más corto que el mínimo (3·alto + 200 mm, al menos 400 mm) se une al vecino más bajo. Si es una zona baja estrecha, como una viga, se ensancha para que quepan los codos.
     - La subida o bajada se aparta medio alto + 50 mm hacia la zona alta, para que el codo no toque el forjado bajo.
     - En cada unión todos los extremos quedan a la cota del más bajo. Si el trozo es largo, solo baja cerca de la unión.
     - Un tramo sin forjado encima toma la cota de los tramos con los que se une. Si en toda su red no hay forjado, queda a 2500 mm (SUPUESTA).
   - Los tramos se mueven y se parten con copias del mismo tipo, sistema y tamaño, y se crean las subidas verticales. Cada tramo va en su subtransacción y, si falla, queda como estaba y se avisa.
   - Cada subida lleva dos codos. La subida rectangular se gira para alinear su ancho con el tramo horizontal; si los codos no encajan, se repite girándola 90°.
   - Se avisa de los tramos que atraviesan el forjado, de los que quedan por debajo del falso techo y de los que quedan bajo el suelo de la planta.
3. Las uniones del plano, los accesorios y las rejillas usan la cota del trozo de su punto.
4. El informe tiene una sección propia, «ALTURA SEGÚN EL TECHO».

## 3. Por qué no salían los codos de los conductos

El tipo de conducto del proyecto de prueba no tenía piezas de unión, y la biblioteca de Revit de ese equipo no tenía ninguna de conducto. El complemento solo buscaba nombres exactos, así que todas las uniones fallaban con «failed to insert elbow».

Corrección:
- `FittingNames.LooksLike` (núcleo) reconoce las piezas por su nombre: codo, te, cruz o transición, y sección rectangular, redonda, oval o tubería. Descarta los cambios de sección, las piezas de bandeja y de tubo eléctrico, los remates y las tes pantalón.
- `FittingSetup` lo usa cuando falla el nombre exacto. Al cargar la familia, comprueba su categoría y su clase de pieza.
- Se instala con el complemento una carpeta `Familias` de reserva (origen: `familias_uniones/`). Contiene el codo, la te, la cruz y la transición para tubería y para conducto rectangular y redondo. Es la última biblioteca consultada: mandan las del proyecto y las de la oficina.

## 4. Verificación

| Comprobación | Resultado | Dónde |
|---|---|---|
| Compilación para Revit 2025 y 2027 | 0 avisos, 0 errores | Fuera de Revit |
| Pruebas automáticas (grupos 2q y 2r nuevos) | Todas superadas | Fuera de Revit |
| Comparación de planes antes/después, 15 planos × 6 botones | Solo diferencias previstas (el plan no cambia: la regla actúa en Revit) | Fuera de Revit |
| Subidas, codos, alineación de subidas rectangulares, falso techo y vínculos | **NO verificado** | Pendiente dentro de Revit |

## 5. Cómo verificarlo dentro de Revit

1. Use un proyecto con un forjado encima del nivel que tenga un escalón o una zona más baja, y un tipo de conducto «Por defecto» sin uniones.
2. Ejecute **Conductos** con un plano de ventilación.
3. Compruebe que se crean los codos, las tes y las transiciones, con las familias de reserva si hace falta.
4. En una sección, compruebe que la parte de arriba queda a 100 mm del forjado y que el tramo baja y sube con dos codos en el escalón.
5. Lea «ALTURA SEGÚN EL TECHO» en `ultimo_informe.txt`.
