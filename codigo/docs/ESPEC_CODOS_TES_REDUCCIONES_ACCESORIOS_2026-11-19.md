# Encargo (19-11-2026, tarde): codos, tes y reducciones; huecos pequeños; accesorios conectados

Proyecto: `CadMepRevit_Combinado_Prueba`, un complemento de Revit 2025 (.NET 8) y 2027 (.NET 10) en C#.

Lee antes:
- `README.md`, `LEEME_VERSION_COMBINADA.txt` y `TRASPASO_PROYECTO.txt`;
- todas las especificaciones de `docs/`, sobre todo `ESPEC_TUBERIAS_PENDIENTES_SIN_UNIONES.md`: las pendientes no se
  pueden romper;
- el informe `Pruebas/RESULTADO_POSICION_BANDEJAS_AJUSTAR_2026-11-19.txt`.

## Reglas del encargo (obligatorias)

- **Reglas generales.** Tienen que valer para planos de cualquier oficina. Nada ajustado a los planos de prueba: ni
  nombres de capa o de archivo, ni coordenadas, ni números sacados de un plano concreto. Los umbrales van en mm o
  proporcionales al diámetro o ancho, como constantes con nombre en un único sitio.
- **No inventes API de Revit.** Si algo no compila para 2025 y 2027, no existe: para y pregunta.
- **No inventes medidas.** Lo supuesto se marca como SUPUESTO en el informe.
- **Copia de seguridad antes de tocar nada:** `CadMepRevit_Combinado_Prueba_copia_20261119_codos`, con `robocopy`.
  Comprueba que coincide.
- **No ejecutes `instalar.cmd`.**
- **No escribas en unidades de red**: solo lectura.
- **SDK:** `%USERPROFILE%\.dotnet\dotnet.exe` (no está en el PATH; para ejecutar, `DOTNET_ROOT` = esa carpeta).
- **Compilación:** `dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true` debe salir con 0 errores y
  0 avisos.
- **Pruebas:** con `CADMEP_PLANOS=C:\CAD-REVIT\Pruebas`. En este equipo pasan hoy 440; al
  acabar deben pasar todas.
- **Comparación antes y después** con `tests/CadMepRevit.PlanComparison` (amplíala si hace falta). Solo puede cambiar
  lo que pide este encargo, y cada diferencia se explica.
- **Si algo no se puede hacer como dice este documento, o hay una duda, para y pregunta.**

## Datos medidos el 19-11 con los planos de prueba (para orientarte, no para ajustar reglas)

El planificador ya detecta los encuentros (`NetworkBuilder`):

| Plano | Codos | Tes | Cruces | Transiciones |
|---|---|---|---|---|
| FONTANERÍA PB (2), tuberías | 329 | 257 | 57 | 6 |
| RED COLGADA, tuberías | 168 | 106 | 14 | 15 |
| VENTILACIÓN PB, conductos | 63 | 12 | 4 | 4 |

- **Giro de los codos:** sobre todo 90° y 45°; también hay otros ángulos, en saneamiento con pendiente.
- **Codos con algún tramo menor de 300 mm:** son muchos (en FONTANERÍA PB (2), 278 de 329). En esos puede no caber la
  pieza.
- **Huecos sin unir entre extremos libres:**
  - esquina sin cerrar (codo perdido): de 7 a 22 por plano;
  - tubo que apunta al costado de otro (te perdida): hasta 69 por plano;
  - tamaño típico en tuberías: de 30 a 150 mm;
  - tamaño típico en conductos: de 300 a 600 mm.
- **Posición de los bloques:**
  - las válvulas quedan a 0-8 mm de su tubería;
  - las rejillas quedan sobre el costado o en el extremo de su conducto.

## 1. Uniones por botón (decisión del usuario, 19-11)

| Botón · clase | Codo | Te | Cruz | Reducción (transición) |
|---|---|---|---|---|
| Tuberías · tubería | SÍ | SÍ | **NO** | SÍ |
| Conductos · conducto | SÍ | SÍ | SÍ | SÍ |
| Bandejas · bandeja | SÍ | SÍ | SÍ | SÍ (como ahora) |
| Bandejas · tubo eléctrico | no | no | no | no (como ahora) |

