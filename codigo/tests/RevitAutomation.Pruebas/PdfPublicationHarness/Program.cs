using RevitAutomation.Addin.Commands;

int checks=0;
void Check(bool value,string label){if(!value)throw new Exception("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);}
IOException Fail(Action action)
{
    try{action();}catch(IOException ex){return ex;}
    throw new Exception("Se esperaba un error de publicación.");
}
void Scenario(string name,Action<string,string,string> test)
{
    string root=Path.Combine(Path.GetTempPath(),"RevitAutomation-PdfTest-"+Guid.NewGuid().ToString("N"));
    string stage=Path.Combine(root,"staging");Directory.CreateDirectory(stage);
    string source=Path.Combine(stage,"new.pdf"),target=Path.Combine(root,"final.pdf");
    File.WriteAllText(source,"new-pdf");
    try{test(stage,source,target);}
    finally
    {
        string full=Path.GetFullPath(root),prefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("RevitAutomation-PdfTest-",StringComparison.Ordinal))throw new Exception("Ruta de prueba no válida.");
        Directory.Delete(full,true);
    }
}
Scenario("new",(stage,source,target)=>
{
    var publication=new PdfFilePublication(stage);publication.Publish(source,target,false);publication.Cleanup();
    Check(File.ReadAllText(target)=="new-pdf","publicación nueva conserva el PDF generado");
    Check(!Directory.Exists(stage),"éxito limpia la carpeta temporal");
});
Scenario("overwrite",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");var publication=new PdfFilePublication(stage);
    publication.Publish(source,target,true);publication.Cleanup();
    Check(File.ReadAllText(target)=="new-pdf"&&!Directory.Exists(stage),"sobrescritura autorizada publica y limpia el respaldo");
});
Scenario("not-authorized",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");var publication=new PdfFilePublication(stage);
    Fail(()=>publication.Publish(source,target,false));publication.Cleanup();
    Check(File.ReadAllText(target)=="old-pdf","sin autorización se conserva el PDF anterior");
});
Scenario("restore-success",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");var publication=new PdfFilePublication(stage,(from,to)=>{if(from==source)throw new IOException("publish-failure");File.Move(from,to);});
    var error=Fail(()=>publication.Publish(source,target,true));publication.Cleanup();
    Check(File.ReadAllText(target)=="old-pdf","fallo de publicación restaura el PDF anterior");
    Check(error.Message=="publish-failure"&&!Directory.Exists(stage),"restauración correcta conserva el error original y permite limpiar");
});
Scenario("restore-failure",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");string backup=Path.Combine(stage,"previous.pdf");
    var publication=new PdfFilePublication(stage,(from,to)=>{if(from==source||from==backup)throw new IOException(from==source?"publish-failure":"restore-failure");File.Move(from,to);});
    var error=Fail(()=>publication.Publish(source,target,true));publication.Cleanup();
    Check(publication.PreserveStaging&&File.ReadAllText(backup)=="old-pdf","doble fallo conserva la única copia del PDF anterior");
    Check(File.ReadAllText(source)=="new-pdf","doble fallo conserva también el PDF nuevo");
    Check(error.Message.Contains(backup)&&error.Message.Contains("publish-failure")&&error.Message.Contains("restore-failure"),"error identifica la recuperación y ambos fallos");
});
Scenario("concurrent-file",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");
    var publication=new PdfFilePublication(stage,(from,to)=>{if(from==source){File.WriteAllText(target,"other-process");throw new IOException("destination appeared");}File.Move(from,to);});
    Fail(()=>publication.Publish(source,target,true));publication.Cleanup();
    Check(File.ReadAllText(target)=="other-process","recuperación no sobrescribe un archivo aparecido durante la publicación");
    Check(File.ReadAllText(Path.Combine(stage,"previous.pdf"))=="old-pdf","conflicto con otro proceso conserva el respaldo anterior");
});
Scenario("backup-failure",(stage,source,target)=>
{
    File.WriteAllText(target,"old-pdf");var publication=new PdfFilePublication(stage,(_,_)=>throw new IOException("backup-failure"));
    Fail(()=>publication.Publish(source,target,true));publication.Cleanup();
    Check(File.ReadAllText(target)=="old-pdf"&&Directory.Exists(stage),"fallo al apartar el anterior nunca limpia una recuperación incierta");
});
Scenario("long-path",(unusedStage,unusedSource,unusedTarget)=>
{
    string root=Path.GetDirectoryName(unusedTarget)!;
    string destination=root;
    for(int i=0;i<8;i++)destination=Path.Combine(destination,"carpeta-de-exportacion-"+i.ToString("D2"));
    Directory.CreateDirectory(destination);
    Check(destination.Length>260,"prueba utiliza una carpeta real de mas de 260 caracteres");
    Check(ExportPathSupport.HasWriteAccess(destination),"ruta larga admite la comprobacion de escritura sin confundirla con permisos");
    foreach(string format in new[]{"pdf","cad"})
    {
        string stage=ExportPathSupport.CreateStaging(format);
        try
        {
            Check(!stage.StartsWith(destination,StringComparison.OrdinalIgnoreCase)&&stage.Length<260,"temporal "+format+" independiente de la ruta larga");
            string source=Path.Combine(stage,"new."+format),target=Path.Combine(destination,"salida."+format);
            File.WriteAllText(source,"new-export");
            if(format=="pdf")
            {
                File.WriteAllText(target,"old-pdf");
                var publication=new PdfFilePublication(stage);publication.Publish(source,target,true);
                Check(File.ReadAllText(target)=="new-export","publicacion PDF a ruta larga reemplaza el destino autorizado");
            }
            else
            {
                File.Move(source,target);
                Check(File.ReadAllText(target)=="new-export","publicacion de archivo CAD a ruta larga mediante File.Move");
            }
        }
        finally {if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
});
Check(ExportPathSupport.ErrorMessage(new IOException("wrapper",new PathTooLongException("detalle-original")),"ruta").Contains("Detalle original"),
    "limite de ruta anidado produce una ayuda accionable");
Check(ExportPathSupport.ErrorMessage(new IOException("otro-error"))=="otro-error","otros errores conservan su mensaje original");
Console.WriteLine($"PDF_TOTAL={checks} PASS={checks} FAIL=0");
