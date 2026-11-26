# 4. Desarrollo

[← Índice](../../README.es.md) · [English](../en/04-development.md)

Salvo que se indique otra cosa, todos los comandos se ejecutan en **PowerShell** desde la carpeta `codigo`.

## 4.1 Herramientas que hay que descargar

**La forma más fácil, sin consola:** haga doble clic en **`codigo\PREPARAR_PC.cmd`**. Comprueba qué falta de la tabla siguiente (SDK de .NET 10 y 8, Git para Windows y GitHub CLI), salta lo que ya esté instalado e instala el resto sin más clics: Windows pide permiso de administrador una vez (pulse «Sí»). Usa `winget` si el PC lo tiene; si no, descarga el instalador oficial desde su enlace directo, comprueba que está firmado por su fabricante y lo ejecuta. Al terminar activa las rutas largas de Git y muestra «TODO LISTO» o qué ha fallado y cómo corregirlo.

Si prefiere instalarlo a mano, estas son las herramientas:

| Herramienta | Para qué | ¿Obligatoria? | Descarga oficial | O con winget |
|---|---|---|---|---|
| **SDK de .NET 10** (el *SDK*, no solo el runtime) | Compilar el complemento para Revit 2027 (`net10.0-windows`) y crear el instalador. También compila las versiones de .NET Framework 4.8 (2023/2024): sus ensamblados de referencia se descargan de NuGet | **Sí** | <https://dotnet.microsoft.com/download/dotnet/10.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` |
| **SDK de .NET 8** | Las versiones anteriores a 2027: Revit 2025 y 2026 (`net8.0-windows`) y las pruebas del motor (`tests/CadMepRevit.Tests`, .NET 8). Se instala junto al SDK 10, no lo sustituye | **Sí** | <https://dotnet.microsoft.com/download/dotnet/8.0> → *SDK* → Windows x64 | `winget install --id Microsoft.DotNet.SDK.8 -e --source winget` |
| **Git para Windows** | Descargar (`git clone`) y subir (`git push`) el repositorio | **Sí** (para trabajar con GitHub) | <https://git-scm.com/download/win> | `winget install --id Git.Git -e --source winget` |
| GitHub CLI | Iniciar sesión en GitHub desde la terminal (`gh auth login`) | Opcional | <https://cli.github.com> | `winget install --id GitHub.cli -e --source winget` |
| Visual Studio 2022 o posterior (Community es gratis) con la carga de trabajo *Desarrollo de escritorio de .NET*, o VS Code / Rider | Editar y depurar el código. Abra `codigo\CadMepRevit.sln` | Opcional | <https://visualstudio.microsoft.com/es/downloads/> | `winget install --id Microsoft.VisualStudio.2022.Community -e --source winget` |
| Autodesk Revit 2023–2027 | Probar dentro de Revit. **No hace falta para compilar**: sin Revit, la API se toma de NuGet | Opcional | Su cuenta de Autodesk | — |

Notas:

- Los dos SDK de .NET tienen que estar en la misma carpeta (la normal es `C:\Program Files\dotnet`). Es lo que hacen los instaladores oficiales, `winget` y `PREPARAR_PC.cmd`.
- `winget` viene con Windows 10/11 («Instalador de aplicación»). Ejecute los comandos en una ventana normal de PowerShell; algunos instaladores muestran un aviso de permisos de Windows.
- Cierre y vuelva a abrir PowerShell después de instalar para que encuentre los comandos nuevos.
- Compruebe la instalación:

  ```powershell
  dotnet --list-sdks     # deben aparecer una versión 10.x y una 8.x
  git --version
  ```

- La primera compilación necesita **Internet** (NuGet descarga ACadSharp, los paquetes de la API de Revit, etc.). En una terminal sin conexión, ponga `$env:NuGetAudit = 'false'`.

## 4.2 Conseguir las 12 familias de uniones

