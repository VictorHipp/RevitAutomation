# 2. Descarga e instalación

[← Índice](../../README.es.md) · [English](../en/02-installation.md)

El instalador `Instalar_RevitAutomation.exe` **no se publica**, porque lleva familias de la biblioteca de Autodesk. Cada PC lo crea a partir del código. Por eso hay dos partes:

- **Parte A. Preinstalación**: se hace **una vez por PC**. Descarga el proyecto, instala lo necesario y crea el instalador.
- **Parte B. Instalación**: instalar el complemento en Revit, actualizarlo y desinstalarlo.

Resumen. Solo hay que escribir **un comando**, el del paso 2; todo lo demás es doble clic:

| Paso | Qué hacer | Cómo |
|---|---|---|
| [2.1](#21-instalar-git) | Instalar Git | Descargarlo de la web y ejecutarlo |
| [2.2](#22-descargar-el-repositorio) | Descargar el proyecto | `git clone https://github.com/VictorHipp/RevitAutomation.git` |
| [2.3](#23-preparar-el-pc-sdk-de-net-10-y-8) | Instalar los SDK de .NET 10 y 8 (y .NET Framework 4.8 si falta) | Doble clic en `codigo\PREPARAR_PC.cmd` |
| [2.4](#24-conseguir-las-12-familias-de-uniones) | Poner las 12 familias de uniones (recomendado) | Copiarlas en `codigo\familias_uniones\`, o dejar que `CREAR_INSTALADOR.cmd` las busque |
| [2.5](#25-crear-el-instalador) | Crear el instalador | Doble clic en `codigo\CREAR_INSTALADOR.cmd` |
| [2.6](#26-instalar-en-revit) | Instalar en Revit | Doble clic en `Instalar_RevitAutomation.exe` |

## Parte A. Preinstalación (una vez por PC)

### 2.1 Instalar Git

Git sirve para descargar el proyecto y, más adelante, sus actualizaciones. Si ya lo tiene, salte este paso.

1. Entre en <https://git-scm.com/download/win>. La descarga empieza sola (si no, pulse *Click here to download*).
2. Ejecute el archivo descargado y pulse **Next** en todas las pantallas y después **Install**. Las opciones por defecto valen.

### 2.2 Descargar el repositorio

1. En el Explorador de archivos, abra la carpeta donde quiera guardar el proyecto. Mejor una carpeta local que **no** se sincronice con OneDrive, porque la compilación crea muchos archivos.
2. Haga clic en la barra de direcciones, escriba `powershell` y pulse **Intro**. Se abre una ventana azul en esa carpeta.
3. Escriba (o pegue) este comando y pulse **Intro**:

   ```powershell
   git clone https://github.com/VictorHipp/RevitAutomation.git
   ```

4. Cuando termine, cierre la ventana. Se ha creado la carpeta `RevitAutomation`; el código está en `RevitAutomation\codigo`.

### 2.3 Preparar el PC (SDK de .NET 10 y 8)

Abra la carpeta `RevitAutomation\codigo` y haga **doble clic en `PREPARAR_PC.cmd`**.

1. Muestra qué hay instalado (`[OK]`) y qué falta (`[FALTA]`): SDK de .NET 10, SDK de .NET 8, .NET Framework 4.8 (runtime), Developer Pack de .NET Framework 4.8 y Git.
2. Si GitHub CLI no está, pregunta: «¿Va a desarrollar el complemento o subir cambios a GitHub? (S/N)». Para solo instalar el complemento, responda **N**.
3. Instala lo que falta sin más clics. Windows pide permiso de administrador **una vez**: pulse **«Sí»**. El progreso se ve en otra ventana.
4. Comprueba las 12 familias de uniones ([2.4](#24-conseguir-las-12-familias-de-uniones)): copia las que encuentre y avisa (`[AVISO]`) de las que falten; no es un error. Con `PREPARAR_PC.cmd -Familias <carpeta>` las busca también en esa carpeta.
5. Al terminar muestra **«TODO LISTO»**, o qué ha fallado y cómo corregirlo. Lo ya instalado se salta, así que puede ejecutarlo las veces que quiera.

Usa `winget` si el PC lo tiene. Si no, descarga el instalador oficial desde su enlace directo, comprueba que está firmado por su fabricante y lo ejecuta. También activa las rutas largas de Git (`core.longpaths`).

Si prefiere instalarlo a mano, esto es lo que hace falta:

| Herramienta | Para qué | ¿Obligatoria? | Descarga oficial | O con winget |
|---|---|---|---|---|
| **SDK de .NET 10** (el *SDK*, no solo el runtime) | Compilar el complemento para Revit 2027 (`net10.0-windows`) y crear el instalador. También compila las versiones de .NET Framework 4.8 (2023/2024): sus ensamblados de referencia se descargan de NuGet | **Sí** | <https://dotnet.microsoft.com/download/dotnet/10.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` |
| **SDK de .NET 8** | Las versiones anteriores a 2027: Revit 2025 y 2026 (`net8.0-windows`) y las pruebas del motor (.NET 8). Se instala junto al SDK 10, no lo sustituye | **Sí** | <https://dotnet.microsoft.com/download/dotnet/8.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.8 -e --source winget` |
| **.NET Framework 4.8** (runtime) | Con él funcionan Revit 2023 y 2024 y el instalador. Viene con Windows 10 (versión 1903 o posterior) y Windows 11: solo se instala si falta | **Sí** (normalmente ya está) | <https://dotnet.microsoft.com/download/dotnet-framework/net48> → *Runtime* | — (winget solo tiene el 4.8.1, que no admite los Windows 10 antiguos) |
| **Developer Pack de .NET Framework 4.8** | Los ensamblados de referencia de 4.8 para compilar 2023 y 2024 sin depender de NuGet. Sin él se compila igual: el SDK descarga los mismos de NuGet. Tiene que ser el **4.8** (el 4.8.1 no sirve) | Recomendado | <https://dotnet.microsoft.com/download/dotnet-framework/net48> → *Developer Pack* | `winget install --id Microsoft.DotNet.Framework.DeveloperPack_4 --version 4.8 -e --source winget` |
| **Git para Windows** | Descargar el proyecto (`git clone`) y sus actualizaciones (`git pull`) | **Sí** | <https://git-scm.com/download/win> | `winget install --id Git.Git -e --source winget` |
| GitHub CLI | Iniciar sesión en GitHub para subir cambios (`gh auth login`) | Solo para desarrollar | <https://cli.github.com> | `winget install --id GitHub.cli -e --source winget` |

Notas:

- Los dos SDK de .NET tienen que estar en la misma carpeta (la normal es `C:\Program Files\dotnet`). Es lo que hacen los instaladores oficiales, `winget` y `PREPARAR_PC.cmd`.
- La primera compilación necesita **Internet**: NuGet descarga ACadSharp, los paquetes de la API de Revit, etc.
- No hace falta tener Revit para crear el instalador: sin Revit, la API se toma de NuGet.

### 2.4 Conseguir las 12 familias de uniones

El instalador lleva las familias de uniones de reserva que haya en `codigo\familias_uniones\`. **Son contenido de Autodesk y no están en el repositorio**. Sin ellas el instalador se crea igualmente, pero CAD-REV pierde la reserva de último recurso: en un proyecto sin familias de uniones cargadas, Revit no crea esas uniones y lo indica en el informe. La lista de los 12 archivos está en [`codigo/familias_uniones/LEEME.md`](../../codigo/familias_uniones/LEEME.md).

**Búsqueda automática.** `PREPARAR_PC.cmd` y `CREAR_INSTALADOR.cmd` buscan las que falten y las copian a `codigo\familias_uniones\`. Buscan, por este orden:
1. en la carpeta indicada con `-Familias <carpeta>`;
2. en la variable de entorno `REVITAUTOMATION_FAMILIAS`;
3. en las bibliotecas de Revit instaladas en el equipo (`C:\ProgramData\Autodesk\RVT <año>\Libraries`, de la más nueva a la más antigua).

Si aun así falta alguna, **avisa y crea el instalador sin ella**. Para la oficina, lo más cómodo es guardar una vez los 12 `.rfa` en una carpeta del servidor y usar:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Familias '<carpeta del servidor>'
```

No se pueden descargar automáticamente: Autodesk solo las da con la sesión de su cuenta iniciada, o desde Revit.

Si la búsqueda no las encuentra, elija una de estas opciones (por orden de preferencia):

**A. Desde su propia copia (lo más rápido).** Guarde una copia privada de la carpeta `familias_uniones` (memoria USB, nube privada) y, después de descargar el repositorio, copie los 12 `.rfa` en `codigo\familias_uniones\`.

**B. Descargar la biblioteca de contenido de Revit desde su cuenta de Autodesk.** Necesita una licencia de Revit.

1. Entre en <https://manage.autodesk.com> e inicie sesión.
2. **Productos y servicios** (menú de la izquierda, o su avatar arriba a la derecha) → **Revit** → elija el **año** → **Bibliotecas** (*Libraries*).
3. Descargue el paquete de contenido de **España** (*Spain* / *Español*; el nombre exacto del paquete puede variar) y ejecute el `.exe` descargado.
4. El contenido se instala en `C:\ProgramData\Autodesk\RVT <año>\Libraries\` (la carpeta `ProgramData` está oculta: escriba la ruta en la barra de direcciones del Explorador).
5. Busque los 12 archivos por su nombre (el cuadro de búsqueda del Explorador sirve) y cópielos en `codigo\familias_uniones\`. O, en PowerShell desde la carpeta `codigo`:

   ```powershell
   $lib  = 'C:\ProgramData\Autodesk\RVT 2027\Libraries'          # cambie el año
   $dest = '.\familias_uniones'
   'M_Codo - Genérico','M_Te - Genérica','M_Cruz - Genérica','M_Transición - Genérica',
   'M_Codo rectangular - Radio 1','M_Te rectangular - Recto','M_Cruz rectangular -  Recta','M_Transición rectangular - Ángulo 2',
   'M_Codo redondo 1','M_Te redonda - Recto','M_Cruz redonda - Recto','M_Transición redonda - Ángulo' |
     ForEach-Object { Get-ChildItem $lib -Recurse -Filter "$_.rfa" | Select-Object -First 1 | Copy-Item -Destination $dest -Verbose }
   ```

**C. Desde Revit, una a una (Revit 2021.1 o posterior).** Abra cualquier proyecto → pestaña **Insertar** → **Cargar familia de Autodesk** → busque el nombre de la familia (p. ej. `M_Codo - Genérico`) → **Cargar**. Después, en el **Navegador de proyectos** → *Familias*, clic derecho sobre la familia → **Guardar…** y guárdela como `.rfa` en `codigo\familias_uniones\` con el nombre exacto.

**D. Usar familias equivalentes.** También valen otras familias de codo, te, cruz y transición para tubería, conducto rectangular y conducto redondo. CAD-REV reconoce las familias de unión por el significado de su nombre (`Core/FittingNames.cs`) y comprueba su categoría y su tipo de pieza al cargarlas.

### 2.5 Crear el instalador

1. En la carpeta `codigo`, haga **doble clic en `CREAR_INSTALADOR.cmd`**.
2. Si falta alguno de los SDK de .NET, pregunta «¿Instalarlo ahora? (S/N)». Responda **S** y continúa solo.
3. Espere. Pasa todas las pruebas y después compila las cinco versiones de Revit. La primera vez tarda varios minutos porque descarga los paquetes de NuGet.
4. Al terminar muestra `INSTALADOR CREADO correctamente`, la ruta, el tamaño y el SHA-256 del archivo. El instalador queda en la carpeta `RevitAutomation`, junto a la carpeta `codigo`:

   ```
   RevitAutomation\Instalar_RevitAutomation.exe
   ```

Puede copiar ese `.exe` a cualquier otro PC con Revit e instalarlo allí: ese PC no necesita .NET, Git ni la preinstalación.

`CREAR_INSTALADOR.cmd` no instala el complemento (solo los SDK de .NET, si faltan y usted responde «S»). Opciones, desde PowerShell en la carpeta `codigo`:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -SinPruebas            # sin pasar las pruebas
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Salida C:\Temp\salida  # deja el .exe en otra carpeta
```

#### Si algo falla

| Mensaje | Causa y solución |
|---|---|
| `git` no se reconoce como comando | Git no está instalado, o la ventana se abrió antes de instalarlo. Haga el paso [2.1](#21-instalar-git) y abra una ventana nueva. |
| «Falta el SDK de .NET 10 / 8» o «No se encuentra el SDK de .NET …» | Falta uno de los dos SDK, solo está el runtime, o están en carpetas distintas. Responda «S» a la pregunta o haga doble clic en `PREPARAR_PC.cmd` ([2.3](#23-preparar-el-pc-sdk-de-net-10-y-8)). |
| `PREPARAR_PC.cmd`: «No se ha podido abrir la instalación como administrador» | Ha pulsado «No» en el aviso de Windows, o su usuario no puede instalar programas. Repita y pulse «Sí», o pida a informática que instale lo que indica. |
| `PREPARAR_PC.cmd`: «La firma digital del archivo descargado no es válida…» | El archivo descargado no es el original (descarga cortada o un proxy que lo modifica). No se ejecuta. Repita o descárguelo desde el enlace oficial que indica. |
| «Las pruebas automáticas han fallado» | Ha fallado una prueba; lea las líneas anteriores. Para generar igualmente: `crear_instalador.ps1 -SinPruebas`. |
| «La compilación para Revit <año> ha fallado» | Normalmente falta Internet para NuGet, o hay un error en el código. Compruebe la conexión y los mensajes del compilador. |
| «AVISO: Faltan N de las 12 familias de uniones» | No están en `codigo\familias_uniones\` ni la búsqueda automática las ha encontrado. El instalador se crea igualmente, sin ellas. Si las quiere dentro, póngalas ([2.4](#24-conseguir-las-12-familias-de-uniones)) y vuelva a crearlo. |
| «… contiene '…' (datos de este equipo)» | Un archivo del paquete lleva su nombre de usuario o de equipo. Compile sin símbolos de depuración (el script lo hace por defecto). |

## Parte B. Instalación

### 2.6 Instalar en Revit

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

#### Qué quita antes

Antes de instalar, y solo para el usuario actual, quita el **RevitAutomation antiguo** (y su módulo «CAD → Revit») y el **CAD MEP antiguo**, reconocidos por el identificador o la clase de su manifiesto. **No** toca otros complementos, los ajustes de las herramientas en `Documentos\RevitAutomation` ni las reglas aprendidas de CAD-REV en `%APPDATA%\CadMepRevit`. Si encuentra uno de los antiguos instalado para todos los usuarios, solo avisa, porque quitarlo necesita permisos de administrador.

### 2.7 Instalación sin ventana

```powershell
Instalar_RevitAutomation.exe /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"C:\Oficina\Familias"]
Instalar_RevitAutomation.exe /desinstalar /silencioso
```

- El código de salida `0` indica que todo ha ido bien.
- El registro se guarda en `%LOCALAPPDATA%\RevitAutomation\instalacion.txt`.
- `/destino:"carpeta"` es solo para pruebas: instala en esa carpeta en lugar de en Revit y no toca el registro de Windows.

### 2.8 Actualizar y desinstalar

**Actualizar a una versión nueva:** traiga los cambios con `git pull` en la carpeta `RevitAutomation` (abra PowerShell en ella como en [2.2](#22-descargar-el-repositorio)). Después haga doble clic en `CREAR_INSTALADOR.cmd` y en el nuevo `Instalar_RevitAutomation.exe`. La instalación nueva sustituye a la anterior; no hace falta desinstalar antes.

**Desinstalar**, con cualquiera de estas opciones:

- «Aplicaciones instaladas» de Windows → RevitAutomation → Desinstalar.
- Ejecutar `Instalar_RevitAutomation.exe` y pulsar **Desinstalar**.
- `codigo\desinstalar.cmd` (usa la copia del instalador que se guarda al instalar en `%LOCALAPPDATA%\RevitAutomation\Instalador\`).

Desinstalar nunca modifica sus proyectos de Revit ni los ajustes de las herramientas.

### 2.9 Instalar directamente desde el código

`codigo\instalar.cmd` genera el instalador (pruebas + compilación + paquete) y lo ejecuta sin ventana. Cierre Revit antes. Admite una lista de versiones opcional:

```powershell
.\instalar.cmd 2025,2027
```

### 2.10 Archivos que usa el complemento en el equipo

| Ruta | Contenido |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<año>\RevitAutomation\` | Complemento instalado (DLL, guía, ejemplos, `Familias` de reserva) |
| `%APPDATA%\CadMepRevit\bibliotecas.txt` | Bibliotecas de familias donde busca CAD-REV, una carpeta por línea (las de red se tratan como solo lectura) |
| `%APPDATA%\CadMepRevit\reglas_reconocimiento.json` | Capas y bloques aprendidos con «¿Qué es esto?»; bórrelo para empezar de cero |
| `%LOCALAPPDATA%\CadMepRevit\ultimo_informe.txt` | Último informe de CAD-REV (copias con fecha en `informes\`) |
| `%LOCALAPPDATA%\CadMepRevit\indice_familias.txt` | Índice en caché de las bibliotecas de familias |
| `%LOCALAPPDATA%\RevitAutomation\instalacion.txt` | Registro de la última instalación o desinstalación sin ventana |
| `Documentos\RevitAutomation\<herramienta>\` | Ajustes, entradas e informes (`output\`) de las herramientas copiadas |

### 2.11 Problemas frecuentes

| Problema | Solución |
|---|---|
| No aparece la pestaña «RevitAutomation» | Cierre Revit, ejecute de nuevo el instalador, abra Revit y elija «Cargar siempre». |
| «No se encuentra Revit…» | Revit no está en `C:\Program Files\Autodesk`. Marque la versión igualmente o use `/versiones:`. |
| No se crean las uniones | El tipo de tubería o conducto no tiene uniones en sus Preferencias de enrutamiento. El complemento intenta asignarlas desde el proyecto, las bibliotecas y la carpeta `Familias` de reserva; el informe dice cuál falta. Use un proyecto creado con la plantilla «Sistemas». |
| SmartScreen bloquea el `.exe` | No está firmado: «Más información» → «Ejecutar de todas formas». |
