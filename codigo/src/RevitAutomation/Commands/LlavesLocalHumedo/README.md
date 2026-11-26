# Llaves de local húmedo

Botón independiente en RevitAutomation > Instalaciones > Llaves de local húmedo.

1. Abra la planta definitiva con el DWG/DXF vinculado. Seleccione el vínculo; si solo hay uno se usa directamente y si hay varios se pide elegirlo.
2. Elija la combinación exacta de bloque y capa que representa las llaves. Se procesan todas sus apariciones en el archivo, también fuera del recorte de la vista. Ejecute de nuevo para otras combinaciones o familias.
3. Elija una familia y tipo cargados de Accesorios de tubería, basada en nivel y sin anfitrión. No se presupone que el primer accesorio sea una llave.
4. Compruebe las unidades, la altura del origen de la familia sobre el nivel (propuesta inicial editable: 1000 mm) y el giro adicional respecto al bloque.
5. Revise el resumen y confirme. Se coloca el origen de la familia en la inserción del bloque, respetando desplazamiento y giro del vínculo. El vínculo debe ser horizontal y no estar reflejado. Antes de modificar se verifica su coincidencia con el archivo.

La altura es común al lote y sustituye la Z del CAD. El giro adicional permite adaptar el eje de la familia al del símbolo CAD. Los tamaños los determina el tipo de familia elegido, no la escala del bloque. No se leen marcas hechas con líneas sueltas, textos, DWF ni referencias externas sin contenido. El punto base del bloque y el origen de la familia deben representar la misma posición de la llave.

Esta primera versión coloca familias reales; no corta tuberías, no inserta válvulas en una red ni verifica conexiones hidráulicas. No cambia diámetros ni elementos existentes. Los accesorios ya presentes a menos de 1 mm se omiten, incluidos los de ejecuciones anteriores; cambiar altura o mover el CAD puede producir nuevas posiciones y no reubica las llaves anteriores.

Cada colocación fallida se revierte individualmente. Un Ctrl+Z deshace el lote completo. Tras confirmar, se seleccionan las llaves creadas y se genera un CSV con colocadas, omitidas, desviaciones y avisos de lectura en Documentos\RevitAutomation\LlavesLocalHumedo\output. La carpeta input queda preparada para ejemplos; la entrada efectiva es el archivo del CAD vinculado.

Pendiente de validación dentro de Revit con el CAD y las familias reales de la tarea 20. Las pruebas externas no demuestran la colocación ni la conexión en un proyecto real.
