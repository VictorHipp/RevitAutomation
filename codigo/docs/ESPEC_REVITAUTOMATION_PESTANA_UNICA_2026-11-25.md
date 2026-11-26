# RevitAutomation: pestaña única y herramientas integradas (25-11-2026)

## 1. Decisiones del usuario

| Tema | Decisión |
|---|---|
| Nombre | La herramienta se llama **RevitAutomation**: es el nombre de la pestaña y de la carpeta de instalación. |
| Motor CAD → Revit | Pasa a llamarse **CAD-REV** (antes «CAD MEP»). |
| Herramientas que se copian del proyecto RevitAutomation anterior | Exportar PDF, Exportar CAD, Etiquetado (tuberías, conductos, bandejas, familias, luminarias), Salas técnicas, Patinillo, Región de sectorización, Planos, Aislamiento RITE y Llaves de local húmedo. El resto del proyecto anterior no se copia. El origen no se modifica. |
| Organización | Una sola pestaña, por secciones, con los botones en desplegables. |
| Versiones de Revit | 2025, 2026 y 2027. |
| Versiones antiguas | El instalador desinstala el RevitAutomation y el CAD MEP antiguos. |
| Confidencialidad | Sin nombres de proyectos, clientes, personas ni rutas. Los ejemplos usan nombres genéricos. |

## 2. Pestaña «RevitAutomation»

| Panel | Desplegable | Botones | Comandos |
|---|---|---|---|
| CAD-REV | CAD-REV | Tuberías, Conductos, Bandejas, Luminarias, Equipos y PCI | `Commands/CadRevCommands.cs` (heredan de `CadMepRevit.Commands.ModelFromCadCommand`) |
| Instalaciones | Salas técnicas | Contadores de agua, Electricidad, PCI, Fontanería | `Commands/SalasTecnicas` |
| Instalaciones | Fontanería | Patinillo de fontanería, Aislamiento RITE, Llaves de local húmedo | `Commands/Patinillo`, `Commands/Aislamiento`, `Commands/LlavesLocalHumedo` |
| Etiquetado | Etiquetado | Tuberías, Conductos, Bandejas, Familias, Luminarias (seleccionadas) | `Commands/PipeTagging` |
| Documentación | Planos | Fontanería, Climatización, Electricidad, Iluminación, Genéricas y PCI, Conjunto completo | `Commands/Planos` |
| Documentación | Exportar | Exportar PDF, Exportar CAD | `Commands/PdfExportCommand.cs`, `Commands/CadExportCommand.cs` |
| Región | Región | Región de sectorización | `Commands/Regiones` |

Todos los botones apuntan a `RevitAutomation.dll`. En Revit 2026 y 2027 el complemento se carga aislado
(`UseRevitContext=False`), y así todo queda en el mismo contexto. El motor CAD-REV está en `CadMepRevit.dll`, que
`RevitAutomation.dll` referencia.

## 3. Cambios al copiar las herramientas

- **Sin cambios de funcionamiento.** Se conservan sus espacios de nombres (`RevitAutomation.Addin.Commands.*`) y sus
  ajustes e informes en `Documentos\RevitAutomation\<herramienta>`.
- **Dependencias del proyecto anterior:**
  - Se quita la ventanita «¿Esperar?» (`WaitBubble`), que abría otras herramientas suyas.
  - `CadWarningSwallower` pasa a `Commands/CadWarningSwallower.cs`.
  - De los iconos (`RibbonIcons.cs`) solo quedan los que se usan.
- **Salas técnicas:** modelan su red con `CadMepRevit.Commands.ModelFromCadCommand.BuildWithoutUi`, portado al motor
  actual. La sala usa su altura propia (1,00 m en contadores; 2,50–2,70 m en el resto), así que ahí **no** se aplica la
  regla «sigue el techo» (`BuildPlan.FollowCeiling = false`). Las redes de tubería con altura supuesta se llevan enteras
  a esa altura; las que tienen cota en el plano la conservan.
- **Patinillo:** usa el `FittingSetup` del motor actual. **Llaves:** usan el lector CAD del motor actual.

## 4. Versiones de Revit (`src/Revit.props`)

- 2025 y 2026 se compilan con .NET 8 y 2027 con .NET 10, siempre contra la API de esa versión: la del Revit instalado
  o, si no está, el paquete NuGet `Nice3point.Revit.Api` (2025.4.60, 2026.4.10 y 2027.4.0).
- Un objetivo de MSBuild comprueba que la API usada es la de esa versión.
- Para una sola versión: `dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows -p:RevitYear=2026`.

## 5. Instalador (`Instalar_RevitAutomation.exe`)

- Instala, para el usuario actual, en `%APPDATA%\Autodesk\Revit\Addins\<año>\RevitAutomation`, con el manifiesto
  `RevitAutomation.addin` (identificador nuevo; contexto aislado en 2026 y 2027).
- **Antes de instalar quita:**
  - los manifiestos del RevitAutomation antiguo y de su módulo «CAD → Revit», reconocidos por su identificador o su
    clase;
  - el CAD MEP antiguo (`CadMepRevit.addin` y su carpeta);
  - el programa antiguo en `%LOCALAPPDATA%\RevitAutomation`;
  - la entrada antigua de «Aplicaciones instaladas».
- **No toca:** otros complementos, `Documentos\RevitAutomation` ni las reglas de CAD-REV en `%APPDATA%\CadMepRevit`.
  Las instalaciones para todos los usuarios solo se avisan.
- Se probó en una carpeta de prueba (`/destino:`) con instalaciones antiguas simuladas y un complemento ajeno:
  - quita las antiguas, instala las tres versiones y conserva el ajeno;
  - la desinstalación deja solo el ajeno.

## 6. Verificación

| Comprobación | Resultado | Dónde |
|---|---|---|
| Compilación de RevitAutomation y CAD-REV para Revit 2025, 2026 y 2027 | 0 avisos, 0 errores | Fuera de Revit |
| Pruebas del motor (`tests/CadMepRevit.Tests`) | Todas superadas | Fuera de Revit |
| Pruebas de las herramientas (`tests/RevitAutomation.Pruebas`: etiquetado 128, aislamiento 501, planos 123, región 25, PDF 20, salas, patinillo, llaves) | Todas superadas | Fuera de Revit |
| Instalador en carpeta de prueba | Correcto | Fuera de Revit |
| Pestaña, desplegables y herramientas dentro de Revit 2025, 2026 y 2027 | **NO verificado** | Pendiente dentro de Revit |
