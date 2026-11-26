using System.Globalization;
using System.Text;

namespace RevitAutomation.Addin.Commands.SalasTecnicas;

public static class SalaReport
{
    private static string N(double n) => n.ToString("0",CultureInfo.InvariantCulture);
    public static string Texto(SalaPlan plan,string cad,string nivel,IReadOnlyList<string> notas)
    {
        var b=new StringBuilder();
        b.AppendLine($"Sala técnica · {plan.Perfil.Nombre}"); b.AppendLine($"CAD: {cad}"); b.AppendLine($"Nivel: {nivel}");
        b.AppendLine(); b.AppendLine($"Equipos modelados ({plan.Equipos.Count})");
        foreach(var grupo in plan.Equipos.GroupBy(e=>e.Tipo.Id))
        {
            b.AppendLine($"{grupo.First().Tipo.Nombre} ({grupo.Count()})");
            foreach(var e in grupo)
            {
                var r=SalaGeometria.Rectangulo(e.Contorno);
                string forma=e.Forma==EquipoForma.Cilindro ? $"Cilindro Ø {N(e.RadioMm*2)}" : $"Caja {N(r.Mayor)}×{N(r.Menor)}";
                b.AppendLine($"- {e.Etiqueta} · {forma} · alto {N(e.AltoMm)} mm{(e.AltoSupuesto?" (supuesto)":"")} · base {N(e.ElevacionMm)} mm · {e.Origen}");
            }
        }
        b.AppendLine(); b.AppendLine("No modelado");
        foreach(var d in plan.Descartes.Take(60)) b.AppendLine($"- {d.Que}: {d.Motivo}");
        if(plan.Descartes.Count>60) b.AppendLine($"… y {plan.Descartes.Count-60} más");
        b.AppendLine(); b.AppendLine("Avisos"); foreach(var a in plan.Avisos) b.AppendLine("- "+a);
        b.AppendLine(); b.AppendLine("Detalles"); foreach(var n in notas) b.AppendLine(n);
        return b.ToString();
    }
    private static string Campo(string s) => s.IndexOfAny(new[]{';','"','\r','\n'})>=0 ? "\""+s.Replace("\"","\"\"")+"\"" : s;
    public static string Csv(SalaPlan plan)
    {
        var b=new StringBuilder();
        b.AppendLine("Tipo;Etiqueta;Forma;Ancho_mm;Fondo_mm;Diametro_mm;Alto_mm;Alto_supuesto;Base_mm;Centro_X_mm;Centro_Y_mm;Origen");
        foreach(var e in plan.Equipos)
        {
            bool cilindro=e.Forma==EquipoForma.Cilindro; var r=SalaGeometria.Rectangulo(e.Contorno);
            string[] campos={e.Tipo.Nombre,e.Etiqueta,cilindro?"Cilindro":"Caja",cilindro?"":N(r.Mayor),cilindro?"":N(r.Menor),cilindro?N(e.RadioMm*2):"",
                N(e.AltoMm),e.AltoSupuesto?"Sí":"No",N(e.ElevacionMm),N(e.Centro.X),N(e.Centro.Y),e.Origen};
            b.AppendLine(string.Join(";",campos.Select(Campo)));
        }
        return b.ToString();
    }
}
