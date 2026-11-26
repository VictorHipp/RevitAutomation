# 2. Instalación

[← Índice](../../README.es.md) · [English](../en/02-installation.md)

## 2.1 Instalar con `Instalar_RevitAutomation.exe`

El instalador **no** se guarda en el repositorio: se genera con `codigo\CREAR_INSTALADOR.cmd` (paso a paso en [Desarrollo § 4.3](04-desarrollo.md#43-crear-el-instalador-paso-a-paso); necesita los SDK de .NET 10 y 8 y las 12 familias de uniones). Queda junto a la carpeta `codigo`.

1. Cierre Revit.
2. Doble clic en `Instalar_RevitAutomation.exe`.
3. La ventana muestra Revit 2023–2027 y marca las versiones encontradas en el equipo (`C:\Program Files\Autodesk\Revit <año>\Revit.exe`).
4. Si quiere, indique la carpeta de familias `.rfa` de su oficina. Se añade a `%APPDATA%\CadMepRevit\bibliotecas.txt` y se puede editar más tarde.
5. Pulse **Instalar**.
6. Abra Revit. La primera vez preguntará si quiere cargar «RevitAutomation»: elija **Cargar siempre**.

Datos de la instalación:

- Se instala **solo para el usuario actual** y no pide permisos de administrador.
- Destino: `%APPDATA%\Autodesk\Revit\Addins\<año>\RevitAutomation\` y el manifiesto `RevitAutomation.addin`.
- Aparece en «Aplicaciones instaladas» de Windows para poder desinstalarlo desde ahí.
- SmartScreen puede avisar porque el `.exe` no está firmado: «Más información» → «Ejecutar de todas formas».

### Qué quita antes

Antes de instalar, y solo para el usuario actual, quita el **RevitAutomation antiguo** (y su módulo «CAD → Revit») y el **CAD MEP antiguo**, reconocidos por el identificador o la clase de su manifiesto. **No** toca otros complementos, los ajustes de las herramientas en `Documentos\RevitAutomation` ni las reglas aprendidas de CAD-REV en `%APPDATA%\CadMepRevit`. Si encuentra uno de los antiguos instalado para todos los usuarios, solo avisa, porque quitarlo necesita permisos de administrador.

## 2.2 Instalación sin ventana

```powershell
Instalar_RevitAutomation.exe /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"C:\Oficina\Familias"]
Instalar_RevitAutomation.exe /desinstalar /silencioso
```

- El código de salida `0` indica que todo ha ido bien.
- El registro se guarda en `%LOCALAPPDATA%\RevitAutomation\instalacion.txt`.
- `/destino:"carpeta"` es solo para pruebas: instala en esa carpeta en lugar de en Revit y no toca el registro de Windows.

## 2.3 Desinstalar

Cualquiera de estas opciones:

- «Aplicaciones instaladas» de Windows → RevitAutomation → Desinstalar.
- Ejecutar `Instalar_RevitAutomation.exe` y pulsar **Desinstalar**.
- `codigo\desinstalar.cmd` (usa la copia del instalador que se guarda al instalar en `%LOCALAPPDATA%\RevitAutomation\Instalador\`).

Desinstalar nunca modifica sus proyectos de Revit ni los ajustes de las herramientas.

## 2.4 Instalar directamente desde el código

`codigo\instalar.cmd` genera el instalador (pruebas + compilación + paquete) y lo ejecuta sin ventana. Cierre Revit antes. Admite una lista de versiones opcional:

```powershell
.\instalar.cmd 2025,2027
```

## 2.5 Archivos que usa el complemento en el equipo

| Ruta | Contenido |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<año>\RevitAutomation\` | Complemento instalado (DLL, guía, ejemplos, `Familias` de reserva) |
| `%APPDATA%\CadMepRevit\bibliotecas.txt` | Bibliotecas de familias donde busca CAD-REV, una carpeta por línea (las de red se tratan como solo lectura) |
| `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` | Capas y bloques aprendidos con «¿Qué es esto?»; bórrelo para empezar de cero |
| `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt` | Último informe de CAD-REV (copias con fecha en `informes\`) |
| `%LOCALAPPDATA%\CadMepRevit\indice_familias.txt` | Índice en caché de las bibliotecas de familias |
| `%LOCALAPPDATA%\RevitAutomation\instalacion.txt` | Registro de la última instalación o desinstalación sin ventana |
| `Documentos\RevitAutomation\<herramienta>\` | Ajustes, entradas e informes (`output\`) de las herramientas copiadas |

## 2.6 Problemas frecuentes

| Problema | Solución |
|---|---|
| No aparece la pestaña «RevitAutomation» | Cierre Revit, ejecute de nuevo el instalador, abra Revit y elija «Cargar siempre». |
| «No se encuentra Revit…» | Revit no está en `C:\Program Files\Autodesk`. Marque la versión igualmente o use `/versiones:`. |
| No se crean las uniones | El tipo de tubería o conducto no tiene uniones en sus Preferencias de enrutamiento. El complemento intenta asignarlas desde el proyecto, las bibliotecas y la carpeta `Familias` de reserva; el informe dice cuál falta. Use un proyecto creado con la plantilla «Sistemas». |
