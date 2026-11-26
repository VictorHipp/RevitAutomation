namespace RevitAutomation.Addin.Commands.SalasTecnicas;

internal static class SalaGeometria
{
    internal const double Epsilon = 1e-6;
    internal readonly record struct Rect(Pt Centro, double Menor, double Mayor);
    internal static double Distancia(Pt a, Pt b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static double Cruz(Pt a, Pt b, Pt c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    internal static double Area(IReadOnlyList<Pt> p)
    {
        if (p.Count < 3) return 0;
        // Origen local para conservar precisión en coordenadas de proyecto.
        double suma = 0;
        for (int i = 1; i+1 < p.Count; i++) suma += Cruz(p[0], p[i], p[i+1]);
        return suma / 2;
    }
    internal static Pt Centroide(IReadOnlyList<Pt> p)
    {
        var o = p[0]; double x = 0, y = 0, suma = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i+1)%p.Count];
            double c = (a.X-o.X)*(b.Y-o.Y)-(b.X-o.X)*(a.Y-o.Y);
            suma += c; x += (a.X+b.X-2*o.X)*c; y += (a.Y+b.Y-2*o.Y)*c;
        }
        return Math.Abs(suma) <= Epsilon ? o : new(o.X+x/(3*suma), o.Y+y/(3*suma));
    }
    internal static List<Pt> Limpiar(IReadOnlyList<Pt> puntos)
    {
        var p = new List<Pt>();
        foreach (var v in puntos) if (p.Count == 0 || Distancia(p[^1],v) > Epsilon) p.Add(v);
        if (p.Count > 1 && Distancia(p[0],p[^1]) <= Epsilon) p.RemoveAt(p.Count-1);
        bool cambio = true;
        while (cambio && p.Count >= 3)
        {
            cambio = false;
            for (int i=0; i<p.Count; i++)
            {
                var a = p[(i+p.Count-1)%p.Count]; var b = p[i]; var c = p[(i+1)%p.Count];
                if (DistanciaSegmento(b,a,c) <= Epsilon) { p.RemoveAt(i); cambio=true; break; }
            }
        }
        if (Area(p) < 0) p.Reverse();
        return p;
    }
    private static double DistanciaSegmento(Pt p, Pt a, Pt b)
    {
        double dx=b.X-a.X, dy=b.Y-a.Y, l=dx*dx+dy*dy;
        double t=l==0 ? 0 : Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/l,0,1);
        return Distancia(p,new(a.X+t*dx,a.Y+t*dy));
    }
    internal static double DistanciaBorde(Pt p, IReadOnlyList<Pt> pol)
    {
        double d=double.PositiveInfinity;
        for(int i=0;i<pol.Count;i++) d=Math.Min(d,DistanciaSegmento(p,pol[i],pol[(i+1)%pol.Count]));
        return d;
    }
    internal static bool Dentro(Pt p, IReadOnlyList<Pt> pol)
    {
        if (pol.Count < 3) return false;
        if (DistanciaBorde(p,pol) <= Epsilon) return true;
        bool dentro=false;
        for(int i=0,j=pol.Count-1;i<pol.Count;j=i++)
        {
            var a=pol[i]; var b=pol[j];
            if ((a.Y>p.Y)!=(b.Y>p.Y) && p.X < (b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X) dentro=!dentro;
        }
        return dentro;
    }
    internal static bool Dentro(Pt p, EquipoPlan e) => e.Forma == EquipoForma.Cilindro
        ? Distancia(p,e.Centro) <= e.RadioMm+Epsilon : Dentro(p,e.Contorno);

    internal static Rect Rectangulo(IReadOnlyList<Pt> puntos)
    {
        // Las orientaciones del casco convexo incluyen el rectángulo mínimo.
        var orden = puntos.Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToList();
        if (orden.Count < 2) return new(orden.Count==0 ? default : orden[0],0,0);
        var casco = new List<Pt>();
        foreach(var p in orden) { while(casco.Count>=2 && Cruz(casco[^2],casco[^1],p)<=0) casco.RemoveAt(casco.Count-1); casco.Add(p); }
        int inferior=casco.Count;
        for(int i=orden.Count-2;i>=0;i--) { var p=orden[i]; while(casco.Count>inferior && Cruz(casco[^2],casco[^1],p)<=0) casco.RemoveAt(casco.Count-1); casco.Add(p); }
        casco.RemoveAt(casco.Count-1);
        double mejor=double.PositiveInfinity; Rect resultado=default; var o=orden[0];
        for(int i=0;i<casco.Count;i++)
        {
            var a=casco[i]; var b=casco[(i+1)%casco.Count]; double largo=Distancia(a,b);
            if(largo<=Epsilon) continue;
            double ux=(b.X-a.X)/largo, uy=(b.Y-a.Y)/largo;
            double minU=double.PositiveInfinity,maxU=double.NegativeInfinity,minV=minU,maxV=maxU;
            foreach(var p in casco)
            {
                double u=(p.X-o.X)*ux+(p.Y-o.Y)*uy, v=-(p.X-o.X)*uy+(p.Y-o.Y)*ux;
                minU=Math.Min(minU,u);maxU=Math.Max(maxU,u);minV=Math.Min(minV,v);maxV=Math.Max(maxV,v);
            }
            double ancho=maxU-minU,fondo=maxV-minV,area=ancho*fondo;
            if(area < mejor-Epsilon)
            {
                mejor=area; double u=(minU+maxU)/2,v=(minV+maxV)/2;
                resultado=new(new(o.X+u*ux-v*uy,o.Y+u*uy+v*ux),Math.Min(ancho,fondo),Math.Max(ancho,fondo));
            }
        }
        return resultado;
    }
    internal static bool Circulo(IReadOnlyList<Pt> p, Pt centro, out double radio)
    {
        double r=p.Count==0?0:p.Average(v=>Distancia(v,centro));
        radio=r;
        return p.Count>=8 && r>Epsilon && p.All(v=>Math.Abs(Distancia(v,centro)-r)<=r*0.04);
    }
    internal static List<Pt> Huella(SalaBloque b)
    {
        double c=Math.Cos(b.AnguloRad),s=Math.Sin(b.AnguloRad);
        return new[] {new Pt(-b.AnchoMm/2,-b.FondoMm/2),new Pt(b.AnchoMm/2,-b.FondoMm/2),new Pt(b.AnchoMm/2,b.FondoMm/2),new Pt(-b.AnchoMm/2,b.FondoMm/2)}
            .Select(p=>new Pt(b.Centro.X+p.X*c-p.Y*s,b.Centro.Y+p.X*s+p.Y*c)).ToList();
    }
}