El instalador necesita las familias de uniones de reserva en `codigo\familias_uniones\`. **Son contenido de Autodesk y no están en el repositorio**, así que hay que ponerlas a mano. La lista de los 12 archivos está en [`codigo/familias_uniones/LEEME.md`](../../codigo/familias_uniones/LEEME.md).

Elija una de estas opciones (por orden de preferencia):

**A. Desde su propia copia (lo más rápido).** Guarde una copia privada de la carpeta `familias_uniones` (memoria USB, nube privada) y, después de clonar, copie los 12 `.rfa` en `codigo\familias_uniones\`.

**B. Descargar la biblioteca de contenido de Revit desde su cuenta de Autodesk.** Necesita una licencia de Revit.

1. Entre en <https://manage.autodesk.com> e inicie sesión.
2. **Productos y servicios** (menú de la izquierda, o su avatar arriba a la derecha) → **Revit** → elija el **año** → **Bibliotecas** (*Libraries*).
3. Descargue el paquete de contenido de **España** (*Spain* / *Español*; el nombre exacto del paquete puede variar) y ejecute el `.exe` descargado.
4. El contenido se instala en `C:\ProgramData\Autodesk\RVT <año>\Libraries\` (la carpeta `ProgramData` está oculta: escriba la ruta en la barra de direcciones del Explorador).
5. Busque los archivos por su nombre y cópielos en `codigo\familias_uniones\`:

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

`CREAR_INSTALADOR.cmd` se detiene con el mensaje «No se encuentran las familias de uniones» si la carpeta no tiene ningún `.rfa`.

## 4.3 Crear el instalador paso a paso

1. Instale los **SDK de .NET 10 y 8**: doble clic en `codigo\PREPARAR_PC.cmd` ([4.1](#41-herramientas-que-hay-que-descargar)). Si se le olvida, `CREAR_INSTALADOR.cmd` lo detecta y le pregunta si quiere instalarlos.
2. Ponga las **12 familias** en `codigo\familias_uniones\` ([4.2](#42-conseguir-las-12-familias-de-uniones)).
3. Abra la carpeta `codigo` en el Explorador y haga **doble clic en `CREAR_INSTALADOR.cmd`**. O, desde PowerShell:

   ```powershell
   cd <carpeta del repositorio>\codigo
   .\CREAR_INSTALADOR.cmd
   ```

4. Espere. Pasa todas las pruebas y después compila las cinco versiones de Revit. La primera vez tarda varios minutos porque descarga los paquetes de NuGet.
5. Al terminar muestra `INSTALADOR CREADO correctamente`, la ruta, el tamaño y el SHA-256 del archivo. El instalador queda en la **raíz del repositorio**, junto a la carpeta `codigo`:

   ```
   RevitAutomation\Instalar_RevitAutomation.exe
   ```

6. Para instalarlo, cierre Revit y haga doble clic en el `.exe` (vea [Instalación](02-instalacion.md)). Puede copiarlo a cualquier otro PC con Revit e instalarlo allí; ese PC no necesita .NET ni Git.

Opciones:

```powershell
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -SinPruebas            # sin pasar las pruebas
powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 -Salida C:\Temp\salida  # deja el .exe en otra carpeta
```

`CREAR_INSTALADOR.cmd` no instala el complemento en el equipo (solo los SDK de .NET si faltan y usted responde «S»). `instalar.cmd` hace lo mismo y además instala el complemento sin ventana para el usuario actual (cierre Revit antes).

### Qué hace el script

1. Busca una carpeta de .NET con el SDK 10 (o posterior) y el SDK 8 (`DOTNET_ROOT`, `%USERPROFILE%\.dotnet`, `C:\Program Files\dotnet` o el `PATH`). Si falta alguno, pregunta «¿Instalarlo ahora? (S/N)» y, con «S», ejecuta `preparar_pc.ps1 -SoloDotnet`.
2. Pasa las pruebas del motor (`tests/CadMepRevit.Tests`) y cada prueba de herramienta de `tests/RevitAutomation.Pruebas`.
3. Compila `RevitAutomation.csproj` para 2023, 2024 (`net48`), 2025, 2026 (`net8.0-windows`) y 2027 (`net10.0-windows`) **sin símbolos de depuración**, cada una en su carpeta `obj\paquete\<año>`, y quita los `RevitAPI*.dll` y los `.pdb`.
4. Añade la guía de usuario, los planos de ejemplo y las `Familias` de reserva.
5. Comprueba que ningún archivo del paquete contiene el nombre de usuario, la ruta del perfil ni el nombre del equipo.
6. Comprime el paquete, lo incrusta en `instalador/InstaladorCadMep.csproj` (WinForms, .NET Framework 4.8) y genera `Instalar_RevitAutomation.exe`.

### Si algo falla

| Mensaje | Causa y solución |
|---|---|
| «Falta el SDK de .NET 10 / 8» o «No se encuentra el SDK de .NET …» | Falta uno de los dos SDK, solo está el runtime, o están en carpetas distintas. Responda «S» a la pregunta o haga doble clic en `PREPARAR_PC.cmd` ([4.1](#41-herramientas-que-hay-que-descargar)). |
| `PREPARAR_PC.cmd`: «No se ha podido abrir la instalación como administrador» | Ha pulsado «No» en el aviso de Windows, o su usuario no puede instalar programas. Repita y pulse «Sí», o pida a informática que instale lo que indica. |
| `PREPARAR_PC.cmd`: «La firma digital del archivo descargado no es válida…» | El archivo descargado no es el original (descarga cortada o un proxy que lo modifica). No se ejecuta. Repita o descárguelo desde el enlace oficial que indica. |
| «Las pruebas automáticas han fallado» | Ha fallado una prueba; lea las líneas anteriores. Para generar igualmente: `crear_instalador.ps1 -SinPruebas`. |
| «La compilación para Revit <año> ha fallado» | Normalmente falta Internet para NuGet, o hay un error en el código. Compruebe la conexión y los mensajes del compilador. |
| «No se encuentran las familias de uniones» | `codigo\familias_uniones\` no tiene ningún `.rfa` ([4.2](#42-conseguir-las-12-familias-de-uniones)). |
| «… contiene '…' (datos de este equipo)» | Un archivo del paquete lleva su nombre de usuario o de equipo. Compile sin símbolos de depuración (el script lo hace por defecto). |
| SmartScreen bloquea el `.exe` | No está firmado: «Más información» → «Ejecutar de todas formas». |

## 4.4 Preparar un PC nuevo

Sí: puede descargar el proyecto completo desde GitHub y seguir trabajando. Todo está en el repositorio **salvo** lo indicado en [4.5](#45-archivos-que-no-van-en-el-repositorio).

**Sin consola:** si aún no tiene el repositorio ni Git, en la página de GitHub pulse **Code › Download ZIP**, descomprímalo y haga doble clic en `codigo\PREPARAR_PC.cmd`. Instala los SDK de .NET 10 y 8, Git y GitHub CLI, y activa las rutas largas de Git. Después clone el repositorio (paso 3) y borre la carpeta del ZIP.

**Con la consola:**

```powershell
# 1. Herramientas (vea 4.1). O, en la carpeta codigo, .\PREPARAR_PC.cmd
winget install --id Git.Git -e --source winget
winget install --id Microsoft.DotNet.SDK.10 -e --source winget   # Revit 2027, instalador y versiones 2023/2024
winget install --id Microsoft.DotNet.SDK.8 -e --source winget    # Revit 2025/2026 (versiones anteriores a 2027) y pruebas
winget install --id GitHub.cli -e --source winget      # opcional
git config --global core.longpaths true                # evita problemas con rutas largas de Windows

