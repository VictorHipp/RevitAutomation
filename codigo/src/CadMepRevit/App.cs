using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using CadMepRevit.Commands;

namespace CadMepRevit;

/// <summary>
/// Botones de CAD-REV (del plano CAD a Revit). La pestaña única «RevitAutomation» (src\RevitAutomation) los pone en su
/// desplegable «CAD-REV». Este DLL ya no crea pestaña propia.
/// </summary>
public static class CadRevRibbon
{
    public const string Name = "CAD-REV";

    /// <summary>
    /// Los cinco botones, uno por instalación. <paramref name="assemblyPath"/> es el DLL que contiene los comandos
    /// (<paramref name="commands"/>: tuberías, conductos, bandejas, luminarias y equipos) y la clase de disponibilidad.
    /// </summary>
    public static IReadOnlyList<PushButtonData> Buttons(string assemblyPath, IReadOnlyList<Type> commands, string availabilityClass)
    {
        if (commands.Count != 5) throw new ArgumentException("Hacen falta los cinco comandos de CAD-REV.", nameof(commands));
        // Un botón por instalación, sin campos ni medidas: cada uno lee el plano y modela solo lo suyo. El antiguo
        // «Modelar desde CAD» (ModelFromCadCommand, todo a la vez) y los comandos anteriores (ImportCadCommand,
        // HelpCommand) siguen en el código pero no se muestran en la cinta.
        const string steps = "1) Elija el plano. 2) Si el proyecto tiene varios niveles, diga en qué planta se modela (es la única pregunta). " +
                             "3) Al final verá un informe: qué se ha modelado, qué no y por qué (y qué botón lo modela), avisos y valores supuestos. " +
                             "Lo que otro botón ya colocó en el mismo sitio no se repite. Ctrl+Z lo deshace todo.";
        PushButtonData Button(string id, string text, Type command, string icon, Color color, string tip, string what) =>
            new(id, text, assemblyPath, command.FullName)
            {
                ToolTip = tip,
                LongDescription = what + " " + steps,
                AvailabilityClassName = availabilityClass,
                LargeImage = Icon(icon, color, 32),
                Image = Icon(icon, color, 16)
            };
        return new[]
        {
            Button("CadMep_ModelPipes", "Tuberías", commands[0], "TUB", Color.FromRgb(0x1F, 0x6F, 0xC5),
                "Modela tuberías y bajantes con codos, tes y reducciones. Coloca aparatos, válvulas y equipos de agua.",
                "Aplica diámetros, pendientes y cotas del plano; las tuberías colgadas sin cota siguen el techo (100 mm bajo el forjado). " +
                "Los encuentros de cuatro tubos quedan para el técnico."),
            Button("CadMep_ModelDucts", "Conductos", commands[1], "CON", Color.FromRgb(0x6A, 0x3D, 0xB8),
                "Modela conductos con codos, tes, cruces y reducciones, y coloca rejillas, difusores, compuertas y ventiladores.",
                "Siguen el techo (100 mm bajo el forjado, subiendo o bajando con codos). Sitúa los accesorios a la altura de su red e informa de cada unión."),
            Button("CadMep_ModelTrays", "Bandejas", commands[2], "BAN", Color.FromRgb(0xD3, 0x6B, 0x00),
                "Modela bandejas de cables y tubos eléctricos con sus codos, tes, cruces y reducciones.",
                "Siguen el techo (100 mm bajo el forjado). Para las bandejas y tubos a doble línea se mide el ancho del dibujo. Las uniones salen del tipo del proyecto o de la biblioteca de Revit; el informe dice las que falten."),
            Button("CadMep_ModelLighting", "Luminarias", commands[3], "LUM", Color.FromRgb(0xC9, 0xA2, 0x00),
                "Coloca las luminarias (downlights, puntos de luz, tiras LED, emergencias…) y los interruptores y detectores de presencia del plano.",
                "Usa la familia del bloque si está en el proyecto o en la biblioteca; si no, una luminaria genérica de Revit. Las de techo, a la cota del falso techo o del forjado."),
            Button("CadMep_ModelEquipment", "Equipos y PCI", commands[4], "PCI", Color.FromRgb(0xC0, 0x39, 0x2B),
                "Coloca extintores, BIE, detectores, pulsadores, sirenas, señales, cuadros eléctricos, tomas y otros dispositivos del plano.",
                "Usa la familia del bloque si está en el proyecto o en la biblioteca; si no, una genérica de Revit de su clase. Alturas supuestas habituales."),
        };
    }

    /// <summary>Icono sencillo generado en memoria (sin archivos de imagen externos).</summary>
    public static BitmapSource Icon(string text, Color color, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size), size / 6.0, size / 6.0);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                text.Length > 2 ? size * 0.36 : size * 0.6, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
