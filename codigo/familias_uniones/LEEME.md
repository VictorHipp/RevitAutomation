# familias_uniones — familias de uniones de reserva

[English](README.md) · **Español**

El instalador copia los `.rfa` de esta carpeta en la carpeta `Familias` del complemento. CAD-REV los usa como **última biblioteca** para codos, tes, cruces y transiciones cuando ni el proyecto ni las bibliotecas de la oficina tienen ninguna (si no, Revit no puede crear las uniones).

## Por qué no están en Git

Son familias de la **biblioteca de contenido de Autodesk Revit** (biblioteca métrica de España): pertenecen a Autodesk y no están cubiertas por la licencia MIT de este proyecto. El `.gitignore` de la raíz las excluye (`codigo/familias_uniones/*.rfa`). Vea [Propiedad intelectual](../../docs/es/08-propiedad-intelectual.md).

## Qué tiene que haber aquí antes de ejecutar `CREAR_INSTALADOR.cmd`

`crear_instalador.ps1` se detiene con un error si esta carpeta no tiene ningún `.rfa`. El juego original es:

| Archivo | Pieza |
|---|---|
| `M_Codo - Genérico.rfa` | Codo genérico de tubería |
| `M_Te - Genérica.rfa` | Te genérica de tubería |
| `M_Cruz - Genérica.rfa` | Cruz genérica de tubería |
| `M_Transición - Genérica.rfa` | Transición genérica de tubería |
| `M_Codo rectangular - Radio 1.rfa` | Codo de conducto rectangular |
| `M_Te rectangular - Recto.rfa` | Te de conducto rectangular |
| `M_Cruz rectangular -  Recta.rfa` | Cruz de conducto rectangular (ojo: dos espacios) |
| `M_Transición rectangular - Ángulo 2.rfa` | Transición de conducto rectangular |
| `M_Codo redondo 1.rfa` | Codo de conducto redondo |
| `M_Te redonda - Recto.rfa` | Te de conducto redondo |
| `M_Cruz redonda - Recto.rfa` | Cruz de conducto redondo |
| `M_Transición redonda - Ángulo.rfa` | Transición de conducto redondo |

## Cómo descargarlas y dónde

Instrucciones completas, con un script que las copia solas: [Desarrollo § 4.2](../../docs/es/04-desarrollo.md#42-conseguir-las-12-familias-de-uniones). En resumen:

1. **Desde su copia privada** (USB, nube privada): copie los 12 archivos aquí después del `git clone`.
2. **Desde su cuenta de Autodesk:** <https://manage.autodesk.com> → **Productos y servicios** → **Revit** → año → **Bibliotecas** → descargue el paquete de **España** y ejecútelo. Se instala en `C:\ProgramData\Autodesk\RVT <año>\Libraries\` (carpeta oculta); copie desde ahí los 12 archivos.
3. **Desde Revit (2021.1 o posterior):** **Insertar › Cargar familia de Autodesk** → busque el nombre → **Cargar**; después, en el Navegador de proyectos, clic derecho sobre la familia → **Guardar…** como `.rfa` en esta carpeta.
4. **Familias equivalentes** con otros nombres: CAD-REV reconoce las familias de unión por el significado de su nombre (`Core/FittingNames.cs`) y comprueba su categoría y su tipo de pieza al cargarlas.

Después, genere el instalador: [Desarrollo § 4.3](../../docs/es/04-desarrollo.md#43-crear-el-instalador-paso-a-paso).
