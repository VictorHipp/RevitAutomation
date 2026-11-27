# 3. Guía de uso

[← Índice](../../README.es.md) · [English](../en/03-user-guide.md)

La guía rápida que se instala con el complemento es [`codigo/docs/Guia_CadMepRevit.txt`](../../codigo/docs/Guia_CadMepRevit.txt). Esta página la resume y añade las demás herramientas.

## 3.1 Antes de empezar

- Use un **proyecto** de Revit creado con la plantilla «Sistemas» o «Mecánica»: trae tipos de tubería, conducto, bandeja y tubo, y sus sistemas. Con «Arquitectura» no hay tipos MEP.
- En el CAD, cada red en su propia capa (`AF`, `ACS`, `SANEAMIENTO`, `P-PIPE`, `M-HVAC-DUCT`, `E-POWR-CNDT`…), dibujada con LÍNEA o POLILÍNEA en el espacio modelo.
- Recomendado: vincule el mismo DWG en el proyecto (Insertar › Vincular CAD) para que el modelo quede alineado con la arquitectura.

## 3.2 Usar un botón de CAD-REV

1. Pulse el botón de la instalación (RevitAutomation › CAD-REV › …).
2. Elija el archivo `.dwg` o `.dxf`.
3. Solo en Tuberías, Conductos y Bandejas: puede elegir también el **DWF del mismo plano** (Cancelar si no lo tiene). Sus textos se suman a los del DWG para leer las medidas escritas. Solo DWF 6 del espacio modelo (no DWFx).
4. Si el proyecto tiene varios niveles, elija la **planta**. Es la única pregunta.
5. Al terminar verá un informe y los elementos creados quedan seleccionados. **Ctrl+Z** lo deshace entero.

Si el plano tiene capas o bloques que el programa no reconoce, la ventana «¿Qué es esto?» pregunta qué es cada uno. Las respuestas se guardan en `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` y sirven para los planos siguientes.

## 3.3 Alturas

- **Tuberías:** pendientes y cotas escritas en el plano, relativas al nivel elegido. Las cotas C.I., solera o fondo se convierten a cota de eje con el radio exterior. Sin ninguna cota escrita, el punto más alto queda a +2500 mm (red colgada) o −600 mm (enterrada), marcado `SUPUESTO`.
- **Conductos, bandejas y tubos eléctricos:** eje a 2500 mm sobre el nivel y, después, la **regla «sigue el techo»** deja su parte superior 100 mm bajo el forjado de encima.
- **Rejillas, accesorios y equipos:** a la cota de su red.
- **Resto de dispositivos:** alturas habituales marcadas `SUPUESTO`: luminarias 2700 mm (apliques y emergencias 2200), interruptores 1100, detectores de presencia 2700, tomas 300, cuadros 1500, extintores 1000, BIE 1500, pulsadores 1200, sirenas 2300, señales 2100, detectores de incendio 2700.

**Uniones y cruces.** Tuberías: codos, tes y reducciones (las cruces de cuatro tubos quedan para el técnico). Conductos, bandejas y tubos eléctricos: codos, tes, cruces y reducciones. En los planos exportados de Revit, un tubo cortado donde pasa otro es un cruce a distinta cota: se modela recto y sin unir, y el informe lo dice. Las bajantes dibujadas con círculos toman el diámetro de su tubo; un círculo más pequeño que su tubo sin rótulo SUBE o BAJA cerca no se modela y aparece en el informe como «REVISAR».

## 3.4 Pendientes

Dibuje la pendiente con texto (`2 %`, `i=1,5 %`, `2 ‰`…) y una flecha hacia donde baja el agua. Se reconocen flechas de líneas, triángulos, rellenos, polilíneas de ancho variable, directrices y bloques. Una etiqueta vale para todo el recorrido hasta la siguiente te o cruz. Sin etiqueta válida, o con empates o votos contrarios, el recorrido queda horizontal y el informe dice qué revisar y dónde.

## 3.5 Familias de los bloques CAD

1. La familia con el nombre del bloque (los planos exportados de Revit se llaman «Familia - Tipo-…»), del proyecto o de las bibliotecas.
2. Si no, una familia **genérica** de Revit de su clase (rejilla, foco empotrado, interruptor, extintor, detector de humos…), prefiriendo las que no necesitan anfitrión.
3. Si no hay ninguna, un marcador 3D (`DirectShape`) con la huella del CAD y sin conectores.

