using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace InstaladorCadMep
{
    internal static class Program
    {
        /// <summary>
        /// Sin argumentos abre la ventana del instalador.
        /// /silencioso [/versiones:2023,2024,2025,2026,2027] [/familias:"carpeta"]  instala sin ventana (código de salida 0 = correcto).
        /// /desinstalar [/silencioso]                               quita el complemento.
        /// /destino:"carpeta"                                       solo para pruebas: instala ahí en lugar de en Revit.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            var op = Opciones.Leer(args);
            if (op.Silencioso) return SinVentana(op);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VentanaInstalador(op));
            return 0;
        }

        private static int SinVentana(Opciones op)
        {
            var log = new StringBuilder();
            void Log(string s) => log.AppendLine(s);
            var inst = new Instalador(op, Log);
            string error;
            if (op.Desinstalar) error = inst.Desinstalar();
            else
            {
                var versiones = op.Versiones.Count > 0
                    ? VersionRevit.Todas.Where(v => op.Versiones.Contains(v.Anio)).ToList()
                    : VersionRevit.Todas.Where(v => v.Detectada).ToList();
                if (versiones.Count == 0)
                    error = "No se encuentra Revit 2023, 2024, 2025, 2026 ni 2027 en este equipo.\n" +
                            "Cómo corregirlo: instale una de esas versiones o indique /versiones:2023,2024,2025,2026,2027.";
                else error = inst.Instalar(versiones);
            }
            if (error != null) Log("ERROR: " + error);
            else Log(op.Desinstalar ? "DESINSTALADO correctamente." : "INSTALADO correctamente.");

            // Informe para quien lo ejecute sin ventana (despliegues, pruebas).
            try
            {
                string carpeta = !string.IsNullOrWhiteSpace(op.Destino)
                    ? op.Destino
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAutomation");
                Directory.CreateDirectory(carpeta);
                File.WriteAllText(Path.Combine(carpeta, "instalacion.txt"), log.ToString(), new UTF8Encoding(true));
            }
            catch { }
            return error == null ? 0 : 1;
        }
    }

    /// <summary>Ventana del instalador.</summary>
    internal sealed class VentanaInstalador : Form
    {
        private readonly Opciones _op;
        private readonly List<(VersionRevit Version, CheckBox Casilla)> _casillas = new List<(VersionRevit, CheckBox)>();
        private readonly TextBox _familias = new TextBox();
        private readonly TextBox _registro = new TextBox();
        private readonly Button _instalar = new Button();
        private readonly Button _desinstalar = new Button();

        public VentanaInstalador(Opciones op)
        {
            _op = op;
            Text = "Instalar " + Instalador.NombreProducto + " " + Instalador.Version;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 542);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var titulo = new Label
            {
                Text = Instalador.NombreProducto,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14),
            };
            var explicacion = new Label
            {
                Text = "Añade a Revit la pestaña «RevitAutomation»: CAD-REV (del plano DWG/DXF a Revit), instalaciones, etiquetado, " +
                       "planos y exportaciones, y región. Quita el RevitAutomation y el CAD MEP antiguos.\n" +
                       "Se instala solo para su usuario: no hace falta ser administrador.",
                Location = new Point(18, 50),
                Size = new Size(566, 48),
            };

            var grupo = new GroupBox { Text = "Versiones de Revit", Location = new Point(16, 104), Size = new Size(568, 150) };
            int y = 22;
            foreach (var v in VersionRevit.Todas)
            {
                bool detectada = v.Detectada;
                var c = new CheckBox
                {
                    Text = "Revit " + v.Anio + (detectada ? "  (encontrado en este equipo)" : "  (no encontrado en este equipo)"),
                    Checked = detectada,
                    AutoSize = true,
                    Location = new Point(14, y),
                };
                grupo.Controls.Add(c);
                _casillas.Add((v, c));
                y += 24;
            }

            var etiquetaFamilias = new Label
            {
                Text = "Carpeta de familias (.rfa) de su oficina (opcional; se puede añadir más tarde en bibliotecas.txt):",
                Location = new Point(16, 264),
                Size = new Size(568, 18),
            };
            _familias.Location = new Point(16, 284);
            _familias.Size = new Size(470, 23);
            _familias.Text = op.Familias ?? "";
            var examinar = new Button { Text = "Examinar…", Location = new Point(494, 283), Size = new Size(90, 25) };
            examinar.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Carpeta con las familias de Revit de su oficina" })
                    if (d.ShowDialog(this) == DialogResult.OK) _familias.Text = d.SelectedPath;
            };

            _registro.Location = new Point(16, 318);
            _registro.Size = new Size(568, 170);
            _registro.Multiline = true;
            _registro.ReadOnly = true;
            _registro.ScrollBars = ScrollBars.Vertical;
            _registro.BackColor = SystemColors.Window;
            _registro.Text = "Pulse «Instalar». Si Revit está abierto, ciérrelo antes." + Environment.NewLine +
                             "Las reglas de reconocimiento que haya enseñado al complemento se conservan.";

            _instalar.Text = "Instalar";
            _instalar.Location = new Point(300, 500);
            _instalar.Size = new Size(90, 28);
            _instalar.Click += (s, e) => Instalar();
            _desinstalar.Text = "Desinstalar";
            _desinstalar.Location = new Point(397, 500);
            _desinstalar.Size = new Size(90, 28);
            _desinstalar.Click += (s, e) => Desinstalar();
            var cerrar = new Button { Text = "Cerrar", Location = new Point(494, 500), Size = new Size(90, 28), DialogResult = DialogResult.Cancel };
            cerrar.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { titulo, explicacion, grupo, etiquetaFamilias, _familias, examinar, _registro, _instalar, _desinstalar, cerrar });
            AcceptButton = _instalar;
            CancelButton = cerrar;

            _desinstalar.Enabled = VersionRevit.Todas.Any(v => new Instalador(_op, null).EstaInstalado(v));
            if (op.Desinstalar) Shown += (s, e) => Desinstalar();
        }

        private void Escribir(string s)
        {
            _registro.AppendText(s.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }

        private void Instalar()
        {
            _registro.Clear();
            _op.Familias = _familias.Text;
            var elegidas = _casillas.Where(c => c.Casilla.Checked).Select(c => c.Version).ToList();
            var noDetectadas = elegidas.Where(v => !v.Detectada).Select(v => "Revit " + v.Anio).ToList();
            if (noDetectadas.Count > 0 &&
                MessageBox.Show(this, string.Join(" y ", noDetectadas) + " no se encuentra en C:\\Program Files\\Autodesk.\n" +
                                      "¿Instalar igualmente? (Solo funcionará si Revit está instalado en otra carpeta.)",
                                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            _instalar.Enabled = _desinstalar.Enabled = false;
            string error;
            try { error = new Instalador(_op, Escribir).Instalar(elegidas); }
            catch (Exception ex) { error = "Error inesperado: " + ex.Message + "\nCómo corregirlo: cierre Revit, vuelva a ejecutar el instalador y, si se repite, envíe este mensaje al responsable."; }
            finally { Cursor = Cursors.Default; _instalar.Enabled = true; }
            _desinstalar.Enabled = VersionRevit.Todas.Any(v => new Instalador(_op, null).EstaInstalado(v));

            if (error != null)
            {
                Escribir("ERROR: " + error);
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string anios = string.Join(" o ", elegidas.Select(v => v.Anio));
            Escribir("");
            Escribir("INSTALADO correctamente.");
            MessageBox.Show(this,
                "RevitAutomation se ha instalado.\n\nSiguiente paso: abra Revit " + anios + ". La primera vez Revit preguntará si quiere cargar " +
                "el complemento «RevitAutomation»: elija «Cargar siempre». Busque la pestaña «RevitAutomation» en la cinta.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Desinstalar()
        {
            if (MessageBox.Show(this, "¿Quitar RevitAutomation de Revit 2023, 2024, 2025, 2026 y 2027 para este usuario?\nSus proyectos de Revit no se modifican.",
                                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _registro.Clear();
            string error;
            try { error = new Instalador(_op, Escribir).Desinstalar(); }
            catch (Exception ex) { error = "Error inesperado: " + ex.Message + "\nCómo corregirlo: cierre Revit y vuelva a intentarlo."; }
            _desinstalar.Enabled = VersionRevit.Todas.Any(v => new Instalador(_op, null).EstaInstalado(v));
            if (error != null)
            {
                Escribir("ERROR: " + error);
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
