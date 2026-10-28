using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaissonWallUi;

// This namespace owns only elements carrying this schema AND the persistent model owner id.
public static class SlabModelEngine
{
    public static readonly Guid SchemaId = new("8bd2d521-e3b8-4485-a733-a601a94a8ed1");
    public static readonly string LogDirectory=RuntimePaths.SlabLogs;
    public const string ViewName = "Slab Reinforcement";
    const string LegacyViewName = "底板钢筋_交互核查";
    static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    static double Mm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    public static Document ResolveDocument(UIApplication ui,SlabDocument design)
    {
        var docs=ui.Application.Documents.Cast<Document>().Where(d=>!d.IsFamilyDocument&&d.PathName+"#"+d.ProjectInformation.UniqueId==design.Host.DocumentKey).ToArray();
        if(docs.Length!=1)throw new InvalidOperationException("Bound project is closed or its identity has changed. Reopen the Slab window.");
        return docs[0];
    }
    public static XYZ World(Point3 p, HostContext h) => new(Ft(h.OriginXmm+p.X),Ft(h.OriginYmm+p.Y),Ft(h.TopZmm+p.Z));
    public static Curve Native(Curve3 c, HostContext h) => c.Kind==SegmentKind.Line
        ? Line.CreateBound(World(c.Start,h),World(c.End,h))
        : Arc.Create(World(c.Start,h),World(c.End,h),World(c.Middle,h));
    static Schema Storage()
    {
        var s=Schema.Lookup(SchemaId); if(s!=null)return s;
        var b=new SchemaBuilder(SchemaId);b.SetSchemaName("P7SlabInteractiveRebarV1");
        b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField("Data",typeof(string));return b.Finish();
    }
    public static JsonObject? Metadata(Element element)
    {
        var s=Schema.Lookup(SchemaId);if(s==null)return null;
        var e=element.GetEntity(s);return e.IsValid()?JsonNode.Parse(e.Get<string>(s.GetField("Data")))?.AsObject():null;
    }
    public static List<Rebar> Owned(Document doc, SlabDocument design) => new FilteredElementCollector(doc)
        .OfClass(typeof(Rebar)).Cast<Rebar>().Where(b=>Metadata(b)?["owner"]?.ToString()==design.ModelOwnerId
            && Metadata(b)?["host_unique_id"]?.ToString()==design.Host.HostUniqueId).ToList();
    public static JsonNode? StoredDesign(Document doc,JsonObject meta)
    {
        if(meta["design"] is {} legacy)return legacy.DeepClone();
        if(meta["design_storage_id"] is not {} sid)return null;
        var record=doc.GetElement(new ElementId(sid.GetValue<long>()));
        var data=record==null?null:Metadata(record);
        return data?["owner"]?.ToString()==meta["owner"]?.ToString()
            &&data?["host_unique_id"]?.ToString()==meta["host_unique_id"]?.ToString()?data?["design"]?.DeepClone():null;
    }
    static ElementId StoreDesign(Document doc,SlabDocument design)
    {
        var records=new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .Where(e=>Metadata(e)?["owner"]?.ToString()==design.ModelOwnerId&&Metadata(e)?["kind"]?.ToString()=="design").ToList();
        if(records.Count>1)throw new InvalidOperationException("Duplicate parameter storage records exist.");
        var record=records.SingleOrDefault()??DataStorage.Create(doc);
        var clean=SlabDocument.FromJson(design.ToJson());clean.ExistingBars.Clear();
        var data=new JsonObject{["owner"]=design.ModelOwnerId,["kind"]="design",["host_unique_id"]=design.Host.HostUniqueId,["design"]=JsonNode.Parse(clean.ToJson())};
        var entity=new Entity(Storage());entity.Set(Storage().GetField("Data"),data.ToJsonString());record.SetEntity(entity);return record.Id;
    }
    static void Store(Rebar rb, SlabDocument design, BarPath3 bar,ElementId storage)
    {
        var s=Storage();var e=new Entity(s);
        var data=new JsonObject{["owner"]=design.ModelOwnerId,["host_unique_id"]=design.Host.HostUniqueId,
            ["draft"]=bar.DraftId,["member"]=bar.MemberIndex,["design_storage_id"]=storage.Value};
        e.Set(s.GetField("Data"),data.ToJsonString());rb.SetEntity(e);
    }
    sealed class RejectFailures : IFailuresPreprocessor
    {
        public readonly List<string> Messages=new();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            var fs=a.GetFailureMessages();if(fs.Count==0)return FailureProcessingResult.Continue;
            Messages.AddRange(fs.Select(f=>f.GetDescriptionText()));return FailureProcessingResult.ProceedWithRollBack;
        }
    }
    public static SlabDocument Refresh(UIApplication ui, SlabDocument input)
    {
        var design=SlabDocument.FromJson(input.ToJson());
        var doc=ResolveDocument(ui,input);
        var key=doc.PathName+"#"+doc.ProjectInformation.UniqueId;
        if(design.Host.DocumentKey!=key)throw new InvalidOperationException("Active project has changed. Reopen the Slab window.");
        var h=SlabHostReader.ReadHost(ui,design.Host.HostId,doc);
        if(h.HostUniqueId!=design.Host.HostUniqueId)throw new InvalidOperationException("Host identity has changed.");
        var excluded=Owned(doc,design).Select(b=>b.Id.Value).ToHashSet();
        design.Host=h;design.ExistingBars=WallHostReader.ReadExisting(doc,h,excluded);
        return design;
    }
    public static JsonObject Build(UIApplication ui,SlabDocument input,bool injectFailure=false)
    {
        JsonObject result;
        try { result=BuildCore(ui,input,injectFailure); }
        catch(Exception ex)
        {result=new JsonObject{["status"]="FAIL",["transaction_committed"]=false,["message"]=ex.Message,["error"]=ex.ToString()};}
        System.IO.Directory.CreateDirectory(LogDirectory);
        System.IO.File.WriteAllText(System.IO.Path.Combine(LogDirectory,"build_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff")+".json"),result.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return result;
    }
    static JsonObject BuildCore(UIApplication ui,SlabDocument input,bool injectFailure)
    {
        var doc=ResolveDocument(ui,input);
        if(doc.IsReadOnly||doc.IsFamilyDocument||doc.IsModifiable)throw new InvalidOperationException("Current project is not writable.");
        var design=Refresh(ui,input);var report=SlabGeometry.Check(design);
        if(!report.CanBuild)return new JsonObject{["status"]="BLOCKED",["transaction_committed"]=false,
            ["message"]="Adjust the red bars first.",["checks"]=JsonSerializer.SerializeToNode(report,SlabDocument.JsonOptions)};
        if(string.IsNullOrWhiteSpace(design.ModelOwnerId))throw new InvalidOperationException("Rebar group ID is missing.");
        var old=Owned(doc,design);var ids=new List<ElementId>();JsonObject? creatingBar=null;
        var protectedBefore=ProtectedSignature(doc,design);
        using var tg=new TransactionGroup(doc,"Slab Rebar · Create and Check");tg.Start();
        try
        {
            using(var tx=new Transaction(doc,"Slab Rebar · Write"))
            {
                tx.Start();var reject=new RejectFailures();tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(reject).SetClearAfterRollback(true));
                if(old.Count>0)
                {
                    var allowed=old.Select(x=>x.Id).ToHashSet();
                    // Delete also reports transient internal ids that are not database Elements.
                    // Protect every exposed instance AND type, including tags and dimensions.
                    var protectedIds=new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()
                        .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds())
                        .Where(id=>!allowed.Contains(id)).ToHashSet();
                    var deleted=doc.Delete(allowed.ToList());
                    if(deleted.Any(protectedIds.Contains)||protectedIds.Any(id=>doc.GetElement(id)==null))
                        throw new InvalidOperationException("Update would affect dependent objects and has been stopped.");
                }
                var types=new Dictionary<string,RebarBarType>();
                foreach(var type in design.Types.Where(t=>report.Bars.Any(b=>b.TypeId==t.Id)))types[type.Id]=ResolveType(doc,design,type,report.Bars);
                var host=doc.GetElement(new ElementId(design.Host.HostId));var view=EnsureView(doc);var storage=StoreDesign(doc,design);
                foreach(var b in report.Bars)
                {
                    var curves=b.Curves.Select(c=>Native(c,design.Host)).ToList();
                    var draftName=design.Groups.First(x=>x.Id==b.DraftId).Name;
                    var arcRadii=curves.OfType<Arc>().Select(a=>Mm(a.Radius)).ToArray();
                    creatingBar=new JsonObject{["draft_id"]=b.DraftId,["draft_name"]=draftName,["member_number"]=b.MemberIndex+1,
                        ["type_id"]=b.TypeId,["diameter_mm"]=b.DiameterMm,["minimum_arc_radius_mm"]=arcRadii.Length>0?arcRadii.Min():null,
                        ["type_inner_bend_diameter_mm"]=Mm(types[b.TypeId].StandardBendDiameter)};
                    if(curves.Any(c=>c.Length<doc.Application.ShortCurveTolerance))throw new InvalidOperationException("Path contains segments shorter than Revit's minimum length.");
                    var loop=CurveLoop.Create(curves);
                    var rb=Rebar.CreateFreeForm(doc,types[b.TypeId],host,new List<CurveLoop>{loop},out var validation);
                    if(rb==null||validation!=RebarFreeFormValidationResult.Success)throw new InvalidOperationException($"{draftName} · Bar {b.MemberIndex+1} creation failed: {validation}");
                    rb.SetUnobscuredInView(view,true);Store(rb,design,b,storage);ids.Add(rb.Id);
                    // Element ids are unique in the document; truncated draft names are not.
                    rb.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SUI-"+rb.Id.Value);
                    rb.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(draftName);
                }
                doc.Regenerate();
                if(injectFailure)throw new InvalidOperationException("Injected test failure: roll back after creation.");
                if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Creation transaction failed: "+string.Join("; ",reject.Messages));
            }
            var result=Verify(ui,design,true);
            if(protectedBefore!=ProtectedSignature(doc,design))throw new InvalidOperationException("Existing rebar changed. The operation was rolled back.");
            result["protected_existing_signature"]=protectedBefore;result["protected_existing_unchanged"]=true;
            if(result["status"]?.ToString()!="PASS")
            {result["rolled_back"]=tg.RollBack()==TransactionStatus.RolledBack;result["transaction_committed"]=false;return result;}
            if(tg.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("Transaction group was not committed.");
            result["transaction_committed"]=true;result["replaced_count"]=old.Count;result["created_count"]=ids.Count;
            result["message"]=$"Created {ids.Count} bars; "+(design.CheckSettings.AllowCollisionViolation?"Collision warnings only":$"Collision tolerance {design.CheckSettings.CollisionToleranceMm:0.###} mm")+
                (design.CheckSettings.AllowConcreteBoundaryViolation?".":design.CheckSettings.AllowCoverViolation?"; cover shortfalls are warnings.":"; geometry checks passed.");return result;
        }
        catch(Exception ex)
        {
            var rolled=tg.GetStatus()==TransactionStatus.RolledBack;
            if(tg.GetStatus()==TransactionStatus.Started)rolled=tg.RollBack()==TransactionStatus.RolledBack;
            return new JsonObject{["status"]="FAIL",["transaction_committed"]=false,["rolled_back"]=rolled,["message"]=ex.Message,["error"]=ex.ToString(),["creating_bar"]=creatingBar};
        }
    }
    public static string ProtectedSignature(Document doc,SlabDocument design)
    {
        var own=Owned(doc,design).Select(x=>x.Id.Value).ToHashSet();
        var rows=new List<string>();
        foreach(var b in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b=>!own.Contains(b.Id.Value)).OrderBy(b=>b.Id.Value))
        {
            var t=(RebarBarType)doc.GetElement(b.GetTypeId());
            var points=new List<string>();
            for(int position=0;position<b.NumberOfBarPositions;position++)if(b.DoesBarExistAtPosition(position))
                foreach(var c in b.GetTransformedCenterlineCurves(false,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,position))
                    foreach(var factor in new[]{0d,.25,.5,.75,1})
                    {var p=c.Evaluate(factor,true);points.Add(FormattableString.Invariant($"{position}:{p.X:R},{p.Y:R},{p.Z:R}"));}
            rows.Add(FormattableString.Invariant($"{b.Id.Value}:{b.UniqueId}:{b.GetHostId().Value}:{b.Quantity}:{t.UniqueId}:{t.BarNominalDiameter:R}:{t.BarModelDiameter:R}:{t.StandardBendDiameter:R}:{t.StandardHookBendDiameter:R}:{t.StirrupTieBendDiameter:R}:{b.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()}|")+string.Join(";",points));
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",rows))));
    }
    static RebarBarType ResolveType(Document doc,SlabDocument design,ReviewBarType type,List<BarPath3> bars)
    {
        // A type is scoped by material grade, nominal/model diameter and explicit bend geometry.
        // This is a geometric value, not a code-prescribed minimum bend diameter.
        var minimumPathInner=double.PositiveInfinity;
        foreach(var path in bars.Where(b=>b.TypeId==type.Id))foreach(var c in path.Curves.Where(c=>c.Kind==SegmentKind.Arc))
        {
            using var arc=(Arc)Native(c,design.Host);minimumPathInner=Math.Min(minimumPathInner,2*Mm(arc.Radius)-type.DiameterMm);
        }
        var innerBend=BendConstraint.Resolve(type.DiameterMm,minimumPathInner);
        var name=FormattableString.Invariant($"SUI-{type.Grade}-D{type.DiameterMm:0.###}-B{innerBend:0.###}");
        var existing=new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().FirstOrDefault(t=>t.Name==name);
        if(existing!=null)
        {
            if(!BendConstraint.Matches(Mm(existing.BarNominalDiameter),type.DiameterMm)
                ||!BendConstraint.Matches(Mm(existing.BarModelDiameter),type.DiameterMm)
                ||!BendConstraint.Matches(Mm(existing.StandardBendDiameter),innerBend)
                ||!BendConstraint.Matches(Mm(existing.StandardHookBendDiameter),innerBend)
                ||!BendConstraint.Matches(Mm(existing.StirrupTieBendDiameter),innerBend))
                throw new InvalidOperationException("Rebar type with the same name does not match the input dimensions.");
            return existing;
        }
        var bt=RebarBarType.Create(doc);bt.Name=name;
        bt.StandardBendDiameter=Ft(innerBend);bt.StandardHookBendDiameter=Ft(innerBend);bt.StirrupTieBendDiameter=Ft(innerBend);
        bt.BarNominalDiameter=Ft(type.DiameterMm);bt.BarModelDiameter=Ft(type.DiameterMm);
        bt.DeformationType=type.Grade=="HPB300"?RebarDeformationType.Plain:RebarDeformationType.Deformed;return bt;
    }
    public static JsonObject Verify(UIApplication ui,SlabDocument source,bool solidChecks=true)
    {
        var doc=ResolveDocument(ui,source);var design=Refresh(ui,source);var planned=SlabGeometry.Check(design);
        var bars=Owned(doc,design);var errors=new List<string>();var rows=new JsonArray();var actualOwn=new List<ExistingBarPath>();
        double maxPath=0,maxLength=0;
        if(!planned.CanBuild)errors.Add("Creation parameters no longer pass geometry checks.");
        if(bars.Count!=planned.Bars.Count)errors.Add("Native rebar count does not match.");
        var visited=new HashSet<string>();
        bool enforceBoundary=!design.CheckSettings.AllowConcreteBoundaryViolation;
        var hostSolids=enforceBoundary?WallHostReader.Solids(doc.GetElement(new ElementId(design.Host.HostId))):new List<Solid>();
        foreach(var rb in bars)
        {
            var meta=Metadata(rb)!;string draft=meta["draft"]!.ToString();int member=meta["member"]!.GetValue<int>();
            var key=draft+"/"+member;
            var expected=planned.Bars.FirstOrDefault(b=>b.DraftId==draft&&b.MemberIndex==member);
            if(expected==null||!visited.Add(key)){errors.Add("Rebar path IDs do not match.");continue;}
            var bt=(RebarBarType)doc.GetElement(rb.GetTypeId());var diameter=Mm(bt.BarModelDiameter);
            var actual=rb.GetTransformedCenterlineCurves(false,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,0).ToArray();
            var target=expected.Curves.Select(c=>Native(c,design.Host)).ToArray();
            double le=Math.Abs(Mm(actual.Sum(c=>c.Length)-target.Sum(c=>c.Length))),err=0;
            foreach(var c in target)foreach(var p in Samples(c))err=Math.Max(err,Mm(actual.Min(a=>a.Distance(p))));
            foreach(var c in actual)foreach(var p in Samples(c))err=Math.Max(err,Mm(target.Min(a=>a.Distance(p))));
            maxPath=Math.Max(maxPath,err);maxLength=Math.Max(maxLength,le);
            if(rb.GetHostId().Value!=design.Host.HostId||rb.Quantity!=1||rb.NumberOfBarPositions!=1)errors.Add($"Host or count of rebar {rb.Id} does not match.");
            if(Math.Abs(diameter-expected.DiameterMm)>.001||Math.Abs(Mm(bt.BarNominalDiameter)-expected.DiameterMm)>.001)errors.Add($"Diameter of rebar {rb.Id} does not match.");
            if(err>.001||le>.001)errors.Add($"Path or length of rebar {rb.Id} does not match.");
            bool allowTop=design.Groups.First(d=>d.Id==draft).AllowTopExtension;
            actualOwn.Add(new ExistingBarPath{ElementId=rb.Id.Value,PositionIndex=0,DiameterMm=diameter,AllowTopExtension=allowTop,
                Curves=actual.SelectMany(c=>WallHostReader.ToLocalCurves(c,design.Host)).ToList()});
            if(enforceBoundary)foreach(var c in actual)
            {
                // Explicit starter bars are independently checked from native curves below,
                // including side/bottom boundaries and the upper-exit policy. They need not be wholly contained.
                if(allowTop)continue;
                Curve portion=c;
                if(allowTop&&c is Line)
                {
                    var a=c.GetEndPoint(0);var b=c.GetEndPoint(1);double top=Ft(design.Host.TopZmm);
                    if(a.Z>=top&&b.Z>=top)continue;
                    if(a.Z>top||b.Z>top){var p=a+(b-a)*((top-a.Z)/(b.Z-a.Z));portion=Line.CreateBound(a.Z>top?p:a,b.Z>top?p:b);}
                }
                double inside=0;foreach(var s in hostSolids)using(var x=s.IntersectWithCurve(portion,new SolidCurveIntersectionOptions()))for(int n=0;n<x.SegmentCount;n++)inside+=x.GetCurveSegment(n).Length;
                if(inside<portion.Length-Ft(.01))errors.Add($"Actual centerline of rebar {rb.Id} extends beyond the host.");
                if(!ReferenceEquals(portion,c))portion.Dispose();
            }
            rows.Add(new JsonObject{["id"]=rb.Id.Value,["unique_id"]=rb.UniqueId,["draft"]=draft,["member"]=member,["host_id"]=rb.GetHostId().Value,
                ["diameter_mm"]=diameter,["length_mm"]=Mm(actual.Sum(c=>c.Length)),["path_error_mm"]=err,["length_error_mm"]=le});
            foreach(var c in target)c.Dispose();
        }
        // Independently read actual native paths, checking them against each other and the scene.
        var ownIds=bars.Select(b=>b.Id.Value).ToHashSet();var actualScene=WallHostReader.ReadExisting(doc,design.Host,ownIds).Concat(actualOwn).ToList();
        var allowById=bars.ToDictionary(b=>b.Id.Value,b=>design.Groups.First(d=>d.Id==Metadata(b)!["draft"]!.ToString()).AllowTopExtension);
        foreach(var path in actualScene)path.AllowTopExtension=allowById.GetValueOrDefault(path.ElementId);
        if(!design.Host.ExistingScanComplete)errors.Add("Actual rebar path reading is incomplete.");
        var actualReport=SlabGeometry.CheckActual(design,actualScene.Where(b=>ownIds.Contains(b.ElementId)).ToList(),actualScene.Where(b=>!ownIds.Contains(b.ElementId)).ToList());
        if(!actualReport.CanBuild)errors.AddRange(actualReport.Issues.Select(i=>i.Message));
        int candidates=0,clashes=0,unknown=0,separated=0,accepted=0;double maxVolume=0;
        var acceptedSolids=new JsonArray();
        if(solidChecks)
        {
            var all=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToArray();
            var cache=new Dictionary<long,List<Solid>>();var view=FindView(doc);
            var boxes=all.ToDictionary(b=>b.Id.Value,b=>b.get_BoundingBox(null));
            var paths=actualScene.GroupBy(b=>b.ElementId).ToDictionary(g=>g.Key,g=>g.ToArray());
            for(int i=0;i<all.Length;i++)for(int j=i+1;j<all.Length;j++)
            {
                var a=all[i];var b=all[j];if(!ownIds.Contains(a.Id.Value)&&!ownIds.Contains(b.Id.Value))continue;
                var ba=boxes[a.Id.Value];var bb=boxes[b.Id.Value];if(ba==null||bb==null){unknown++;continue;}
                if(!Overlap(ba,bb))continue;candidates++;
                try
                {
                    // Analytic lower-bound separation rejects distant tubes before expensive native Boolean operations.
                    double certifiedGap=double.NegativeInfinity;
                    if(paths.TryGetValue(a.Id.Value,out var pa)&&paths.TryGetValue(b.Id.Value,out var pb))
                        certifiedGap=pa.Min(x=>pb.Min(y=>SlabGeometry.CertifiedClearanceMm(x,y)));
                    if(certifiedGap>0){separated++;continue;}
                    if(!cache.ContainsKey(a.Id.Value))cache[a.Id.Value]=BarSolids(a,view);
                    if(!cache.ContainsKey(b.Id.Value))cache[b.Id.Value]=BarSolids(b,view);
                    if(cache[a.Id.Value].Count==0||cache[b.Id.Value].Count==0){unknown++;continue;}
                    double volume=0;foreach(var sa in cache[a.Id.Value])foreach(var sb in cache[b.Id.Value])
                    {using var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(sa,sb,BooleanOperationsType.Intersect);volume+=intersection.Volume*Math.Pow(304.8,3);}
                    maxVolume=Math.Max(maxVolume,volume);
                    if(volume>1)
                    {
                        // The authorized tolerance is a linear penetration depth. Intersection
                        // volume cannot establish that depth. Native paths and actual diameters
                        // supply a conservative lower bound for surface separation instead.
                        if(design.CheckSettings.AllowCollisionViolation||(design.CheckSettings.CollisionToleranceMm>0&&double.IsFinite(certifiedGap)
                            &&SlabGeometry.AcceptsCollision(certifiedGap,design.CheckSettings.CollisionToleranceMm)))
                        {
                            accepted++;
                            acceptedSolids.Add(new JsonObject{["first_id"]=a.Id.Value,["second_id"]=b.Id.Value,
                                ["penetration_upper_bound_mm"]=double.IsFinite(certifiedGap)?Math.Max(0,-certifiedGap):null,["intersection_mm3"]=volume,
                                ["acceptance_basis"]=design.CheckSettings.AllowCollisionViolation?"user_warning_only_policy":"within_linear_tolerance"});
                        }
                        else {clashes++;errors.Add($"Actual rebar {a.Id} / {b.Id} solids intersect beyond the confirmed tolerance, or penetration depth could not be verified.");}
                    }
                }
                catch{unknown++;}
            }
            if(unknown>0&&!design.CheckSettings.AllowCollisionViolation)errors.Add($"Solid checks for {unknown} actual rebar pairs are incomplete.");
        }
        return new JsonObject{["status"]=errors.Count==0?"PASS":"FAIL",["message"]=errors.Count==0?"Native rebar readback passed.":string.Join("; ",errors.Distinct()),
            ["native_count"]=bars.Count,["bars"]=rows,["max_path_error_mm"]=maxPath,["max_length_error_mm"]=maxLength,
            ["actual_geometry_checks"]=JsonSerializer.SerializeToNode(actualReport,SlabDocument.JsonOptions),["solid_candidates"]=candidates,
            ["solid_clashes"]=clashes,["solid_unknown"]=unknown,["solid_broadphase_clear_pairs"]=separated,["solid_boolean_pairs"]=candidates-separated,["maximum_intersection_mm3"]=maxVolume,["collision_volume_tolerance_mm3"]=1,
            ["collision_tolerance_mm"]=design.CheckSettings.CollisionToleranceMm,["solid_accepted_overlap_pairs"]=accepted,["accepted_solid_overlaps"]=acceptedSolids,
            ["allow_collision_violation"]=design.CheckSettings.AllowCollisionViolation,["solid_collision_check_complete"]=unknown==0,["solid_intersection_pairs"]=accepted+clashes,
            ["linear_collision_quantification_incomplete_count"]=actualReport.AcceptedCollisions.Count(i=>i.Kind==ValidationKind.Incomplete),
            ["allow_cover_violation"]=design.CheckSettings.AllowCoverViolation,["accepted_cover_violation_count"]=actualReport.AcceptedCoverViolations.Count,
            ["allow_concrete_boundary_violation"]=design.CheckSettings.AllowConcreteBoundaryViolation,
            ["cover_checks_enforced"]=enforceBoundary&&!design.CheckSettings.AllowCoverViolation,["concrete_boundary_checks_enforced"]=enforceBoundary,
            ["owner"]=design.ModelOwnerId,["document_path"]=doc.PathName,["scope"]=design.CheckSettings.AllowConcreteBoundaryViolation?
                "Native host, type, count, centreline and length readback remain checked. Cover and concrete-boundary checks are disabled. "+
                (design.CheckSettings.AllowCollisionViolation?"Collision results remain warnings.":"The specified collision tolerance remains enforced.")+" This does not establish concrete containment or code compliance.":design.CheckSettings.AllowCollisionViolation?
                "User allows rebar collisions at any depth. Results are warnings and incomplete checks remain marked incomplete. Cover follows the selected policy; actual curves and concrete solid boundaries are still checked. This does not establish collision-free geometry or code compliance.":design.CheckSettings.AllowCoverViolation?
                "User allows intrusion into reference cover. Bar outer surfaces are still checked against the concrete boundary and the specified collision tolerance. This does not establish cover or full code compliance.":
                "Geometric collisions and specified clear cover; excludes structural capacity and full code review."};
    }
    static IEnumerable<XYZ> Samples(Curve c){int n=Math.Max(2,(int)Math.Ceiling(Mm(c.Length)/20));for(int i=0;i<=n;i++)yield return c.Evaluate((double)i/n,true);}
    static bool Overlap(BoundingBoxXYZ a,BoundingBoxXYZ b)=>a.Min.X<=b.Max.X&&a.Max.X>=b.Min.X&&a.Min.Y<=b.Max.Y&&a.Max.Y>=b.Min.Y&&a.Min.Z<=b.Max.Z&&a.Max.Z>=b.Min.Z;
    static List<Solid> BarSolids(Rebar b,View3D? view)
    {
        var result=new List<Solid>();void Walk(GeometryElement g){foreach(var o in g){if(o is Solid s&&s.Volume>1e-12)result.Add(s);else if(o is GeometryInstance i)Walk(i.GetInstanceGeometry());}}
        var geo=view!=null?b.GetFullGeometryForView(view):b.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine});if(geo!=null)Walk(geo);return result;
    }
    static View3D? FindView(Document d)=>new FilteredElementCollector(d).OfClass(typeof(View3D)).Cast<View3D>()
        .Where(v=>!v.IsTemplate&&(v.Name==ViewName||v.Name==LegacyViewName)).OrderBy(v=>v.Name==ViewName?0:1).FirstOrDefault();
    static View3D EnsureView(Document d)
    {
        var v=FindView(d);if(v!=null){if(v.Name==LegacyViewName)v.Name=ViewName;return v;}
        var type=new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);
        v=View3D.CreateIsometric(d,type.Id);v.Name=ViewName;v.DetailLevel=ViewDetailLevel.Fine;v.DisplayStyle=DisplayStyle.FlatColors;return v;
    }
    public static void Show(UIApplication ui,SlabDocument design)
    {
        var d=ResolveDocument(ui,design);var v=FindView(d);if(v==null)return;
        using(var t=new Transaction(d,"Slab Reinforcement View"))
        {
            t.Start();if(v.Name==LegacyViewName)v.Name=ViewName;v.SetElementOverrides(new ElementId(design.Host.HostId),new OverrideGraphicSettings().SetSurfaceTransparency(92));
            // A dedicated inspection view isolates the slab; source wall elements remain untouched.
            var wallIds=new FilteredElementCollector(d).WhereElementIsNotElementType()
                .Where(e=>e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()=="CJ-WALL").Select(e=>e.Id).ToHashSet();
            var hide=wallIds.Concat(new FilteredElementCollector(d).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b=>wallIds.Contains(b.GetHostId())).Select(b=>b.Id))
                .Where(id=>d.GetElement(id).CanBeHidden(v)&&!d.GetElement(id).IsHidden(v)).ToList();
            if(hide.Count>0)v.HideElements(hide);
            var host=d.GetElement(new ElementId(design.Host.HostId));var box=host.get_BoundingBox(null);
            if(box!=null){double pad=Ft(100);v.SetSectionBox(new BoundingBoxXYZ{Min=box.Min-new XYZ(pad,pad,pad),Max=box.Max+new XYZ(pad,pad,pad)});v.IsSectionBoxActive=true;}
            t.Commit();
        }
        var uidoc=ui.OpenAndActivateDocument(d.PathName);uidoc.ActiveView=v;uidoc.Selection.SetElementIds(Owned(d,design).Select(b=>b.Id).ToList());uidoc.RefreshActiveView();
        uidoc.GetOpenUIViews().FirstOrDefault(w=>w.ViewId==v.Id)?.ZoomToFit();
    }
}
