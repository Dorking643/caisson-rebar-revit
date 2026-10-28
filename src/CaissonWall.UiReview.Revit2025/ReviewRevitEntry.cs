using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace CaissonWallUi
{
    public static class ReviewSession
    {
        private static WallReviewForm? _form;
        private static JsonObject? _openedDocument;
        private static JsonObject? _lastResult;
        private static ExternalEvent? _external;
        private static BuildHandler? _handler;
        private static bool _sceneDirty;
        private static UIApplication? _application;
        private static bool _cleanupPending,_documentChangedSubscribed,_idlingSubscribed;
        private static long _sessionSequence;
        private static JsonObject? _lastUiError;
        public static string AssemblyPath=>Assembly.GetExecutingAssembly().Location;
        private sealed class Owner(IntPtr handle):IWin32Window {public IntPtr Handle=>handle;}
        public static JsonObject Show(UIApplication ui,ReviewDocument? supplied=null,string? draftDirectory=null)
        {
            try{return ShowCore(ui,supplied,draftDirectory);}
            catch(Exception e){RecordUiError("show_ui",e);throw;}
        }
        static JsonObject ShowCore(UIApplication ui,ReviewDocument? supplied,string? draftDirectory)
        {
            // Show is an API entry point. Finish an old modeless close before replacing any session fields.
            if(_cleanupPending||_form is {IsDisposed:true}){_cleanupPending=true;CleanupSession();}
            var boundDocument=supplied!=null?WallModelEngine.ResolveDocument(ui,supplied):ui.ActiveUIDocument?.Document??throw new InvalidOperationException("No active project.");
            if(_form is {IsDisposed:false})
            {
                if(_handler?.Pending!=null||!_form.Enabled)
                    throw new InvalidOperationException("A rebar operation is in progress. Reopen the editor when it finishes.");
                // Every open reads the live project. Preserve the old draft on disk first;
                // a failed/cancelled save must never be reported as a successful reload.
                draftDirectory??=Path.GetDirectoryName(_form.DraftPath);
                Close(ui);
                if(_form is {IsDisposed:false}||_cleanupPending)
                    throw new OperationCanceledException("The previous editor is still open. Save and close it before switching projects.");
            }
            _openedDocument=DocumentSnapshot(ui,boundDocument);var host=WallHostReader.ReadHost(ui,supplied?.Host.HostId,boundDocument);
            try
            {
            _form=new WallReviewForm(host,draftDirectory:draftDirectory);
            var librarySeed=ReviewDocument.FromJson(_form.ExportDraftJson());
            // Explicit API-supplied designs remain available for controlled preview/testing.
            // The ribbon command supplies none and always resolves current native model data.
            var design=supplied??WallStartupLoader.Load(ui,boundDocument,host,librarySeed);
            design=WallModelEngine.Refresh(ui,design);
            if(!design.CheckSettings.CoverConfirmed&&WallHostReader.ReadSourceCover(WallModelEngine.ResolveDocument(ui,design),design.Host) is double cover)
            {
                design.CheckSettings.InnerCoverMm=design.CheckSettings.OuterCoverMm=cover;
                design.CheckSettings.TopCoverMm=design.CheckSettings.BottomCoverMm=cover;design.CheckSettings.CoverConfirmed=true;
            }
            _form.ReplaceDraftDocument(design);
            _sessionSequence++;_sceneDirty=false;
            _handler=new BuildHandler();_external=ExternalEvent.Create(_handler);
            var form=_form;var handler=_handler;var external=_external;
            form.BuildRequested+=()=>QueueRequest(form,handler,external,"build");
            // FormClosed is a modeless callback: do not unsubscribe Revit events or dispose ExternalEvent here.
            form.FormClosed+=(_,_)=>{if(ReferenceEquals(_form,form))_cleanupPending=true;};
            form.FormClosing+=(_,e)=>{if(e.Cancel&&ReferenceEquals(_form,form))_sceneDirty=true;};
            _application=ui;
            ui.Application.DocumentChanged+=DocumentChanged;_documentChangedSubscribed=true;
            ui.Idling+=Idle;_idlingSubscribed=true;
            _form.Show(new Owner(ui.MainWindowHandle));_form.RefreshChecks();return Status(ui);
            }
            catch
            {
                _cleanupPending=true;
                try{CleanupSession();}catch{/* Cleanup logs its own error and remains retryable. */}
                throw;
            }
        }
        static void QueueRequest(WallReviewForm form,BuildHandler handler,ExternalEvent external,string operation)
        {
            try
            {
                if(_cleanupPending||!ReferenceEquals(_form,form)||!ReferenceEquals(_handler,handler)||form.IsDisposed)
                    throw new InvalidOperationException("The editor is closed. Reopen Wall Reinforcement.");
                if(handler.Pending!=null)throw new InvalidOperationException("Revit is busy. Try again shortly.");
                handler.PendingOperation=operation;handler.Pending=form.ExportDraftJson();
                try
                {
                    if(external.Raise()!=ExternalEventRequest.Accepted)throw new InvalidOperationException("Revit is busy. Try again shortly.");
                }
                catch{handler.Pending=null;throw;}
            }
            catch(Exception e){RecordUiError(operation+"_request",e);throw;}
        }
        // Call only from Show/Close, Idling, ExternalEvent.Execute, or application shutdown (valid API contexts).
        static void CleanupSession()
        {
            if(!_cleanupPending)return;
            try
            {
                if(_handler!=null)_handler.Pending=null;
                if(_form is {IsDisposed:false})_form.Dispose();
                if(_external!=null){_external.Dispose();_external=null;}
                if(_application!=null&&_documentChangedSubscribed){_application.Application.DocumentChanged-=DocumentChanged;_documentChangedSubscribed=false;}
                // Idling is removed last, so a failed earlier cleanup can retry in the next legal API context.
                if(_application!=null&&_idlingSubscribed){_application.Idling-=Idle;_idlingSubscribed=false;}
                _form=null;_handler=null;_application=null;_sceneDirty=false;_cleanupPending=false;
            }
            catch(Exception e){RecordUiError("cleanup",e);throw;}
        }
        public static void RecordUiError(string stage,Exception error)
        {
            // Do not inspect a Document/UIApplication here: this also runs outside Revit API context.
            var context=new JsonObject{["session_sequence"]=_sessionSequence,["cleanup_pending"]=_cleanupPending,
                ["document_changed_subscribed"]=_documentChangedSubscribed,["idling_subscribed"]=_idlingSubscribed,
                ["request_pending"]=_handler?.Pending!=null,["opened_document"]=_openedDocument?.DeepClone(),["last_build"]=_lastResult?.DeepClone()};
            var path=WallBuildLog.SaveDiagnostic(stage,error,context);
            _lastUiError=new JsonObject{["stage"]=stage,["message"]=error.Message,["error"]=error.ToString(),["log_path"]=path};
        }
        static void ReportToForm(bool success,string message)
        {
            if(_cleanupPending||_form is not {IsDisposed:false})return;
            try{_form.SetModelResult(success,message);}catch(Exception e){RecordUiError("show_result",e);}
        }
        static void DocumentChanged(object? sender,Autodesk.Revit.DB.Events.DocumentChangedEventArgs args){if(!_cleanupPending)_sceneDirty=true;}
        static void Idle(object? sender,Autodesk.Revit.UI.Events.IdlingEventArgs args)
        {
            try
            {
                if(_cleanupPending||_form is {IsDisposed:true}){_cleanupPending=true;CleanupSession();return;}
                if(!_sceneDirty||_form is not {IsDisposed:false,Visible:true}||_handler?.Pending!=null)return;
                _sceneDirty=false;RefreshScene((UIApplication)sender!);
            }
            catch(Exception e){RecordUiError("idling",e);ReportToForm(false,e.Message);}
        }
        public static void RefreshScene(UIApplication ui)
        {
            if(_form is not {IsDisposed:false})return;
            var source=ReviewDocument.FromJson(_form.ExportDraftJson());
            try{var design=WallModelEngine.Refresh(ui,source);_form.ReplaceContext(design.Host,design.ExistingBars);_form.RefreshChecks();}
            catch{source.Host.ExistingScanComplete=false;source.Host.ExistingScanNote="The project or host has changed. Reopen the editor.";_form.ReplaceContext(source.Host);_form.RefreshChecks();throw;}
        }
        sealed class BuildHandler:IExternalEventHandler
        {
            public string? Pending;
            public string PendingOperation="build";
            public string GetName()=>"Create Wall Reinforcement";
            public void Execute(UIApplication ui)
            {
                // An accepted event from an older closed window must never build or update a replacement window.
                if(!ReferenceEquals(this,_handler)){Pending=null;return;}
                if(_cleanupPending||_form is not {IsDisposed:false})
                {
                    _cleanupPending=true;
                    try{CleanupSession();}catch{/* Keep a failed cleanup pending for the next API entry. */}
                    return;
                }
                var json=Pending;var operation=PendingOperation;Pending=null;if(json==null)return;
                try
                {
                    var design=ReviewDocument.FromJson(json);
                    _lastResult=WallModelEngine.Build(ui,design);
                    RefreshScene(ui);bool success=_lastResult["status"]?.ToString()=="PASS";
                    ReportToForm(success,_lastResult["message"]?.ToString()??"Model creation complete.");
                    if(success)WallModelEngine.Show(ui,design);
                }
                catch(Exception e)
                {
                    // A refresh/view failure after a committed build is a UI failure, not a rolled-back build.
                    RecordUiError(operation+"_execute",e);ReportToForm(false,e.Message);
                }
                finally{_sceneDirty=false;}
            }
        }
        public static JsonObject Close(UIApplication ui)
        {
            try
            {
                if(_form is {IsDisposed:false})_form.Close();
                // FormClosing may cancel after a failed draft save; keep that window's live session intact.
                if(_cleanupPending||_form is {IsDisposed:true}){_cleanupPending=true;CleanupSession();}
                return Status(ui);
            }
            catch(Exception e){RecordUiError("close_ui",e);throw;}
        }
        public static void Shutdown()
        {
            try{if(_form is {IsDisposed:false})_form.Close();}
            finally{_cleanupPending=true;CleanupSession();}
        }
        /// <summary>Explicit migration command. Only older loaded versions of this plugin; API context required.</summary>
        public static JsonObject RetireLegacyUi(UIApplication ui)
        {
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
            var pattern=new System.Text.RegularExpressions.Regex(@"^P7\.CaissonWallUi\.R(\d+)$");
            var current=pattern.Match(Assembly.GetExecutingAssembly().GetName().Name??"");
            if(!current.Success)throw new InvalidOperationException("The current plugin version cannot be verified.");
            int currentVersion=int.Parse(current.Groups[1].Value);
            var rows=new JsonArray();bool failed=false;
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a=>a.GetName().Name,StringComparer.Ordinal))
            {
                string name=assembly.GetName().Name??"";var version=pattern.Match(name);
                if(!version.Success||!int.TryParse(version.Groups[1].Value,out int number)||number>=currentVersion)continue;
                var row=new JsonObject{["assembly"]=name};rows.Add(row);
                var type=assembly.GetType("CaissonWallUi.ReviewSession",false);
                if(type==null){row["status"]="SKIPPED";row["message"]="No editor session in this version.";continue;}
                try
                {
                    // Let the original window persist its draft and honor a user-cancelled close.
                    try{type.GetMethod("Close",flags,null,new[]{typeof(UIApplication)},null)?.Invoke(null,new object[]{ui});}
                    catch(Exception e)
                    {
                        var cause=e is TargetInvocationException {InnerException:not null} target?target.InnerException!:e;
                        row["close_error"]=cause.ToString();RecordUiError("retire_legacy_close_"+name,cause);
                    }
                    if(type.GetField("_form",flags)?.GetValue(null) is System.Windows.Forms.Form {IsDisposed:false})
                        throw new InvalidOperationException("The previous editor is still in use or its close was cancelled; event connections were retained.");

                    var errors=new List<Exception>();
                    void Attempt(Action action){try{action();}catch(Exception e){errors.Add(e is TargetInvocationException {InnerException:not null} t?t.InnerException!:e);}}
                    Attempt(()=>
                    {
                        var handler=type.GetField("_handler",flags)?.GetValue(null);
                        handler?.GetType().GetField("Pending",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.SetValue(handler,null);
                    });
                    Attempt(()=>
                    {
                        var method=type.GetMethod("DocumentChanged",flags);var evt=ui.Application.GetType().GetEvent("DocumentChanged");
                        if(method!=null&&evt!=null)evt.RemoveEventHandler(ui.Application,Delegate.CreateDelegate(evt.EventHandlerType!,method));
                    });
                    Attempt(()=>
                    {
                        var method=type.GetMethod("Idle",flags);var evt=typeof(UIApplication).GetEvent("Idling");
                        if(method!=null&&evt!=null)evt.RemoveEventHandler(ui,Delegate.CreateDelegate(evt.EventHandlerType!,method));
                    });
                    Attempt(()=>{if(type.GetField("_external",flags)?.GetValue(null) is IDisposable external)external.Dispose();});
                    if(errors.Count>0)throw new AggregateException("Cleanup of previous editor versions is incomplete.",errors);
                    foreach(string field in new[]{"_form","_handler","_external","_application"})type.GetField(field,flags)?.SetValue(null,null);
                    foreach(string field in new[]{"_sceneDirty","_cleanupPending","_documentChangedSubscribed","_idlingSubscribed"})type.GetField(field,flags)?.SetValue(null,false);
                    row["status"]="PASS";row["form_closed"]=true;row["callbacks_retired"]=true;
                }
                catch(Exception e){failed=true;row["status"]="FAIL";row["error"]=e.ToString();RecordUiError("retire_legacy_"+name,e);}
            }
            return new JsonObject{["status"]=failed?"FAIL":"PASS",["versions"]=rows,["model_writes"]=false};
        }
        public static JsonObject Status(UIApplication ui)
        {
            var now=DocumentSnapshot(ui);
            var draft=_form is {IsDisposed:false}?JsonNode.Parse(_form.ExportDraftJson()):null;
            return new JsonObject
            {
                ["stage"]="checked_modeling",["status"]="OK",["form_visible"]=_form is {IsDisposed:false,Visible:true},
                ["window_title"]=_form is {IsDisposed:false}?_form.Text:null,["assembly"]=AssemblyPath,
                ["model_writes_enabled"]=true,["engineering_checks_enabled"]=true,
                ["check_scope"]="Geometric clashes and specified clear cover; excludes structural design review",
                ["draft"]=draft,
                ["bound_document_key"]=draft?["host"]?["document_key"]?.ToString(),
                ["model_owner_id"]=draft?["model_owner_id"]?.ToString(),
                ["draft_count"]=draft?["drafts"]?.AsArray().Count,
                ["planned_bar_count"]=_form is {IsDisposed:false}?_form.LatestValidation?.Bars.Count:null,
                ["validation"]=_form is {IsDisposed:false}?JsonSerializer.SerializeToNode(_form.LatestValidation,ReviewDocument.JsonOptions):null,
                ["last_build"]=_lastResult?.DeepClone(),["opened_document"]=_openedDocument?.DeepClone(),["current_document"]=now,
                ["last_ui_error"]=_lastUiError?.DeepClone(),["session_sequence"]=_sessionSequence,["cleanup_pending"]=_cleanupPending,
                ["document_changed_subscribed"]=_documentChangedSubscribed,["idling_subscribed"]=_idlingSubscribed,["external_event_alive"]=_external!=null,
                ["request_pending"]=_handler?.Pending!=null,
                ["element_identity_unchanged"]=_openedDocument is null?null:_openedDocument["element_identity_sha256"]?.ToString()==now["element_identity_sha256"]?.ToString()
            };
        }
        /// <summary>Reads native storage without opening, replacing or saving a UI draft.</summary>
        public static JsonObject ReadModelDesign(UIApplication ui,long? hostId=null)
        {
            var doc=ui.ActiveUIDocument?.Document??throw new InvalidOperationException("No active project.");
            var host=WallHostReader.ReadHost(ui,hostId,doc);
            var hostBars=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>()
                .Where(b=>b.GetHostId().Value==host.HostId).ToArray();
            var native=hostBars.Select(b=>(Bar:b,Meta:WallModelEngine.Metadata(b)))
                .Where(x=>x.Meta?["host_unique_id"]?.ToString()==host.HostUniqueId
                    &&(x.Meta?["design"]!=null||x.Meta?["design_storage_id"]!=null)).ToArray();
            var owners=native.GroupBy(x=>x.Meta!["owner"]?.ToString()).ToArray();
            if(owners.Length!=1||string.IsNullOrWhiteSpace(owners[0].Key))
                throw new InvalidOperationException(owners.Length==0?"No recoverable native rebar parameters were found for this wall.":"Multiple native rebar parameter sets exist for this wall. Automatic selection is unavailable.");
            var rows=owners[0].ToArray();
            var storageIds=rows.Select(x=>x.Meta!["design_storage_id"]?.ToString()).Distinct().ToArray();
            if(storageIds.Length!=1)throw new InvalidOperationException("The rebar references different parameter records. No automatic selection was made.");
            var saved=WallModelEngine.StoredDesign(doc,rows[0].Meta!)??throw new InvalidOperationException("The stored rebar draft cannot be read.");
            var design=ReviewDocument.FromJson(saved.ToJsonString());
            if(design.ModelOwnerId!=owners[0].Key||design.Host.HostUniqueId!=host.HostUniqueId)
                throw new InvalidOperationException("The stored rebar group or host identity does not match.");
            // A saved copy has a new path; only the live document/host context is rebound.
            // Keep the original owner, draft/member keys and import-source identity intact.
            design.Host=host;design=WallModelEngine.Refresh(ui,design);
            var planned=WallValidation.Check(design);var errors=new List<string>();
            var expected=planned.Bars.GroupBy(b=>(b.DraftId,b.MemberIndex)).ToDictionary(g=>g.Key,g=>g.ToArray());
            if(expected.Any(x=>x.Value.Length!=1))errors.Add("Duplicate rebar path identifiers exist in the parameters.");
            var visited=new HashSet<(string,int)>();var bindings=new JsonArray();
            foreach(var row in rows)
            {
                var bar=row.Bar;var meta=row.Meta!;
                string id=meta["draft"]?.ToString()??"";int member=meta["member"]?.GetValue<int>()??-1;
                if(!visited.Add((id,member)))errors.Add($"Duplicate native rebar path identifier: {id}/{member}");
                expected.TryGetValue((id,member),out var candidates);
                var type=doc.GetElement(bar.GetTypeId()) as RebarBarType;
                double? diameter=type==null?null:WallHostReader.Mm(type.BarModelDiameter);
                if(candidates?.Length!=1)errors.Add($"Native rebar {bar.Id.Value} has no unique parameter path.");
                else if(diameter==null||Math.Abs(diameter.Value-candidates[0].DiameterMm)>.001)
                    errors.Add($"Native rebar {bar.Id.Value} has a different diameter from its parameters.");
                if(bar.Quantity!=1||bar.NumberOfBarPositions!=1)errors.Add($"Native rebar {bar.Id.Value} does not match its single-bar record.");
                bindings.Add(new JsonObject{["id"]=bar.Id.Value,["unique_id"]=bar.UniqueId,
                    ["draft"]=id,["member"]=member,["host_id"]=bar.GetHostId().Value,
                    ["type_id"]=bar.GetTypeId().Value,["diameter_mm"]=diameter,["quantity"]=bar.Quantity,
                    ["design_storage_id"]=meta["design_storage_id"]?.DeepClone()});
            }
            if(!visited.SetEquals(expected.Keys))errors.Add("Native rebar coverage does not match the parameter paths.");
            return new JsonObject{["status"]=errors.Count==0?"PASS":"FAIL",["read_only"]=true,["ui_draft_unchanged"]=true,
                ["source"]="native_model_storage",["document_path"]=doc.PathName,["model_owner_id"]=design.ModelOwnerId,
                ["draft_count"]=design.Drafts.Count,["planned_bar_count"]=planned.Bars.Count,["native_bar_count"]=rows.Length,
                ["native_host_total_count"]=hostBars.Length,["unmanaged_host_bar_count"]=hostBars.Length-rows.Length,
                ["type_count"]=design.Types.Count,["shape_count"]=design.Shapes.Count,
                ["can_build"]=planned.CanBuild,["binding_checks_passed"]=errors.Count==0,
                ["errors"]=new JsonArray(errors.Select(e=>JsonValue.Create(e)).ToArray()),["bindings"]=bindings,
                ["design"]=JsonNode.Parse(design.ToJson()),
                ["scope"]="Reads model storage and checks groups, path identifiers, hosts, diameters and quantities; does not replace curve, opening or solid-clash verification."};
        }
        public static HostContext ReadContext(UIApplication ui)=>WallHostReader.ReadHost(ui);
        public static JsonObject DocumentSnapshot(UIApplication ui,Document? document=null)
        {
            var doc=document??ui.ActiveUIDocument?.Document;if(doc is null)return new JsonObject{["document"]=null};
            var all=new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
            var identity=string.Join("\n",all.OrderBy(e=>e.Id.Value).Select(e=>$"{e.Id.Value}:{e.UniqueId}:{e.GetType().FullName}"));
            return new JsonObject{["document"]=doc.Title,["path"]=doc.PathName,["is_modified"]=doc.IsModified,["is_modifiable"]=doc.IsModifiable,
                ["elements"]=all.Count,["rebar"]=all.Count(e=>e is Rebar),["element_identity_sha256"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))};
        }
    }
    [Transaction(TransactionMode.Manual)]
    public sealed class OpenWallReview:IExternalCommand
    {
        public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
        {
            try{ReviewSession.Show(data.Application);return Result.Succeeded;}
            catch(Exception ex)
            {
                // A recoverable parameter/window error should not open Revit's fatal
                // external-command failure dialog; Show already records diagnostics.
                Autodesk.Revit.UI.TaskDialog.Show("Wall Reinforcement",ex.Message);
                return Result.Cancelled;
            }
        }
    }
    public sealed class App:IExternalApplication
    {
        UIControlledApplication? _startupApplication;
        public Result OnStartup(UIControlledApplication app)
        {
            try{Register(()=>app.CreateRibbonTab(RibbonLayout.TabName),()=>app.GetRibbonPanels(RibbonLayout.TabName),()=>app.CreateRibbonPanel(RibbonLayout.TabName,RibbonLayout.PanelName),ReviewSession.AssemblyPath);_startupApplication=app;app.Idling+=OnFirstIdle;return Result.Succeeded;}catch(Exception e){ReviewSession.RecordUiError("startup",e);return Result.Failed;}
        }
        void OnFirstIdle(object? sender,Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            // Other add-ins can create their ribbon after this application's startup.
            if(_startupApplication is {} app){app.Idling-=OnFirstIdle;_startupApplication=null;}
            try{RibbonLayout.HideLegacyTabs();}catch(Exception error){ReviewSession.RecordUiError("ribbon_visibility",error);}
        }
        public Result OnShutdown(UIControlledApplication app)
        {try{if(_startupApplication is {} startup){startup.Idling-=OnFirstIdle;_startupApplication=null;}SlabSession.Shutdown();ReviewSession.Shutdown();return Result.Succeeded;}catch(Exception e){ReviewSession.RecordUiError("shutdown",e);return Result.Failed;}}
        public static void Register(UIApplication app,string path)=>Register(()=>app.CreateRibbonTab(RibbonLayout.TabName),()=>app.GetRibbonPanels(RibbonLayout.TabName),()=>app.CreateRibbonPanel(RibbonLayout.TabName,RibbonLayout.PanelName),path);
        private static void Register(Action createTab,Func<IList<RibbonPanel>> getPanels,Func<RibbonPanel> createPanel,string path)
        {
            try{createTab();}catch(Autodesk.Revit.Exceptions.ArgumentException){}
            var panel=getPanels().FirstOrDefault(p=>p.Name==RibbonLayout.PanelName)??createPanel();var id="P7.CaissonWallUi."+Assembly.GetExecutingAssembly().GetName().Name;
            if(!panel.GetItems().Any(i=>i.Name==id))panel.AddItem(new PushButtonData(id,"Wall",path,typeof(OpenWallReview).FullName!){ToolTip="Define wall reinforcement and create native rebar."});
            foreach(var old in panel.GetItems().Where(i=>i.Name.StartsWith("P7.CaissonWallUi.")&&i.Name!=id))old.Visible=false;
            var slabId="P7.CaissonSlabUi."+Assembly.GetExecutingAssembly().GetName().Name;
            if(!panel.GetItems().Any(i=>i.Name==slabId))panel.AddItem(new PushButtonData(slabId,"Slab",path,typeof(OpenSlabReview).FullName!){ToolTip="Define slab reinforcement and create native rebar."});
            foreach(var old in panel.GetItems().Where(i=>i.Name.StartsWith("P7.CaissonSlabUi.")&&i.Name!=slabId))old.Visible=false;
            RibbonLayout.HideLegacyTabs();
        }
    }
}
namespace PileCage
{
    public static class NativeEngine
    {
        static string Root => CaissonWallUi.RuntimePaths.Root;
        static string SharedHash(string p){using var stream=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(SHA256.HashData(stream));}
        public static JsonObject Run(UIApplication ui,string requestJson)
        {
            JsonObject? r=null;
            try{r=JsonNode.Parse(requestJson)!.AsObject();return RunInner(ui,r);}
            catch(Exception e){CaissonWallUi.ReviewSession.RecordUiError("bridge_"+(r?["operation"]?.ToString()??"parse"),e);return new JsonObject{["status"]="FAIL",["message"]=e.Message,["error"]=e.ToString()};}
        }
        static CaissonWallUi.ReviewDocument Design(JsonObject r)=>CaissonWallUi.ReviewDocument.FromJson(r["design"]!.ToJsonString());
        static JsonObject RunInner(UIApplication ui,JsonObject r)
        {
            var op=r["operation"]?.ToString();
            if(op?.StartsWith("slab_")==true)return CaissonWallUi.SlabSession.Run(ui,r);
            switch(op)
            {
                case "show_ui":return CaissonWallUi.ReviewSession.Show(ui,r["design"]!=null?Design(r):null,r["draft_directory"]?.ToString());
                case "ui_status":return CaissonWallUi.ReviewSession.Status(ui);
                case "read_model_design":return CaissonWallUi.ReviewSession.ReadModelDesign(ui,r["host_id"]?.GetValue<long>());
                case "refresh_ui":CaissonWallUi.ReviewSession.RefreshScene(ui);return CaissonWallUi.ReviewSession.Status(ui);
                case "close_ui":return CaissonWallUi.ReviewSession.Close(ui);
                case "retire_legacy_ui":return CaissonWallUi.ReviewSession.RetireLegacyUi(ui);
                case "register_ui":CaissonWallUi.App.Register(ui,r["assembly_path"]?.ToString()??CaissonWallUi.ReviewSession.AssemblyPath);return CaissonWallUi.ReviewSession.Status(ui);
                case "import_rebar":
                    var input=r["design"]!=null?Design(r):new CaissonWallUi.ReviewDocument{Host=CaissonWallUi.WallHostReader.ReadHost(ui)};
                    CaissonWallUi.ReviewPresets.Ensure(input);
                    var imported=CaissonWallUi.WallRebarImporter.Import(ui,input);
                    var importedResult=JsonSerializer.SerializeToNode(imported,CaissonWallUi.ReviewDocument.JsonOptions)!.AsObject();
                    importedResult.Remove("document");importedResult["status"]="PASS";importedResult["design"]=JsonNode.Parse(imported.Document.ToJson());
                    if(r["show_ui"]?.GetValue<bool>()==true)importedResult["ui"]=CaissonWallUi.ReviewSession.Show(ui,imported.Document,r["draft_directory"]?.ToString());
                    return importedResult;
                case "inspect_scene":
                    var ctx=CaissonWallUi.WallHostReader.ReadHost(ui);var scene=CaissonWallUi.WallHostReader.ReadExisting(ui.ActiveUIDocument.Document,ctx);
                    return new JsonObject{["status"]="PASS",["host"]=JsonSerializer.SerializeToNode(ctx,CaissonWallUi.ReviewDocument.JsonOptions),["cover_mm"]=CaissonWallUi.WallHostReader.ReadSourceCover(ui.ActiveUIDocument.Document,ctx),["existing"]=JsonSerializer.SerializeToNode(scene,CaissonWallUi.ReviewDocument.JsonOptions),["document"]=CaissonWallUi.ReviewSession.DocumentSnapshot(ui)};
                case "check":return JsonSerializer.SerializeToNode(CaissonWallUi.WallValidation.Check(CaissonWallUi.WallModelEngine.Refresh(ui,Design(r))),CaissonWallUi.ReviewDocument.JsonOptions)!.AsObject();
                case "build":return CaissonWallUi.WallModelEngine.Build(ui,Design(r),r["inject_failure"]?.GetValue<bool>()??false);
                case "verify":return CaissonWallUi.WallModelEngine.Verify(ui,Design(r));
                case "show_model":CaissonWallUi.WallModelEngine.Show(ui,Design(r));return new JsonObject{["status"]="PASS"};
                case "save_copy":
                    var path=Path.GetFullPath(r["path"]!.ToString());
                    if(!path.StartsWith(Path.Combine(Root,"deliverables")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!path.EndsWith(".rvt",StringComparison.OrdinalIgnoreCase)||File.Exists(path))throw new InvalidOperationException("The test-copy path is invalid or already exists.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);ui.ActiveUIDocument.Document.SaveAs(path,new SaveAsOptions{OverwriteExistingFile=false});
                    return new JsonObject{["status"]="PASS",["document"]=CaissonWallUi.ReviewSession.DocumentSnapshot(ui)};
                case "reopen":
                    var d=ui.ActiveUIDocument.Document;var p=d.PathName;
                    if(!p.StartsWith(Path.Combine(Root,"deliverables","TestCopy_"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only the current test copy can be reopened.");
                    var closed=CaissonWallUi.ReviewSession.Close(ui);
                    if(closed["form_visible"]?.GetValue<bool>()==true||closed["cleanup_pending"]?.GetValue<bool>()==true)throw new InvalidOperationException("The wall editor is still open. The project was not reopened.");
                    d.Save();var before=SharedHash(p);
                    var temp=ui.Application.NewProjectDocument(UnitSystem.Metric);var holdpath=Path.Combine(Root,"runtime","wall_ui_review","switch-"+Guid.NewGuid().ToString("N")+".rvt");Directory.CreateDirectory(Path.GetDirectoryName(holdpath)!);temp.SaveAs(holdpath);temp.Close(false);
                    var hold=ui.OpenAndActivateDocument(holdpath).Document;if(!d.Close(false))throw new InvalidOperationException("The test document did not close.");
                    var reopened=ui.OpenAndActivateDocument(p).Document;hold.Close(false);
                    var result=CaissonWallUi.WallModelEngine.Verify(ui,Design(r));result["disk_reopened"]=true;result["sha256_before"]=before;result["sha256_after"]=SharedHash(p);return result;
                default:throw new ArgumentException("Unknown operation: "+op);
            }
        }
    }
}
