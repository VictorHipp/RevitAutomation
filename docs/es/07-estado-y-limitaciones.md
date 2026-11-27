# 7. Estado y limitaciones

[← Índice](../../README.es.md) · [English](../en/07-status-and-limitations.md)

Estado a **25-11-2026** (versión 1.0.0).

## 7.1 Verificado

| Comprobación | Resultado | Dónde |
|---|---|---|
| Compilación de RevitAutomation + CAD-REV para Revit 2023, 2024, 2025, 2026 y 2027 contra la API de cada versión | 0 errores, 0 avisos | Fuera de Revit |
| Pruebas del motor (`CadMepRevit.Tests`) | 395 superadas, 0 fallidas (27-11-2026) | Fuera de Revit |
| Pruebas de las herramientas (etiquetado, aislamiento, planos, región, PDF, salas, patinillo, llaves) | Todas superadas (≈ 830 comprobaciones de lógica en total con el motor) | Fuera de Revit |
| Instalador en una carpeta de prueba (`/destino:`) | Instala las versiones, quita los complementos antiguos y respeta los ajenos | Fuera de Revit |
| Versiones anteriores de CAD MEP | El complemento carga; se crearon conductos desde un plano real de climatización | Dentro de Revit 2025 (versión antigua) |

## 7.2 Pendiente de comprobar dentro de Revit

Las actualizaciones del 19-11-2026, 25-11-2026 y 27-11-2026 aún deben comprobarse dentro de Revit 2023–2027:

- Que la pestaña, los desplegables y las herramientas copiadas aparecen y funcionan en todas las versiones.
- Creación real de codos, tes, cruces y reducciones; pendientes intactas tras recortar; aviso de los encuentros de cuatro tubos; cierre de huecos.
- En 3D: que válvulas y compuertas cortan los tramos; rejillas a la cota del conducto, orientadas y conectadas en el extremo o el lateral.
- Uniones de bandeja.
- 27-11-2026: codos, tes, cruces y reducciones de tubos eléctricos; tubos que se cruzan en el plano quedan rectos y sin unir; bajantes con el diámetro de su tubo y líneas «REVISAR» de círculos pequeños sin SUBE/BAJA. Detalle en `codigo/docs/ESPEC_UNIONES_TUBERIAS_Y_TUBOS_2026-11-27.md`.
- Alineación sobre un CAD vinculado movido y girado (línea `Posición` del informe).
- Colocación de familias genéricas.
- Regla «sigue el techo»: subidas con dos codos, alineación de subidas rectangulares, falsos techos, forjados de modelos vinculados.

Revise `Posición`, `UNIONES`, «Pendiente antes y después», «Huecos cerrados», «ALTURA SEGÚN EL TECHO» y «ACCESORIOS, TERMINALES Y EQUIPOS DE RED» en `ultimo_informe.txt`.

## 7.3 Limitaciones conocidas

- Solo se lee el **espacio modelo**. Los arcos y las polilíneas curvas se aproximan por cuerdas. Las XREF sin cargar se avisan, no se leen.
- Las uniones dependen de las preferencias de enrutamiento del tipo; Revit puede rechazar tramos más cortos que la pieza.
- Una transición descentrada (los ejes ni se cortan ni están alineados) queda sin unir.
- Los encuentros de cuatro tuberías los resuelve el técnico (los tubos eléctricos sí llevan cruz desde el 27-11-2026).
- Las uniones necesitan familias: si el tipo del proyecto no tiene piezas y no hay biblioteca ni las 12 familias de reserva, no se crea ninguna (PREPARAR_PC y CREAR_INSTALADOR avisan si faltan).
- Las reducciones de saneamiento son concéntricas (la excéntrica necesita una familia que no hay).
- Zonas de equipos dibujadas con mucho detalle (filtros, válvulas y bridas con líneas sueltas) pueden dar piezas de anchos raros: revíselas.
- Los marcadores 3D (`DirectShape`) no tienen conectores: no se conectan ni forman sistemas.
- Las familias basadas en cara o con anfitrión no se colocan directamente (las de techo se apoyan en un plano de referencia horizontal «CAD-REV · techo …»); si no, se crea un marcador y se informa.
- No se crean puertas, ventanas, suelos ni circuitos eléctricos.
- Las alturas que no da la planta son `SUPUESTO` y hay que revisarlas.
- Solo DWF 6 del espacio modelo (no DWFx).
- En zonas muy densas (baños, junto a aparatos) pueden quedar sin unir algunos encuentros de 5 o 6 tubos; el informe los lista.

## 7.4 Posibles siguientes pasos

Según las notas de traspaso del proyecto (`codigo/TRASPASO_PROYECTO.txt`):

1. Hacer dentro de Revit todas las comprobaciones de 7.2 y corregir lo que indiquen los informes.
2. Importar varios planos a la vez (arquitectura + instalaciones) o resolver las XREF.
3. Conectar tramos a equipos con el conector desviado (tramo corto o codo en lugar de rechazar).
4. Circuitos eléctricos desde las tomas hasta un cuadro elegido.
5. Puertas y ventanas en los huecos de los muros.
6. Si se quiere, publicar el instalador como «Release» de GitHub cuando ya no lleve dentro familias de Autodesk.
