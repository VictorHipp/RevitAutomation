using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace InstaladorCadMep
{
    /// <summary>Una versión de Revit admitida por el complemento.</summary>
    internal sealed class VersionRevit
    {
        public string Anio;
        /// <summary>El complemento se carga en su propio contexto (Revit 2026 y posteriores): ACadSharp no choca con otros complementos.</summary>
        public bool Aislado;

        public static readonly VersionRevit[] Todas =
        {
            new VersionRevit { Anio = "2023", Aislado = false }, // .NET Framework 4.8
            new VersionRevit { Anio = "2024", Aislado = false }, // .NET Framework 4.8
            new VersionRevit { Anio = "2025", Aislado = false },
            new VersionRevit { Anio = "2026", Aislado = true },
            new VersionRevit { Anio = "2027", Aislado = true },
        };

        public string CarpetaRevit => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + Anio);

        public bool Detectada => File.Exists(Path.Combine(CarpetaRevit, "Revit.exe"));
    }

    /// <summary>Opciones de la línea de órdenes.</summary>
    internal sealed class Opciones
    {
        /// <summary>Instala o desinstala sin ventana y devuelve 0 si todo ha ido bien.</summary>
        public bool Silencioso;
        public bool Desinstalar;
        /// <summary>Solo para pruebas: instala en esta carpeta en lugar de %APPDATA% y no toca el registro.</summary>
        public string Destino;
        /// <summary>Versiones pedidas (2023, 2024, 2025, 2026, 2027). Vacío = las que estén instaladas en el equipo.</summary>
        public List<string> Versiones = new List<string>();
        /// <summary>Carpeta de familias de la oficina que se añade a bibliotecas.txt (opcional).</summary>
        public string Familias;

        public static Opciones Leer(string[] args)
        {
            var o = new Opciones();
            foreach (string a in args)
            {
                string arg = a.Trim();
                string lower = arg.ToLowerInvariant();
                if (lower == "/silencioso" || lower == "/s" || lower == "/quiet") o.Silencioso = true;
                else if (lower == "/desinstalar" || lower == "/uninstall") o.Desinstalar = true;
                else if (lower.StartsWith("/destino:")) o.Destino = arg.Substring(9).Trim('"');
                else if (lower.StartsWith("/familias:")) o.Familias = arg.Substring(10).Trim('"');
                else if (lower.StartsWith("/versiones:"))
                    o.Versiones.AddRange(arg.Substring(11).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return o;
        }
    }

    /// <summary>Instala y desinstala RevitAutomation para el usuario actual (sin permisos de administrador).</summary>
    internal sealed class Instalador
    {
        public const string NombreProducto = "RevitAutomation";
        public const string NombreExe = "Instalar_RevitAutomation.exe";
        private const string ClaveDesinstalar = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RevitAutomation";
        private const string AddInId = "3B8E1F52-6C1A-4E0B-9D27-5A4C2E7F81D6";
        private const string Dll = "RevitAutomation.dll";
        private const string Carpeta = "RevitAutomation";

        // Versiones anteriores que se quitan al instalar (solo si se reconocen como suyas).
        private const string ClaveCadMepAntigua = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CadMepRevit";
        private const string AddInIdCadMepAntiguo = "00972C4E-258C-4A47-81E5-5CD5B6A63DE0";
        private const string AddInIdRevitAutomationAntiguo = "66C6B84C-4C82-48C4-8AB9-26FA39C5F8DB";
        private static readonly string[] AniosAntiguos = { "2023", "2024", "2025", "2026", "2027" };

        private readonly Opciones _op;
        private readonly Action<string> _log;

        public Instalador(Opciones op, Action<string> log)
        {
            _op = op;
            _log = log ?? (s => { });
        }

        private bool ModoPrueba => !string.IsNullOrWhiteSpace(_op.Destino);

        /// <summary>Carpeta Addins de Revit (o la de prueba).</summary>
        private string BaseAddins => ModoPrueba
            ? Path.Combine(_op.Destino, "Addins")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins");

        /// <summary>Carpeta local del usuario (%LOCALAPPDATA%), o la de prueba.</summary>
        private string BaseLocal => ModoPrueba
            ? Path.Combine(_op.Destino, "Local")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>Configuración del motor CAD-REV: reglas aprendidas y lista de bibliotecas (no cambia de sitio).</summary>
        private string CarpetaConfig => ModoPrueba
            ? Path.Combine(_op.Destino, "CadMepRevit")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadMepRevit");

        private string CarpetaInstalador => Path.Combine(BaseLocal, Carpeta, "Instalador");

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static bool RevitAbierto(VersionRevit v)
        {
            foreach (var p in Process.GetProcessesByName("Revit"))
            {
                try
                {
                    string ruta = p.MainModule?.FileName ?? "";
                    if (ruta.IndexOf("Revit " + v.Anio, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                catch
                {
                    // No se puede leer la ruta del proceso: por prudencia se considera abierto.
                    return true;
                }
            }
            return false;
        }

        public bool EstaInstalado(VersionRevit v) =>
            File.Exists(Path.Combine(BaseAddins, v.Anio, Carpeta, Dll));

        /// <summary>Instala en las versiones indicadas. Devuelve null si todo ha ido bien o el mensaje de error.</summary>
        public string Instalar(IList<VersionRevit> versiones)
        {
            if (versiones.Count == 0)
                return "No se ha elegido ninguna versión de Revit.\nCómo corregirlo: marque Revit 2023, 2024, 2025, 2026 o 2027.";

            foreach (var v in VersionRevit.Todas)
                if (!ModoPrueba && RevitAbierto(v))
                    return "Revit " + v.Anio + " está abierto y bloquea los archivos del complemento.\n" +
                           "Cómo corregirlo: guarde su trabajo, cierre Revit y vuelva a pulsar Instalar.";

            using (var paquete = AbrirPaquete(out string error))
            {
                if (paquete == null) return error;
                var entradas = paquete.Entries.Select(e => new { Entrada = e, Ruta = e.FullName.Replace('\\', '/') }).ToList();
                foreach (var v in versiones)
                    if (!entradas.Any(e => e.Ruta.Equals(v.Anio + "/" + Dll, StringComparison.OrdinalIgnoreCase)))
                        return "El instalador no contiene la versión para Revit " + v.Anio + ".\n" +
                               "Cómo corregirlo: vuelva a generar el instalador con CREAR_INSTALADOR.cmd.";

                // Antes de instalar se quitan el RevitAutomation y el CAD MEP antiguos: si no, saldrían dos pestañas.
                string aviso = QuitarVersionesAntiguas();
                if (aviso != null) return aviso;

                foreach (var v in versiones)
                {
                    string prefijo = v.Anio + "/";
                    string addinsDir = Path.Combine(BaseAddins, v.Anio);
                    string installDir = Path.Combine(addinsDir, Carpeta);
                    string addinFile = Path.Combine(addinsDir, Carpeta + ".addin");
                    try
                    {
                        // Se parte de una carpeta limpia para que no queden archivos de una instalación anterior.
                        if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
                        Directory.CreateDirectory(installDir);

                        foreach (var e in entradas)
                        {
                            string relativa;
                            if (e.Ruta.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) relativa = e.Ruta.Substring(prefijo.Length);
                            else if (e.Ruta.StartsWith("comun/", StringComparison.OrdinalIgnoreCase)) relativa = e.Ruta.Substring(6);
                            else continue;
                            if (relativa.Length == 0 || relativa.EndsWith("/")) continue;

                            string destino = Path.GetFullPath(Path.Combine(installDir, relativa.Replace('/', Path.DirectorySeparatorChar)));
                            if (!destino.StartsWith(Path.GetFullPath(installDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                continue; // ruta no válida dentro del paquete
                            Directory.CreateDirectory(Path.GetDirectoryName(destino));
                            e.Entrada.ExtractToFile(destino, true);
                        }

                        File.WriteAllText(addinFile, Manifiesto(Path.Combine(installDir, Dll), v.Aislado), new UTF8Encoding(false));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        return "No se pudieron copiar los archivos para Revit " + v.Anio + ": " + ex.Message + "\n" +
                               "Cómo corregirlo: cierre Revit y cualquier ventana abierta en " + installDir + " y vuelva a pulsar Instalar.";
                    }
                    _log("Instalado para Revit " + v.Anio + ".");
                    _log("   Archivos:   " + installDir);
                    _log("   Manifiesto: " + addinFile);
                }
            }

            string avisoFamilias = AnadirCarpetaFamilias();
            if (avisoFamilias != null) _log(avisoFamilias);

            if (File.Exists(Path.Combine(CarpetaConfig, "reglas_reconocimiento.json")))
                _log("Se conservan las reglas de reconocimiento aprendidas en " + CarpetaConfig + ".");
            _log("Los ajustes de las herramientas (Documentos\\RevitAutomation) no se tocan.");

            RegistrarDesinstalador();
            return null;
        }

        /// <summary>Quita RevitAutomation de todas las versiones. Las reglas aprendidas y los ajustes se conservan.</summary>
        public string Desinstalar()
        {
            foreach (var v in VersionRevit.Todas)
                if (!ModoPrueba && EstaInstalado(v) && RevitAbierto(v))
                    return "Revit " + v.Anio + " está abierto.\nCómo corregirlo: cierre Revit " + v.Anio + " y vuelva a desinstalar.";

            bool quitado = false;
            foreach (var v in VersionRevit.Todas)
            {
                string addinsDir = Path.Combine(BaseAddins, v.Anio);
                string installDir = Path.Combine(addinsDir, Carpeta);
                string addinFile = Path.Combine(addinsDir, Carpeta + ".addin");
                try
                {
                    if (File.Exists(addinFile) && EsManifiestoPropio(addinFile)) { File.Delete(addinFile); _log("Eliminado: " + addinFile); quitado = true; }
                    if (Directory.Exists(installDir)) { Directory.Delete(installDir, true); _log("Eliminado: " + installDir); quitado = true; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return "No se pudo quitar el complemento de Revit " + v.Anio + ": " + ex.Message + "\n" +
                           "Cómo corregirlo: cierre Revit " + v.Anio + " y vuelva a desinstalar.";
                }
            }

            QuitarDesinstalador();
            _log(quitado
                ? "RevitAutomation se ha desinstalado. La pestaña desaparecerá la próxima vez que abra Revit."
                : "RevitAutomation no estaba instalado para este usuario. No se ha cambiado nada.");
            if (Directory.Exists(CarpetaConfig))
                _log("Se conservan sus reglas de reconocimiento y la lista de bibliotecas en " + CarpetaConfig + " (bórrela a mano si no las quiere).");
            return null;
        }

        /// <summary>
        /// Quita las instalaciones anteriores para este usuario: el antiguo RevitAutomation (y su módulo «CAD → Revit»)
        /// y el antiguo CAD MEP. Solo borra los manifiestos que reconoce por su identificador o su clase, y las carpetas de
        /// programa de esos productos. Los ajustes de las herramientas (Documentos) y del motor (%APPDATA%) se conservan.
        /// </summary>
        private string QuitarVersionesAntiguas()
        {
            try
            {
                foreach (string anio in AniosAntiguos)
                {
                    string dir = Path.Combine(BaseAddins, anio);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string manifiesto in Directory.GetFiles(dir, "*.addin"))
                    {
                        string que = ProductoAntiguo(manifiesto);
                        if (que == null) continue;
                        File.Delete(manifiesto);
                        _log("Quitado " + que + " (Revit " + anio + "): " + manifiesto);
                    }
                    string cadMep = Path.Combine(dir, "CadMepRevit");
                    if (Directory.Exists(cadMep)) { Directory.Delete(cadMep, true); _log("Quitado CAD MEP antiguo (Revit " + anio + "): " + cadMep); }
                }

                // Programa del antiguo RevitAutomation (%LOCALAPPDATA%\RevitAutomation), salvo la copia de este instalador.
                string local = Path.Combine(BaseLocal, Carpeta);
                if (Directory.Exists(local))
                {
                    foreach (string sub in Directory.GetDirectories(local))
                        if (!string.Equals(Path.GetFileName(sub), "Instalador", StringComparison.OrdinalIgnoreCase))
                        { Directory.Delete(sub, true); _log("Quitado RevitAutomation antiguo: " + sub); }
                    foreach (string file in Directory.GetFiles(local))
                    { File.Delete(file); }
                }
                // Copia del instalador del CAD MEP antiguo.
                string viejo = Path.Combine(BaseLocal, "CadMepRevit", "Instalador");
                if (Directory.Exists(viejo)) { Directory.Delete(viejo, true); _log("Quitado el instalador del CAD MEP antiguo: " + viejo); }
                if (!ModoPrueba)
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(ClaveCadMepAntigua, false); } catch { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "No se pudo quitar una versión anterior: " + ex.Message + "\n" +
                       "Cómo corregirlo: cierre Revit (todas las versiones) y vuelva a pulsar Instalar.";
            }
            // Instalaciones para todos los usuarios (necesitan administrador): solo se avisa.
            string comun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins");
            if (!ModoPrueba && Directory.Exists(comun))
                foreach (string anio in AniosAntiguos)
                {
                    string dir = Path.Combine(comun, anio);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string manifiesto in Directory.GetFiles(dir, "*.addin"))
                        if (ProductoAntiguo(manifiesto) is string que)
                            _log("AVISO: hay un " + que + " instalado para todos los usuarios en " + manifiesto +
                                 ". Para quitarlo hace falta un administrador (borre ese archivo).");
                }
            return null;
        }

        /// <summary>Qué producto antiguo registra este manifiesto, o null si no es de uno conocido (no se toca).</summary>
        private static string ProductoAntiguo(string manifiesto)
        {
            string texto;
            try { texto = File.ReadAllText(manifiesto); } catch { return null; }
            if (texto.IndexOf(AddInId, StringComparison.OrdinalIgnoreCase) >= 0) return null; // el nuevo
            if (texto.IndexOf(AddInIdCadMepAntiguo, StringComparison.OrdinalIgnoreCase) >= 0 ||
                texto.IndexOf("CadMepRevit.App", StringComparison.Ordinal) >= 0) return "CAD MEP antiguo";
            if (texto.IndexOf(AddInIdRevitAutomationAntiguo, StringComparison.OrdinalIgnoreCase) >= 0 ||
                texto.IndexOf("RevitAutomation.Addin.RevitAutomationApplication", StringComparison.Ordinal) >= 0 &&
                texto.IndexOf("RevitAutomation.Addin.dll", StringComparison.OrdinalIgnoreCase) >= 0) return "RevitAutomation antiguo";
            if (texto.IndexOf("RevitAutomation.CadStandaloneApplication", StringComparison.Ordinal) >= 0) return "módulo «CAD → Revit» del RevitAutomation antiguo";
            return null;
        }

        private static bool EsManifiestoPropio(string manifiesto)
        {
            try { return File.ReadAllText(manifiesto).IndexOf(AddInId, StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private static ZipArchive AbrirPaquete(out string error)
        {
            error = null;
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("paquete.zip");
            if (s == null)
            {
                error = "El instalador está incompleto: no lleva dentro el complemento.\n" +
                        "Cómo corregirlo: vuelva a generarlo con CREAR_INSTALADOR.cmd.";
                return null;
            }
            try { return new ZipArchive(s, ZipArchiveMode.Read); }
            catch (InvalidDataException ex)
            {
                error = "El paquete del instalador está dañado (" + ex.Message + ").\nCómo corregirlo: vuelva a descargar o generar el instalador.";
                return null;
            }
        }

        private static string Manifiesto(string dll, bool aislado)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<RevitAddIns>\n");
            sb.Append("  <AddIn Type=\"Application\">\n");
            sb.Append("    <Name>RevitAutomation</Name>\n");
            sb.Append("    <Assembly>").Append(SecurityElement.Escape(dll)).Append("</Assembly>\n");
            sb.Append("    <AddInId>").Append(AddInId).Append("</AddInId>\n");
            sb.Append("    <FullClassName>RevitAutomation.Addin.RevitAutomationApplication</FullClassName>\n");
            sb.Append("    <VendorId>RVTA</VendorId>\n");
            // Sin tildes: algunos lectores de manifiestos de Revit no respetan la codificación declarada.
            sb.Append("    <VendorDescription>RevitAutomation - CAD-REV, instalaciones, etiquetado, documentacion y region</VendorDescription>\n");
            sb.Append("  </AddIn>\n");
            if (aislado)
            {
                sb.Append("  <ManifestSettings>\n");
                sb.Append("    <UseRevitContext>False</UseRevitContext>\n");
                sb.Append("    <ContextName>RevitAutomation</ContextName>\n");
                sb.Append("  </ManifestSettings>\n");
            }
            sb.Append("</RevitAddIns>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Añade la carpeta de familias de la oficina a bibliotecas.txt (el mismo archivo que crea el motor CAD-REV).
        /// Si el archivo no existe se crea con la misma cabecera y la biblioteca de Revit del equipo.
        /// </summary>
        private string AnadirCarpetaFamilias()
        {
            string carpeta = (_op.Familias ?? "").Trim().Trim('"');
            if (carpeta.Length == 0) return null;
            try
            {
                Directory.CreateDirectory(CarpetaConfig);
                string cfg = Path.Combine(CarpetaConfig, "bibliotecas.txt");
                var lineas = File.Exists(cfg)
                    ? File.ReadAllLines(cfg).ToList()
                    : new List<string>
                    {
                        "# Carpetas con familias de Revit (.rfa) que «Modelar desde CAD» puede cargar (una por línea).",
                        "# Se buscan por el nombre de la familia. Las líneas que empiezan por # no se leen.",
                        @"# Añada aquí las carpetas de familias de su oficina, por ejemplo: X:\Biblioteca Revit\Familias",
                        @"C:\ProgramData\Autodesk\RVT {AÑO}\Libraries\Spanish",
                    };
                if (lineas.Any(l => string.Equals(l.Trim().TrimEnd('\\'), carpeta.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    return "La carpeta de familias ya estaba en la lista: " + carpeta;
                lineas.Add(carpeta);
                File.WriteAllLines(cfg, lineas, new UTF8Encoding(true));
                string aviso = Directory.Exists(carpeta) ? "" : " (AVISO: ahora no se encuentra; compruebe que la unidad de red está conectada)";
                return "Carpeta de familias añadida a " + cfg + ": " + carpeta + aviso;
            }
            catch (Exception ex)
            {
                return "AVISO: no se pudo añadir la carpeta de familias (" + ex.Message + "). Puede añadirla a mano en " +
                       Path.Combine(CarpetaConfig, "bibliotecas.txt") + ".";
            }
        }

        /// <summary>Copia el instalador a la carpeta local del usuario y lo registra en «Aplicaciones instaladas».</summary>
        private void RegistrarDesinstalador()
        {
            try
            {
                Directory.CreateDirectory(CarpetaInstalador);
                string propio = Assembly.GetExecutingAssembly().Location;
                string copia = Path.Combine(CarpetaInstalador, NombreExe);
                if (!string.Equals(Path.GetFullPath(propio), Path.GetFullPath(copia), StringComparison.OrdinalIgnoreCase))
                    File.Copy(propio, copia, true);
                if (ModoPrueba) return;

                using (var k = Registry.CurrentUser.CreateSubKey(ClaveDesinstalar))
                {
                    k.SetValue("DisplayName", NombreProducto);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", NombreProducto);
                    k.SetValue("DisplayIcon", copia);
                    k.SetValue("InstallLocation", Path.GetDirectoryName(copia));
                    k.SetValue("UninstallString", "\"" + copia + "\" /desinstalar");
                    k.SetValue("QuietUninstallString", "\"" + copia + "\" /desinstalar /silencioso");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                _log("AVISO: no se pudo registrar en «Aplicaciones instaladas» (" + ex.Message + "). El complemento sí está instalado.");
            }
        }

        private void QuitarDesinstalador()
        {
            if (ModoPrueba)
            {
                try { if (Directory.Exists(CarpetaInstalador)) Directory.Delete(CarpetaInstalador, true); } catch { }
                return;
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(ClaveDesinstalar, false); } catch { }

            // Este programa puede ser la propia copia guardada: se borra cuando termine.
            string copia = Path.Combine(CarpetaInstalador, NombreExe);
            if (!File.Exists(copia)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + copia + "\" & rmdir \"" + CarpetaInstalador + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch { }
        }
    }
}
