using System.Windows.Forms;

namespace CadMepRevit.Ui;

/// <summary>Ayuda breve para principiantes (también se instala como Guia_CadMepRevit.txt).</summary>
public static class HelpText
{
    public const string Short =
        "IMPORTAR CAD Y CONSTRUIR MEP\n" +
        "1. Abra un PROYECTO de Revit (no una familia) que tenga tipos de tubería y sistemas\n" +
        "   (por ejemplo, creado con la plantilla 'Sistemas', 'Mecánica' o 'Fontanería').\n" +
        "2. Pulse 'Importar CAD y construir MEP' y elija el plano DWG o DXF.\n" +
        "3. Revise las unidades del dibujo y elija el nivel y la altura.\n" +
        "4. En 'Este plano es de' elija TUBERÍAS (diámetro) o CONDUCTOS (ancho × alto).\n" +
        "   En la tabla puede ajustar cada capa: qué es (tubería, conducto, bandeja, tubo\n" +
        "   eléctrico, muro o 'No importar'), su sistema, su SECCIÓN (redonda = diámetro;\n" +
        "   rectangular u oval = ancho × alto), su tipo y sus medidas.\n" +
        "5. Opcional: asigne familias de Revit a los bloques (válvulas, aparatos…).\n" +
        "6. Pulse 'Previsualizar…', compruebe el dibujo y pulse 'Construir en Revit…'.\n" +
        "7. Confirme. Todo se crea en una sola operación: Ctrl+Z la deshace por completo.\n\n" +
        "CONSEJOS\n" +
        "• Dibuje cada red en su propia capa (p. ej. AF, ACS, SANEAMIENTO).\n" +
        "• Conductos dibujados con su CONTORNO (dos líneas, como exporta Revit): marque\n" +
        "  'Doble línea' en la capa. Se crea el eje con el ancho medido en el CAD y se\n" +
        "  cierran los huecos de codos, tes y transiciones dibujados como bloque.\n" +
        "• Las uniones se crean donde los extremos se tocan (según la tolerancia) y donde\n" +
        "  un tramo termina sobre otro (te). Un codo dibujado como arco entre dos tramos\n" +
        "  rectos se convierte en una esquina. Los círculos no se convierten.\n" +
        "• Si una unión falla, revise las 'Preferencias de enrutamiento' del tipo de tubería.\n" +
        "• Si el dibujo está muy lejos del origen, marque 'Acercar el dibujo al origen'.";

    public static void ShowDialog(IWin32Window? owner)
    {
        MessageBox.Show(owner, Short, "CAD-REV · Ayuda", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