1. **Uniones permitidas por botón y clase.** Sustituye los indicadores todo o nada
   (`PipesWithoutFittings`, `DuctsWithoutFittings`, `ConduitsWithoutFittings` y `WithoutFittings(kind)`) por una sola
   regla: qué clases de unión se crean en cada botón y clase de elemento (por ejemplo
   `ModelScopes.AllowedFittings(scope, kind)`).
   - Revisa todos los sitios que usan los indicadores antiguos: `AutoPlanner`, `MepBuilder`, `ModelReport`,
     `ImportCadCommand`, la comparación y las pruebas.
   - Ojo: `PipesWithoutFittings` también activa otras cosas del botón Tuberías (pendientes, sistemas por red, cotas,
     conexión automática). Esas tienen que seguir igual.
2. **Preparación del proyecto.** `FittingSetup.Complete` vuelve a completar las preferencias de enrutamiento con codo,
   te y transición en los tipos de tubería, y con codo, te, cruz y transición en los de conducto. Las familias se
   buscan en la biblioteca de la empresa. Comprueba en la biblioteca que las que se nombran en `FittingSetup` existen, y lista
   en el informe del encargo las que no.
3. **Cruces en tuberías (decisión del usuario):** no se crea ninguna pieza.
   - Los tramos quedan tocándose, con sus extremos compartidos, como ahora.
   - El informe los lista: «Encuentro de 4 tuberías en (x, y): sin unir; resuélvalo a mano (en tubería no hay cruces)».
4. **Tuberías con pendiente.** Las uniones se crean después de los tramos, cada una en su subtransacción, como ya hace
   `MepBuilder`.
   - Al crear una pieza, Revit recorta los tramos. Comprueba que la pendiente de cada tubo no cambia. Añade al
     diagnóstico, por muestra, la pendiente antes y después.
   - Si la pieza cambia la pendiente o no se puede crear, se deshace esa pieza y los tramos quedan tocándose, con el
     motivo en el informe. Esto incluye las tes cuya tubería principal tiene dos pendientes distintas, que pueden no
     estar alineadas.
   - Bajantes: el encuentro de la vertical con la horizontal es un codo o una te más.
   - Mantén la regla de la conexión automática: nunca estirar, mover ni inclinar una tubería.
5. **Reducciones bien hechas, en tuberías y conductos.**
   - **Problema actual:** `NetworkBuilder` clasifica la transición por el ancho dibujado antes de que
     `AutoPlanner.AssignSizesFromLabels` asigne las medidas de los rótulos y las cotas. Un cambio de diámetro que solo
     dice el rótulo no produce transición.
   - **Qué hacer:** después de asignar las medidas, reevalúa cada encuentro de dos tramos alineados.
     - Si su medida final es distinta, es una `Transicion` y se crea la reducción.
     - Si es igual, no hace falta pieza: no se crea nada y no cuenta como problema.
   - **Comprueba también qué hace hoy el planificador con dos trozos alineados de la misma medida:** si los une o deja
     el encuentro.
   - **Tes con derivación de otra medida:** las resuelve la familia de te del tipo. Si la tubería principal cambia de
     medida en la te, se añade la reducción donde haga falta. Si no se puede, va al informe.
   - **Conductos:** transición rectangular-rectangular y redonda-redonda. Si cambia la forma, es una transición de
     forma si el tipo la admite; si no, va al informe.
6. **Si Revit no puede crear una unión** (tramo más corto que la pieza, ángulo no admitido…): se deshace solo esa, los
   tramos quedan tocándose y el informe dice cuál, dónde y por qué. Nunca se pierde un tramo.
7. **Informe:**
   - recuentos por clase de unión: creadas, no creadas con su motivo, y cruces de tubería sin unir;
   - la línea de diagnóstico por unión que ya existe, también para tuberías y conductos.