Las bibliotecas se indican en `%APPDATA%\CadMepRevit\bibliotecas.txt` (una carpeta por línea).

## 3.6 Reconocimiento de capas

| La capa contiene | Se construye como |
|---|---|
| AF, ACS, RACS, SANEAMIENTO, PLUVIALES, FONTANERÍA, PCI, P-PIPE | Tubería (Tuberías) |
| CONDUCTO, CLIMA, IMPULSIÓN, RETORNO, EXTRACCIÓN, VENTILACIÓN, M-HVAC-DUCT | Conducto |
| BANDEJA, TRAY, E-CABL-TRAY | Bandeja de cables |
| CONDUIT, CORRUGADO, E-POWR-CNDT, FUERZA | Tubo eléctrico |
| -IDEN, -ANNO, -NOTE, TEXTOS, COTAS, -RISE | No se construye (anotaciones) |

Una capa llamada `0`, o con un nombre que no dice lo que es, no se construye: renómbrela. Los dibujos a doble línea (contorno) se detectan solos y se modelan como un tramo por el eje con el ancho dibujado.

## 3.7 El informe

Se muestra después de cada botón y se guarda en `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt`, con una copia con fecha en `informes\`:

1. Elementos modelados, por sistema, con su familia y tipo.
2. Elementos no modelados, con el motivo (incluido «lo modela el botón …»).
3. Avisos: uniones fallidas, medidas no aplicadas, DWF no usado…
4. Decisiones por defecto y valores `SUPUESTO`.
5. Cuadre: entidades del plano = modeladas + no modeladas.

Secciones útiles para revisar un resultado: `Posición`, `UNIONES`, «Pendiente antes y después», «Huecos cerrados», `PENDIENTES`, «ALTURA SEGÚN EL TECHO» y «ACCESORIOS, TERMINALES Y EQUIPOS DE RED».

## 3.8 Las demás herramientas

- **Salas técnicas:** abra la planta con el DWG de la sala vinculado (si hay varios, pinche el de la sala) y pulse la variante. Cada contorno cerrado con un rótulo dentro o al lado se convierte en el equipo del rótulo (círculo → cilindro, resto → caja), con su alto habitual o el del rótulo `H=…`. Al repetirlo sustituye solo lo que creó este botón. Claves y alturas editables en `Documentos\RevitAutomation\SalasTecnicas`.
- **Patinillo:** seleccione el patinillo (habitación o contorno de líneas) o dibújelo; todos los valores se editan en el diálogo y se guardan. Informe y desviaciones en `Documentos\RevitAutomation\Patinillo\output`.
- **Aislamiento RITE:** seleccione las tuberías (o elíjalas con el ratón); solo pregunta si van por el interior o el exterior del edificio. Informe en `Documentos\RevitAutomation\Aislamiento\output`.
- **Llaves de local húmedo:** elija bloque y capa, una familia de accesorio de tubería basada en nivel, las unidades, la altura (propuesta 1000 mm) y el giro adicional. Las posiciones ya ocupadas (< 1 mm) se omiten. Informe CSV en `Documentos\RevitAutomation\LlavesLocalHumedo\output`.
- **Etiquetado:** seleccione los elementos en la vista activa y pulse el botón correspondiente. Los tramos rectos de más de 15 cm reciben una etiqueta 20 cm por encima del eje, que se sube hasta 100 cm con directriz, o se pone debajo, si hace falta.
- **Planos:** muestra un resumen (vistas, láminas, niveles, etiquetas disponibles y datos que faltan) antes de crear nada; al repetirlo sustituye solo lo que generó antes.
- **Región de sectorización:** usa el CAD seleccionado (o el único de la vista); cada polilínea cerrada se convierte en una región rellenada del tipo «REG - <capa>».

## 3.9 Problemas frecuentes

| Mensaje o síntoma | Solución |
|---|---|
| «En este plano no hay nada que modele el botón …» | El plano es de otra instalación; el detalle dice qué botón modela cada capa y cada bloque. |
| «El proyecto no tiene tipos de …» | Use la plantilla «Sistemas» o Gestionar › Transferir normas de proyecto. |
| Todo sale diminuto o enorme | Corrija las unidades del archivo (UNIDADES / INSUNITS). |
| Fallan las uniones | El informe dice qué familia de unión le falta al tipo. |
