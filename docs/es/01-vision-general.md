# 1. Visión general

[← Índice](../../README.es.md) · [English](../en/01-overview.md)

RevitAutomation es un complemento de Autodesk Revit 2023–2027 escrito en C#. Al arrancar Revit crea una sola pestaña en la cinta, **«RevitAutomation»**. Cada sección de la pestaña es un panel con un desplegable que contiene sus botones.

Está pensado para técnicos de instalaciones (fontanería, saneamiento, climatización, electricidad, iluminación y protección contra incendios). La interfaz, las ventanas y los informes están en español.

## 1.1 La pestaña de la cinta

| Panel | Desplegable | Botones | Código |
|---|---|---|---|
| CAD-REV | CAD-REV | Tuberías · Conductos · Bandejas · Luminarias · Equipos y PCI | `src/RevitAutomation/Commands/CadRevCommands.cs` → `src/CadMepRevit` |
| Instalaciones | Salas técnicas | 1 · Contadores de agua · 2 · Electricidad · 3 · PCI · 4 · Fontanería | `Commands/SalasTecnicas` |
| Instalaciones | Fontanería | Patinillo de fontanería · Aislamiento RITE · Llaves de local húmedo | `Commands/Patinillo`, `Commands/Aislamiento`, `Commands/LlavesLocalHumedo` |
| Etiquetado | Etiquetado | Tuberías · Conductos · Bandejas · Familias · Luminarias (seleccionadas) | `Commands/PipeTagging` |
| Documentación | Planos | Fontanería · Climatización · Electricidad · Iluminación · Genéricas y PCI · Conjunto completo | `Commands/Planos` |
| Documentación | Exportar | Exportar PDF · Exportar CAD | `Commands/PdfExportCommand.cs`, `Commands/CadExportCommand.cs` |
| Región | Región | Región de sectorización | `Commands/Regiones` |

## 1.2 CAD-REV: del plano CAD al modelo de Revit

CAD-REV (antes «CAD MEP») lee un plano DWG o DXF y modela una instalación por botón. Cada botón lee el plano entero pero modela **solo lo suyo**, así que se pueden pulsar varios con el mismo plano sin duplicar elementos.

| Botón | Qué modela |
|---|---|
| **Tuberías** | Tuberías y bajantes, aparatos sanitarios, arquetas, equipos de agua y accesorios de tubería. Crea codos, tes y reducciones. Lee las pendientes (`%`, `‰`, flechas, cotas). Los encuentros de cuatro tubos se señalan para que los resuelva el técnico. |
| **Conductos** | Conductos con codos, tes, cruces y transiciones. Rejillas, difusores, compuertas y equipos de aire a la cota y con el giro de su conducto. |
| **Bandejas** | Bandejas de cables y tubos eléctricos, los dos con sus uniones (codos, tes, cruces y reducciones). |
| **Luminarias** | Luminarias (downlights, tiras LED, emergencias…), interruptores y detectores de presencia, también las dibujadas con líneas sueltas en vez de bloques. |
| **Equipos y PCI** | Extintores, BIE, detectores, pulsadores, sirenas, señales, cuadros eléctricos, tomas y otros dispositivos. |

Comportamiento principal:

- **Encima del CAD.** Si el mismo plano está vinculado o importado en el proyecto, todo se coloca encima, aunque el vínculo esté movido o girado. Si falta, el complemento lo vincula.
- **Medidas del plano.** Las cotas, los rótulos `Ø`/`DN`, las directrices y un DWF opcional del mismo plano dan el tamaño de los tramos.
- **Regla «sigue el techo».** Los conductos, bandejas, tubos eléctricos y tuberías horizontales sin cota escrita dejan su parte superior 100 mm por debajo del forjado de encima (también en modelos vinculados) y suben o bajan con dos codos donde cambia el forjado.
- **No se inventan valores.** Si el plano no da un dato (p. ej. el alto de un conducto rectangular), se usa un valor por defecto marcado como `SUPUESTO` en el informe.
- **Aprende lo desconocido.** Las capas y bloques que no reconoce abren la ventana «¿Qué es esto?»; las respuestas se recuerdan para los planos siguientes.
- **Un solo Ctrl+Z y un informe.** Todo va en un `TransactionGroup`; el informe se muestra y se guarda en `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt`.

## 1.3 Las demás herramientas

| Herramienta | Qué hace |
|---|---|
| **Salas técnicas** | Modela una sala técnica desde su CAD vinculado en la vista en planta activa: cada contorno cerrado con un rótulo se convierte en el equipo que dice el rótulo (depósitos, grupos, cuadros, contadores…), con su forma y alto habitual, y sus tuberías o bandejas. Cuatro variantes: contadores de agua (1,00 m), electricidad (2,70 m), PCI (2,70 m) y fontanería (2,50 m). |
| **Patinillo de fontanería** | Modela el patinillo vertical de agua fría y ACS: las subidas desde los contadores de planta baja y, en cada planta, el peine hacia cada vivienda bajo el forjado. Muestra una matriz previa y crea una vista 3D y un alzado. |
| **Aislamiento RITE** | Aísla las tuberías seleccionadas, con sus piezas y accesorios, según el RITE (IT 1.2.4.2.1.2, procedimiento simplificado): identifica el servicio por el sistema de Revit, calcula el espesor según diámetro, temperatura y conductividad y lo redondea al espesor comercial. Nunca reduce un aislamiento existente. |
| **Llaves de local húmedo** | Coloca familias de llave en las inserciones de bloque del CAD vinculado (bloque y capa elegidos por el usuario), respetando el desplazamiento y el giro del vínculo. Solo colocación, sin conexión hidráulica. |
| **Etiquetado** | Etiqueta las tuberías, conductos, bandejas, familias o luminarias seleccionadas con las etiquetas del propio modelo, sin solapes (aparta la etiqueta o añade directriz; informa de lo que no cabe). |
| **Planos** | Genera vistas en planta por nivel y disciplina, colores por sistema, etiquetas, tablas, leyendas y láminas con cajetín; muestra antes un resumen y guarda un PDF combinado en el Escritorio. No modifica los elementos del modelo. |
| **Exportar PDF / CAD** | Exporta conjuntos de láminas o vistas a un PDF combinado, o a DWG, DXF o DGN. |
| **Región de sectorización** | Calca las zonas de colores del CAD vinculado como regiones rellenadas en la planta activa: cada polilínea cerrada se convierte en una región con el color de su capa. |

Las herramientas de *Instalaciones*, *Etiquetado*, *Documentación* y *Región* se copiaron sin cambios de funcionamiento de un proyecto RevitAutomation anterior; sus ajustes están en `Documentos\RevitAutomation\<herramienta>`.

## 1.4 Versiones de Revit

| Revit | Plataforma .NET | Notas |
|---|---|---|
| 2023, 2024 | .NET Framework 4.8 (`net48`) | Capa de compatibilidad para las API de .NET que faltan y el `ElementId` de 32 bits |
| 2025, 2026 | .NET 8 (`net8.0-windows`) | 2026 carga el complemento en un contexto aislado |
| 2027 | .NET 10 (`net10.0-windows`) | Contexto aislado |

La lógica de las herramientas es la misma en las cinco versiones. Cómo se consigue: [Arquitectura](05-arquitectura.md).