## 2. Cerrar huecos pequeños (decisión del usuario: «sí, huecos pequeños»)

En la fase de plan (Core), antes de las pendientes y de asignar medidas por recorrido. Solo entre tramos de la **misma
capa**:

- **Esquina (codo perdido):** dos extremos libres cuyas prolongaciones se cortan delante de los dos, a no más del
  límite de cada uno. Se alargan ambos hasta el punto de corte y queda un `Codo`.
- **Te perdida:** un extremo libre que apunta, con un ángulo de al menos 30°, al costado de otro tramo y está a no más
  del límite. Se alarga hasta el eje del otro, se parte el otro en ese punto y queda una `Te`.
  - En tuberías, si en ese punto acaban dos tramos que no forman te, el encuentro queda como en el punto 1.3.
- **Hueco en línea:** dos extremos alineados y enfrentados.
  - Si por el hueco pasa otro tramo, es un cruce a distinta altura y **no se cierra**.
  - Si no pasa nada, se cierra solo si el hueco es menor que el límite. Si la medida cambia, es una reducción.
- **Límite, proporcional a la medida del tramo** (constantes con nombre). Propuesta inicial:
  - tuberías: 3 × diámetro, como mínimo 50 mm y como máximo 250 mm;
  - conductos y bandejas: 1,5 × ancho, como máximo 1000 mm.

  Si la medida del tramo aún no se conoce, usa la de su capa. **Calibra con los planos de prueba sin ajustarte a
  ellos** y explica en el informe del encargo cuántos huecos se cierran y cuántos quedan con cada valor.
- **Nunca se cierra:**
  - entre capas distintas;
  - si el alargamiento cruza otro tramo de la misma capa;
  - si deja un tramo de menos de `MinLengthMm`.
- **Informe del modelado:** «Huecos cerrados: N esquinas, M tes, K en línea (alargamiento máximo X mm)», y la lista de
  los huecos que no se cierran por superar el límite.
- **Pendientes:** comprueba que en los planos de saneamiento las pendientes y los rótulos asociados siguen saliendo
  igual en los tramos no afectados. RED COLGADA tiene 30 de 30 etiquetas y RED ENTERRADA 70 de 70. En los afectados,
  explica la diferencia.

## 3. Accesorios y terminales: cada uno en su botón, insertados y conectados

**Qué botón coloca cada cosa (decisión del usuario):**
- **Conductos:** rejillas, difusores, bocas de extracción (`Terminal`), compuertas y demás accesorios de conducto
  (`Accesorio` con `IsDuctAccessory`), y ventiladores y equipos de aire (`Equipo` que no es de agua).
  - Hoy lo coloca «Equipos y PCI»: quítaselo, porque si no se colocaría dos veces.
  - Revisa `ModelScopes.Includes`, `IsDuctBlock`, `BlockFamilies.Prepare` y `AutoPlanner.BlockInScope`.
- **Tuberías:** válvulas, grifos, contadores y equipos de agua, como ahora.
- **Bandejas:** los accesorios de bandeja, si los hay. No hay ninguno en los planos de prueba; deja la regla general y
  una prueba sintética.
- **Equipos y PCI:** PCI, eléctricos, dispositivos y los bloques «Otro» con familia, como ahora sin lo de aire.

Para cada bloque de estos, el plan (Core) busca **su tramo**: el de su red más cercano en planta, a menos de una
distancia proporcional a la medida del tramo (constante con nombre). Anota en qué tramo está, en qué punto del eje,
si cae en un extremo o en medio y a qué lado. Después:

1. **Altura y giro (decisión del usuario: a la altura de su red):**
   - el punto de inserción va a la cota del eje del tramo en ese punto; en tuberías con pendiente, a la cota en ese
     punto de la pendiente;
   - el giro sigue la dirección del tramo (en línea) o la cara hacia fuera del conducto (en el costado);
   - esto sustituye, para estos bloques, la altura supuesta y la colocación en techo de `BlockElevations`.