# 2. Identidad de sus commits. Use la dirección "noreply" de GitHub para no publicar su correo:
#    GitHub → Settings → Emails → "Keep my email addresses private" la muestra.
git config --global user.name  "Victor Hipp Murcia"
git config --global user.email "<id>+VictorHipp@users.noreply.github.com"

# 3. Descargar el repositorio
cd $HOME\Desktop
git clone https://github.com/VictorHipp/RevitAutomation.git
cd RevitAutomation\codigo

# 4. Vuelva a poner las 12 familias de uniones (vea 4.2)

# 5. Compruebe que todo funciona y cree el instalador (vea 4.3)
.\CREAR_INSTALADOR.cmd
```

Para subir cambios desde el PC nuevo, inicie sesión una vez con `gh auth login` (o Git abrirá el navegador la primera vez que haga `git push`).

Rutina diaria en cualquier PC:

```powershell
git pull                      # al empezar: trae los últimos cambios
# ... trabajar ...
git add -A
git commit -m "Describa el cambio"
git push                      # al terminar: sube sus cambios
```

> Sin Git: en la página de GitHub, **Code › Download ZIP** descarga los archivos actuales, pero sin historial y sin poder volver a subir los cambios.

`CLAUDE.md` (raíz) y `codigo/CLAUDE.md` están en el repositorio: Claude Code recibe las reglas y la arquitectura del proyecto en cualquier PC.

## 4.5 Archivos que no van en el repositorio

| Elemento | Por qué | Cómo recuperarlo |
|---|---|---|
| `codigo/familias_uniones/*.rfa` (12 familias de uniones) | Contenido de la biblioteca de Autodesk, no cubierto por la licencia de este proyecto | [4.2](#42-conseguir-las-12-familias-de-uniones) |
| `Instalar_RevitAutomation.exe` | Archivo generado | [4.3](#43-crear-el-instalador-paso-a-paso) |
| `bin/`, `obj/` | Resultado de la compilación | Se generan solos |
| `codigo/ejemplos/ejemplo_mep.dwg` | Lo generan las pruebas y sale distinto en cada ejecución; se conserva la versión DXF | No hace falta |
| Planos reales de clientes, `Pruebas/`, `FIRMAS_*.json` | Confidenciales | Nunca se guardan; vea [Pruebas](06-pruebas.md#64-planos-reales-confidenciales) |

## 4.6 Compilar a mano

Una versión de Revit:

```powershell
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2023
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2024
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2025
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2026
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net10.0-windows -p:RevitYear=2027
```

Si compila varios años seguidos, dé a cada uno su carpeta intermedia, p. ej. `-p:IntermediateOutputPath=obj\y2026\`.

La solución (2025 + 2027 si Revit 2027 está instalado o con `BuildForRevit2027=true`):

```powershell
dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true
```

### Cómo se elige la versión de Revit (`src/Revit.props`)

- `RevitYear` elige la plataforma: 2023/2024 → `net48`, 2025/2026 → `net8.0-windows`, 2027 → `net10.0-windows`. Sin `RevitYear`: `net48` → 2024, `net8.0-windows` → 2025, `net10.0-windows` → 2027.
- Usa `C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` / `RevitAPIUI.dll` si existen; si no, los paquetes NuGet `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` (`2023.4.0`, `2024.3.0`, `2025.4.60`, `2026.4.10`, `2027.4.0`), solo para compilar.
- El objetivo `ValidateRevitApiVersion` detiene la compilación si la versión principal de la API no corresponde al año.
- Define la constante `REVIT<año>` (p. ej. `REVIT2026`) para código condicional.

### Ajustes comunes (`Directory.Build.props`)

C# `latest`, tipos de referencia anulables, usings implícitos, versión y metadatos de copyright. SourceLink y las consultas de control de código fuente están desactivados (fallaban en una unidad de red). Para `net48` añade PolySharp, `System.Text.Json` 8.0.5 y un `using CadMepRevit.Compat` global.

## 4.7 Reglas de código

Vienen de [`codigo/CLAUDE.md`](../../codigo/CLAUDE.md) y se aplican a todo cambio:

- Las reglas de reconocimiento y geometría deben valer para planos de **cualquier oficina**: nunca adaptarlas a un nombre de archivo de prueba, una capa exacta, unas coordenadas o un solo plano.
- Usar solo tipos, métodos y parámetros de la API de Revit que **compilen en todas las versiones admitidas**. Si una llamada no compila, se considera que no existe. Revit 2023 no tiene `ElementId.Value` ni `new ElementId(long)`: use `RevitIds.FromValue(long)` (`src/CadMepRevit/Compat/RevitApiCompat.cs`).
- Escribir el código para .NET 8. Si la compilación `net48` falla por un miembro de la BCL que falta, añádalo a `src/CadMepRevit.Core/Compat/NetFrameworkCompat.cs` en lugar de cambiar el código común.
- Tomar las medidas del plano o de otra fuente identificable; marcar los valores por defecto como `SUPUESTO` en el informe.
- Todos los cambios del modelo de un clic en la cinta van en un solo `TransactionGroup` asimilado (un solo Ctrl+Z).
- Textos de la cinta, ventanas, informes, ayudas y **comentarios del código en español**. Los mensajes de error dicen qué falló **y cómo corregirlo**.
- Mantener **ACadSharp en 3.8.35** (otros complementos cargan la misma DLL dentro de Revit; otra versión puede chocar).
- WinForms y Revit comparten nombres de tipos (`Form`, `Control`, `TaskDialog`…): use alias como `using Form = System.Windows.Forms.Form;`.
- Informar por separado de la compilación, las pruebas fuera de Revit y la verificación dentro de Revit.
- Guardar las especificaciones en `codigo/docs/ESPEC_*.md`.
- Antes de aceptar un cambio en el motor, comparar los planes antes y después (vea [Pruebas](06-pruebas.md#63-comparar-planes-antes-de-aceptar-un-cambio)).
