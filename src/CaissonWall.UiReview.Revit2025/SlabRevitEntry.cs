using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace CaissonWallUi;

[Transaction(TransactionMode.Manual)]
public sealed class OpenSlabReview:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        try{SlabSession.Show(data.Application);return Result.Succeeded;}
        catch(Exception e){Autodesk.Revit.UI.TaskDialog.Show("Slab Reinforcement",e.Message);return Result.Cancelled;}
    }
}

public static class SlabSession
{
    static SlabReviewForm? form;
    static ExternalEvent? external;
    static Handler? handler;
    static UIApplication? app;
    static bool cleanupPending,subscribed;
    static JsonObject? lastBuild,lastError;
    static string dataSource="";
    static long sessionSequence;
    sealed class Owner(IntPtr h):IWin32Window{public IntPtr Handle=>h;}
    static void Log(Exception e,string operation)
    {
        lastError=new JsonObject{["operation"]=operation,["message"]=e.Message,["error"]=e.ToString()};
        Directory.CreateDirectory(SlabModelEngine.LogDirectory);
        File.WriteAllText(Path.Combine(SlabModelEngine.LogDirectory,"ui_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff")+".json"),lastError.ToJsonString());
    }
    public static JsonObject Show(UIApplication ui,SlabDocument? supplied=null,string? draftDirectory=null)
    {
        try{return ShowCore(ui,supplied,draftDirectory);}
        catch(Exception e){Log(e,"show_ui");throw;}
    }
    static JsonObject ShowCore(UIApplication ui,SlabDocument? supplied,string? draftDirectory)
    {
        if(cleanupPending||form is {IsDisposed:true})Cleanup();
        var doc=supplied!=null?SlabModelEngine.ResolveDocument(ui,supplied):ui.ActiveUIDocument?.Document??throw new InvalidOperationException("Open a model first.");
        if(form is {IsDisposed:false})
        {
            var opened=form.Document;
            bool sameDocument=opened.Host.DocumentKey==doc.PathName+"#"+doc.ProjectInformation.UniqueId;
            if(supplied==null&&sameDocument)
            {
                // Reopening keeps the current host and editable draft, including its undo stack.
                // An accepted operation will refresh its scene when its ExternalEvent completes.
                if(handler?.Pending==null)RefreshCurrentContext(ui);
                form.Show();form.Activate();return Status(ui);
            }
            // Preserve the old draft before changing project, host, or explicitly supplied data.
            // Closing can be cancelled by an in-flight request or a failed draft save.
            Close(ui);
            if(form is {IsDisposed:false}||cleanupPending)
                throw new OperationCanceledException("The previous editor is still open. Save and close it before switching parameters.");
        }
        var host=SlabHostReader.ReadHost(ui,supplied?.Host.HostId,doc);
        var design=supplied;
        var loadedFrom=supplied!=null?"supplied":"";
        if(design==null)
        {
            var stored=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Select(SlabModelEngine.Metadata)
                .Where(m=>m?["host_unique_id"]?.ToString()==host.HostUniqueId).GroupBy(m=>m!["owner"]!.ToString()).ToArray();
            if(stored.Length>1)throw new InvalidOperationException("Multiple slab parameter sets exist. Specify a parameter file.");
            if(stored.Length==1)
            {
                design=SlabDocument.FromJson((SlabModelEngine.StoredDesign(doc,stored[0].First()!)??throw new InvalidOperationException("The model parameters cannot be read.")).ToJsonString());
                loadedFrom="model_storage";
            }
            else
            {
                design=SlabDefaultData.TryCreate(host);
                loadedFrom=design!=null?"sample_project_default":"empty_presets";
                if(design==null)
                {
                    var presets=new ReviewDocument();ReviewPresets.Ensure(presets);
                    design=new SlabDocument{Types=presets.Types,Shapes=presets.Shapes};
                    var cover=WallHostReader.ReadSourceCover(doc,host);
                    if(cover.HasValue)design.CheckSettings=new(){TopCoverMm=cover.Value,BottomCoverMm=cover.Value,SideCoverMm=cover.Value,CoverConfirmed=true};
                }
            }
        }
        design.Host=host;design=SlabModelEngine.Refresh(ui,design);
        dataSource=loadedFrom;
        try
        {
            form=new SlabReviewForm(design,draftDirectory);handler=new Handler();external=ExternalEvent.Create(handler);app=ui;
            var current=form;
            form.BuildRequested+=()=>Queue(current,"build");form.RefreshRequested+=()=>Queue(current,"refresh");
            form.ShowModelRequested+=()=>Queue(current,"show");form.SaveRequested+=()=>Queue(current,"save");
            form.FormClosed+=(_,_)=>{if(ReferenceEquals(form,current))cleanupPending=true;};
            ui.Idling+=Idle;subscribed=true;sessionSequence++;form.Show(new Owner(ui.MainWindowHandle));
            return Status(ui);
        }
        catch
        {
            cleanupPending=true;
            try{Cleanup();}catch(Exception e){Log(e,"cleanup_after_show");}
            throw;
        }
    }
    static void RefreshCurrentContext(UIApplication ui)
    {
        if(form is not {IsDisposed:false})return;
        try
        {
            var refreshed=SlabModelEngine.Refresh(ui,form.ExportDocument());
            form.Document.Host=refreshed.Host;form.Document.ExistingBars=refreshed.ExistingBars;
            form.RefreshChecks();
        }
        catch
        {
            form.Document.Host.ExistingScanComplete=false;
            form.Document.Host.ExistingScanNote="The project or host has changed. Reopen the editor.";
            form.RefreshChecks();throw;
        }
    }
    static void Queue(SlabReviewForm current,string operation)
    {
        try
        {
            if(cleanupPending||!ReferenceEquals(current,form)||handler==null||external==null)throw new InvalidOperationException("The editor is closed.");
            if(handler.Pending!=null)throw new InvalidOperationException("Revit is busy.");
            handler.Pending=current.ExportDraftJson();handler.Operation=operation;
            if(external.Raise()!=ExternalEventRequest.Accepted){handler.Pending=null;throw new InvalidOperationException("Revit is busy. Try again shortly.");}
        }
        catch(Exception e){Log(e,operation);current.SetBuildResult(false,e.Message);}
    }
    sealed class Handler:IExternalEventHandler
    {
        public string? Pending;public string Operation="";
        public string GetName()=>"Slab Reinforcement";
        public void Execute(UIApplication ui)
        {
            var pending=Pending;Pending=null;if(pending==null||cleanupPending||!ReferenceEquals(this,handler))return;
            try
            {
                var design=SlabModelEngine.Refresh(ui,SlabDocument.FromJson(pending));
                if(Operation=="build")
                {
                    lastBuild=SlabModelEngine.Build(ui,design);
                    form?.SetBuildResult(lastBuild["status"]?.ToString()=="PASS",lastBuild["message"]?.ToString()??"");
                    if(lastBuild["status"]?.ToString()=="PASS")SlabModelEngine.Show(ui,design);
                }
                else if(Operation=="save")
                {
                    var doc=SlabModelEngine.ResolveDocument(ui,design);if(string.IsNullOrEmpty(doc.PathName))throw new InvalidOperationException("Save the model to a file first.");
                    doc.Save();form?.SetBuildResult(true,"Model saved");
                }
                else if(Operation=="show"){SlabModelEngine.Show(ui,design);form?.SetBuildResult(true,"Model displayed");}
                else form?.SetBuildResult(true,"Host updated");
                form?.SetDocument(SlabModelEngine.Refresh(ui,design));
            }
            catch(Exception e){Log(e,Operation);form?.SetBuildResult(false,e.Message);}
        }
    }
    static void Idle(object? sender,Autodesk.Revit.UI.Events.IdlingEventArgs args)
    {if(cleanupPending||form is {IsDisposed:true})try{Cleanup();}catch(Exception e){Log(e,"cleanup");}}
    static void Cleanup()
    {
        if(form is {IsDisposed:false})form.Dispose();
        if(handler!=null)handler.Pending=null;
        external?.Dispose();external=null;
        if(subscribed&&app!=null){app.Idling-=Idle;subscribed=false;}
        form=null;handler=null;app=null;cleanupPending=false;
    }
    public static void Shutdown(){form?.Close();Cleanup();}
    public static JsonObject Close(UIApplication ui)
    {form?.Close();if(form==null||form.IsDisposed){cleanupPending=true;Cleanup();}return Status(ui);}
    public static JsonObject Status(UIApplication ui)=>new()
    {
        ["status"]="OK",["form_visible"]=form is{IsDisposed:false,Visible:true},["cleanup_pending"]=cleanupPending,
        ["session_sequence"]=sessionSequence,["assembly_path"]=System.Reflection.Assembly.GetExecutingAssembly().Location,
        ["draft_path"]=form is{IsDisposed:false}?form.DraftPath:null,
        ["bound_document_key"]=form is{IsDisposed:false}?form.Document.Host.DocumentKey:null,
        ["bound_host_id"]=form is{IsDisposed:false}?form.Document.Host.HostId:null,
        ["bound_host_unique_id"]=form is{IsDisposed:false}?form.Document.Host.HostUniqueId:null,
        ["model_owner_id"]=form is{IsDisposed:false}?form.Document.ModelOwnerId:null,
        ["draft_session_id"]=form is{IsDisposed:false}?form.Document.SessionId:null,
        ["request_pending"]=handler?.Pending!=null,
        ["data_source"]=form is{IsDisposed:false}?dataSource:null,
        ["external_event_alive"]=external!=null,["idling_subscribed"]=subscribed,["last_ui_error"]=lastError?.DeepClone(),
        ["draft"]=form is{IsDisposed:false}?JsonNode.Parse(form.ExportDraftJson()):null,
        ["validation"]=form is{IsDisposed:false}?JsonSerializer.SerializeToNode(SlabGeometry.Check(form.Document),SlabDocument.JsonOptions):null,
        ["last_build"]=lastBuild?.DeepClone(),["current_document"]=ReviewSession.DocumentSnapshot(ui)
    };
    public static JsonObject Run(UIApplication ui,JsonObject r)
    {
        SlabDocument Design()=>SlabDocument.FromJson(r["design"]!.ToJsonString());
        switch(r["operation"]?.ToString())
        {
            case "slab_show_ui":
                if(r["document_path"] is{} openPath)ui.OpenAndActivateDocument(Path.GetFullPath(openPath.ToString()));
                return Show(ui,r["design"]!=null?Design():null,r["draft_directory"]?.ToString());
            case "slab_ui_status":return Status(ui);
            case "slab_close_ui":return Close(ui);
            case "slab_check":return JsonSerializer.SerializeToNode(SlabGeometry.Check(SlabModelEngine.Refresh(ui,Design())),SlabDocument.JsonOptions)!.AsObject();
            case "slab_build":return SlabModelEngine.Build(ui,Design(),r["inject_failure"]?.GetValue<bool>()??false);
            case "slab_verify":return SlabModelEngine.Verify(ui,Design());
            case "slab_show_model":SlabModelEngine.Show(ui,Design());return Status(ui);
            case "slab_test_collision_tolerance":return TestCollisionTolerance(ui,Design());
            case "slab_readback":
                var actualDesign=Design();var actualDoc=SlabModelEngine.ResolveDocument(ui,actualDesign);
                var data=new JsonArray();
                foreach(var bar in new FilteredElementCollector(actualDoc).OfClass(typeof(Rebar)).Cast<Rebar>())
                {
                    var bt=(RebarBarType)actualDoc.GetElement(bar.GetTypeId());
                    var curves=bar.GetTransformedCenterlineCurves(false,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,0).ToArray();
                    data.Add(new JsonObject{["id"]=bar.Id.Value,["unique_id"]=bar.UniqueId,["host_id"]=bar.GetHostId().Value,
                        ["quantity"]=bar.Quantity,["diameter_mm"]=WallHostReader.Mm(bt.BarModelDiameter),["type_unique_id"]=bt.UniqueId,
                        ["length_mm"]=WallHostReader.Mm(curves.Sum(c=>c.Length)),["metadata"]=SlabModelEngine.Metadata(bar),
                        ["curves"]=JsonSerializer.SerializeToNode(curves.SelectMany(c=>WallHostReader.ToLocalCurves(c,actualDesign.Host)).ToList(),SlabDocument.JsonOptions)});
                }
                return new JsonObject{["status"]="PASS",["host"]=JsonSerializer.SerializeToNode(actualDesign.Host,SlabDocument.JsonOptions),
                    ["native_count"]=data.Count,["bars"]=data,["protected_existing_signature"]=SlabModelEngine.ProtectedSignature(actualDoc,actualDesign),["document"]=ReviewSession.DocumentSnapshot(ui,actualDoc)};
            case "slab_inspect":
                var d=r["document_path"] is{} path?ui.Application.Documents.Cast<Document>().Single(x=>string.Equals(x.PathName,path.ToString(),StringComparison.OrdinalIgnoreCase)):ui.ActiveUIDocument.Document;
                var h=SlabHostReader.ReadHost(ui,null,d);var old=WallHostReader.ReadExisting(d,h);
                return new JsonObject{["status"]="PASS",["host"]=JsonSerializer.SerializeToNode(h,SlabDocument.JsonOptions),["existing"]=JsonSerializer.SerializeToNode(old,SlabDocument.JsonOptions),["document"]=ReviewSession.DocumentSnapshot(ui,d)};
            case "slab_open_copy":return OpenCopy(ui,r);
            case "slab_save":
                var sd=SlabModelEngine.ResolveDocument(ui,Design());sd.Save();return new JsonObject{["status"]="PASS",["sha256"]=SharedHash(sd.PathName),["document"]=ReviewSession.DocumentSnapshot(ui,sd)};
            case "slab_reopen":
                var design=Design();var doc=SlabModelEngine.ResolveDocument(ui,design);var filename=doc.PathName;Close(ui);
                if(!File.Exists(filename+".slab-ui-copy.json"))throw new InvalidOperationException("Only a copy created for this task can be reopened.");
                doc.Save();var hash=SharedHash(filename);
                var holdPath=Path.Combine(Path.GetDirectoryName(filename)!,"临时切换_"+Guid.NewGuid().ToString("N")+".rvt");
                var blank=ui.Application.NewProjectDocument(UnitSystem.Metric);blank.SaveAs(holdPath);blank.Close(false);
                var hold=ui.OpenAndActivateDocument(holdPath).Document;
                if(!doc.Close(false))throw new InvalidOperationException("The copy did not close.");
                ui.OpenAndActivateDocument(filename);hold.Close(false);
                var result=SlabModelEngine.Verify(ui,design);result["file_sha256_before"]=hash;result["file_sha256_after"]=SharedHash(filename);result["disk_reopened"]=true;return result;
            default:throw new ArgumentException("Unknown slab operation.");
        }
    }
    static JsonObject TestCollisionTolerance(UIApplication ui,SlabDocument source)
    {
        var doc=SlabModelEngine.ResolveDocument(ui,source);
        var probe=SlabDocument.FromJson(source.ToJson());
        probe.ModelOwnerId="slab-tolerance-probe-"+Guid.NewGuid().ToString("N");
        probe.CheckSettings.CollisionToleranceMm=2;
        probe.Types=new(){new ReviewBarType{Id="probe16",Name="HRB400 Φ16",Grade="HRB400",DiameterMm=16}};
        probe.Groups=new(){new SlabGroup{Id="probe",Name="Temporary tolerance check",TypeId="probe16",Kind=SlabGroupKind.Orthogonal,
            Enabled=true,PositionConfirmed=true,UseEndOffset=false,Count=2,FitToBoundary=false,Z=-200,StartOffset=0,
            ShapeSnapshot=new(){new SketchSegment{Kind=SegmentKind.Line,Start=new(-300,0),End=new(300,0)}},
            StretchSegmentIndex=0,ReferenceSpan=600}};
        var before=SlabModelEngine.ProtectedSignature(doc,probe);
        int countBefore=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount();
        var results=new JsonArray();bool ok=true;
        foreach(double overlap in new[]{1.999,2.0,2.001})
        {
            probe.Groups[0].Spacing=16-overlap;
            using var tx=new TransactionGroup(doc,"Temporary Slab Clash Tolerance Check");tx.Start();
            var result=SlabModelEngine.Build(ui,probe);
            bool expected=overlap<=2;
            bool observed=result["status"]?.ToString()=="PASS";
            bool rolledBack=tx.RollBack()==TransactionStatus.RolledBack;
            bool preserved=before==SlabModelEngine.ProtectedSignature(doc,probe)
                &&new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount()==countBefore
                &&SlabModelEngine.Owned(doc,probe).Count==0;
            bool passed=observed==expected&&rolledBack&&preserved;
            if(expected)passed&=result["solid_accepted_overlap_pairs"]?.GetValue<int>()==1;
            ok&=passed;
            results.Add(new JsonObject{["penetration_mm"]=overlap,["expected_acceptance"]=expected,["passed"]=passed,
                ["rolled_back"]=rolledBack,["source_preserved"]=preserved,["native_result"]=result});
        }
        return new JsonObject{["status"]=ok?"PASS":"FAIL",["cases"]=results,["native_count_before"]=countBefore,
            ["native_count_after"]=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),["all_test_geometry_rolled_back"]=true};
    }
    static string SharedHash(string p){using var s=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(SHA256.HashData(s));}
    static JsonObject OpenCopy(UIApplication ui,JsonObject r)
    {
        var source=Path.GetFullPath(r["source_path"]!.ToString());var target=Path.GetFullPath(r["target_path"]!.ToString());
        var root=Path.GetFullPath(Path.Combine(RuntimePaths.Root,"deliverables"))+Path.DirectorySeparatorChar;
        if(!source.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!target.EndsWith(".rvt",StringComparison.OrdinalIgnoreCase)||File.Exists(target))throw new InvalidOperationException("Specify a new copy path inside the project directory.");
        var open=ui.Application.Documents.Cast<Document>().Where(d=>!d.IsFamilyDocument).ToDictionary(d=>d,d=>d.IsModified);
        if(open.Keys.Any(d=>string.Equals(d.PathName,source,StringComparison.OrdinalIgnoreCase)&&d.IsModified))throw new InvalidOperationException("The source model has unsaved changes. Confirm the copy source first.");
        var sourceHash=SharedHash(source);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
        if(SharedHash(target)!=sourceHash)throw new InvalidOperationException("Copy verification failed.");
        var doc=ui.OpenAndActivateDocument(target).Document;
        if(open.Any(p=>!p.Key.IsValidObject||p.Key.IsModified!=p.Value))throw new InvalidOperationException("The state of an existing project has changed.");
        var receipt=new JsonObject{["status"]="PASS",["source_path"]=source,["source_sha256"]=sourceHash,["target_path"]=target,["project_uid"]=doc.ProjectInformation.UniqueId,["preserved_open_documents"]=true};
        File.WriteAllText(target+".slab-ui-copy.json",receipt.ToJsonString());
        receipt["host"]=JsonSerializer.SerializeToNode(SlabHostReader.ReadHost(ui,null,doc),SlabDocument.JsonOptions);receipt["document"]=ReviewSession.DocumentSnapshot(ui,doc);return receipt;
    }
}