2. **Insertados en la red (válvulas, compuertas y accesorios en línea):**
   - la familia se coloca en el eje, orientada con el tramo;
   - el tramo se corta en ese punto;
   - los dos trozos se conectan a los conectores del accesorio;
   - si el accesorio y el tramo son de distinta medida, se ajusta la medida del accesorio si su familia lo permite; si
     no, se pone una reducción.

   Usa solo API que compile en 2025 y 2027; si no encuentras cómo cortar el tramo con la API, para y pregunta. Todo en
   una subtransacción por accesorio: si falla, se deshace, el tramo queda entero y el accesorio se coloca sin
   conectar, con el motivo en el informe.
3. **Rejillas y difusores:**
   - **En el extremo del conducto:** a la altura del conducto, orientada con su eje y conectada al extremo
     (directamente, o con una transición si la medida no coincide).
   - **En el costado del conducto (decisión del usuario):** a la altura del conducto, con la cara orientada hacia fuera
     del conducto, y **conectada a él** con la pieza de toma o derivación que corresponda en Revit.
   - Comprueba qué método de la API existe en 2025 y 2027 para conectar un terminal sobre un conducto, o crear la toma.
     **No lo supongas.** Si no hay ninguno que compile, para y pregunta.
   - Si no se puede conectar, se deja colocada y orientada, y va al informe.
4. **Ventiladores y equipos:** a la altura de su conducto o tubería. Se conectan si un conector coincide con el extremo
   de un tramo, con la conexión automática actual y sin estirar tramos.
5. **Informe:**
   - por familia: colocadas, insertadas o conectadas, y solo colocadas con su motivo;
   - en el diagnóstico, una muestra por familia con su punto, su cota, su giro y el tramo al que se conectó.

## 4. Pruebas nuevas (fuera de Revit)

- **DXF sintético de tuberías:** L, te, cuatro tramos en un punto (cruce, que no se une), cambio de diámetro por rótulo
  en un tramo alineado (reducción), esquina con hueco de 40 mm (se cierra), hueco de 600 mm (no se cierra), hueco en
  línea con otro tubo pasando por medio (no se cierra) y una válvula en mitad de un tramo (se asocia a ese tramo, en
  medio y en línea).
- **DXF sintético de conductos:** L, te, cruz, reducción, una rejilla en el extremo, una rejilla en el costado (lado y
  orientación correctos) y una compuerta en línea.
- **Planos reales:**
  - recuentos de uniones permitidas por botón;
  - huecos cerrados;
  - las rejillas de `VENTILACION_S1.dwg` y `VENTILACION_PB.dwg`, cada una asociada a un conducto;
  - las válvulas de `FONTANERIA_PB*.dwg`, cada una asociada a una tubería.
- **Comparación antes y después.** Las únicas diferencias admitidas son:
  - las uniones planificadas de tuberías y conductos;
  - los tramos de los huecos cerrados;
  - los bloques de aire, que pasan de «Equipos y PCI» a «Conductos»;
  - las transiciones añadidas por medida de rótulo.

  En lo demás, medidas, sistemas y pendientes tienen que salir iguales.

## Al terminar

1. Compila: 0 errores y 0 avisos. Pasa las pruebas: todas.
2. Actualiza `CAD-REVIT\COMPILADO_CadMepRevit_2026-11-17` (DLL y PDB de 2025 y 2027, `SHA256SUMS.txt`) y los dos
   LEEME. Explica qué NO se ha podido verificar dentro de Revit:
   - la creación real de las piezas;
   - las pendientes después de recortar los tramos;
   - la inserción de accesorios;
   - la conexión de rejillas.
3. Deja el informe en `CadMepRevit_Combinado_Prueba\Pruebas\RESULTADO_CODOS_TES_ACCESORIOS_2026-11-19.txt`, con:
   - lo hecho en cada punto, por archivo;
   - las familias de unión encontradas y las que faltan;
   - la calibración de los huecos;
   - la comparación antes y después;
   - las pruebas nuevas;
   - qué líneas del `ultimo_informe.txt` debe mirar el usuario en Revit.
