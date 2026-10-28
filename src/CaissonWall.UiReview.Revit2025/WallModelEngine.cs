using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaissonWallUi;

// This namespace owns only elements carrying this schema AND the persistent model owner id.
public static class WallModelEngine
{
    public static readonly Guid SchemaId = new("2f9b17e2-3cc0-4a1b-8e76-6da65b00d911");
    public const string ViewName = "Wall Reinforcement";
    const string LegacyViewName = "井壁钢筋_交互核查";
    static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    static double Mm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    public static Document ResolveDocument(UIApplication ui,ReviewDocument design)
    {
        var docs=ui.Application.Documents.Cast<Document>().Where(d=>!d.IsFamilyDocument&&d.PathName+"#"+d.ProjectInformation.UniqueId==design.Host.DocumentKey).ToArray();
        if(docs.Length!=1)throw new InvalidOperationException("Bound project is closed or its identity has changed. Reopen the Wall window.");
        return docs[0];
    }
    public static XYZ World(Point3 p, HostContext h) => new(Ft(h.OriginXmm+p.X),Ft(h.OriginYmm+p.Y),Ft(h.TopZmm+p.Z));
    public static Curve Native(Curve3 c, HostContext h) => c.Kind==SegmentKind.Line
        ? Line.CreateBound(World(c.Start,h),World(c.End,h))
        : Arc.Create(World(c.Start,h),World(c.End,h),World(c.Middle,h));
    static Schema Storage()
    {
        var s=Schema.Lookup(SchemaId); if(s!=null)return s;
        var b=new SchemaBuilder(SchemaId);b.SetSchemaName("P7WallInteractiveRebarV1");
        b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField("Data",typeof(string));return b.Finish();
    }
    public static JsonObject? Metadata(Element element)
    {
        var s=Schema.Lookup(SchemaId);if(s==null)return null;
        var e=element.GetEntity(s);return e.IsValid()?JsonNode.Parse(e.Get<string>(s.GetField("Data")))?.AsObject():null;
    }
    public static List<Rebar> Owned(Document doc, ReviewDocument design) => new FilteredElementCollector(doc)
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
    static ElementId StoreDesign(Document doc,ReviewDocument design)
    {
        var records=new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .Where(e=>Metadata(e)?["owner"]?.ToString()==design.ModelOwnerId&&Metadata(e)?["kind"]?.ToString()=="design").ToList();
        if(records.Count>1)throw new InvalidOperationException("Duplicate parameter storage records exist.");
        var record=records.SingleOrDefault()??DataStorage.Create(doc);
        var clean=ReviewDocument.FromJson(design.ToJson());clean.ExistingBars.Clear();
        var data=new JsonObject{["owner"]=design.ModelOwnerId,["kind"]="design",["host_unique_id"]=design.Host.HostUniqueId,["design"]=JsonNode.Parse(clean.ToJson())};
        var entity=new Entity(Storage());entity.Set(Storage().GetField("Data"),data.ToJsonString());record.SetEntity(entity);return record.Id;
    }
    static void Store(Rebar rb, ReviewDocument design, BarPath3 bar,ElementId storage)
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
    public static ReviewDocument Refresh(UIApplication ui, ReviewDocument input)
    {
        var design=ReviewDocument.FromJson(input.ToJson());
        var doc=ResolveDocument(ui,input);
        var key=doc.PathName+"#"+doc.ProjectInformation.UniqueId;
        if(design.Host.DocumentKey!=key)throw new InvalidOperationException("Active project has changed. Reopen the Wall window.");
        var h=WallHostReader.ReadHost(ui,design.Host.HostId,doc);
        if(h.HostUniqueId!=design.Host.HostUniqueId)throw new InvalidOperationException("Host identity has changed.");
        var excluded=Owned(doc,design).Select(b=>b.Id.Value).ToHashSet();
        excluded.UnionWith(ImportedSourceIds(doc,design));
        design.Host=h;design.ExistingBars=WallHostReader.ReadExisting(doc,h,excluded);
        return design;
    }
    public static HashSet<long> ImportedSourceIds(Document doc,ReviewDocument design)
    {
        var ids=new HashSet<long>();
        if(design.ImportSourceDocumentKey!=doc.PathName+"#"+doc.ProjectInformation.UniqueId)return ids;
        foreach(var d in design.Drafts)
        {
            if(d.SourceElementIds.Count!=d.SourceUniqueIds.Count)throw new InvalidOperationException("Import source identities are incomplete.");
            for(int i=0;i<d.SourceElementIds.Count;i++)
            {
                var element=doc.GetElement(new ElementId(d.SourceElementIds[i]));
                if(element is not Rebar b||b.UniqueId!=d.SourceUniqueIds[i]||b.GetHostId().Value!=design.Host.HostId)
                    throw new InvalidOperationException("Imported source rebar has changed. Reload it.");
                ids.Add(b.Id.Value);
            }
        }
        return ids;
    }
    public static JsonObject Build(UIApplication ui,ReviewDocument input,bool injectFailure=false)
    {
        JsonObject result;
        try { result=BuildCore(ui,input,injectFailure); }
        catch(Exception ex)
        {result=new JsonObject{["status"]="FAIL",["transaction_committed"]=false,["message"]=ex.Message,["error"]=ex.ToString()};}
        WallBuildLog.Save(input,result);
        return result;
    }
    static JsonObject BuildCore(UIApplication ui,ReviewDocument input,bool injectFailure)
    {
        var doc=ResolveDocument(ui,input);
        if(doc.IsReadOnly||doc.IsFamilyDocument||doc.IsModifiable)throw new InvalidOperationException("Current project is not writable.");
        var design=Refresh(ui,input);var report=WallValidation.Check(design);
        if(ImportedSourceIds(doc,design).Except(Owned(doc,design).Select(b=>b.Id.Value)).Any())
            return new JsonObject{["status"]="BLOCKED",["transaction_committed"]=false,["message"]="Existing rebar has been loaded. Create rebar in a model copy without duplicate bars."};
        if(!report.CanBuild)return new JsonObject{["status"]="BLOCKED",["transaction_committed"]=false,
            ["message"]="Adjust the red bars first.",["checks"]=JsonSerializer.SerializeToNode(report,ReviewDocument.JsonOptions)};
        if(string.IsNullOrWhiteSpace(design.ModelOwnerId))throw new InvalidOperationException("Rebar group ID is missing.");
        var old=Owned(doc,design);var ids=new List<ElementId>();JsonObject? creatingBar=null;
        using var tg=new TransactionGroup(doc,"Wall Rebar · Create and Check");tg.Start();
        try
        {
            using(var tx=new Transaction(doc,"Wall Rebar · Write"))
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
                    var draftName=design.Drafts.First(x=>x.Id==b.DraftId).Name;
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
                    rb.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("WUI-"+rb.Id.Value);
                }
                doc.Regenerate();
                if(injectFailure)throw new InvalidOperationException("Injected test failure: roll back after creation.");
                if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Creation transaction failed: "+string.Join("; ",reject.Messages));
            }
            var result=Verify(ui,design,true);
            if(result["status"]?.ToString()!="PASS")
            {result["rolled_back"]=tg.RollBack()==TransactionStatus.RolledBack;result["transaction_committed"]=false;return result;}
            if(tg.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("Transaction group was not committed.");
            result["transaction_committed"]=true;result["replaced_count"]=old.Count;result["created_count"]=ids.Count;
            int warnings=result["visible_warning_count"]?.GetValue<int>()??0;
            result["message"]=$"Created {ids.Count} bars; native path readback matches."+(warnings>0?$" {warnings} warnings need review.":"");return result;
        }
        catch(Exception ex)
        {
            var rolled=tg.GetStatus()==TransactionStatus.RolledBack;
            if(tg.GetStatus()==TransactionStatus.Started)rolled=tg.RollBack()==TransactionStatus.RolledBack;
            return new JsonObject{["status"]="FAIL",["transaction_committed"]=false,["rolled_back"]=rolled,["message"]=ex.Message,["error"]=ex.ToString(),["creating_bar"]=creatingBar};
        }
    }
    static RebarBarType ResolveType(Document doc,ReviewDocument design,ReviewBarType type,List<BarPath3> bars)
    {
        // A type is scoped by material grade, nominal/model diameter and explicit bend geometry.
        // This is a geometric value, not a code-prescribed minimum bend diameter.
        var minimumPathInner=double.PositiveInfinity;
        foreach(var path in bars.Where(b=>b.TypeId==type.Id))foreach(var c in path.Curves.Where(c=>c.Kind==SegmentKind.Arc))
        {
            using var arc=(Arc)Native(c,design.Host);minimumPathInner=Math.Min(minimumPathInner,2*Mm(arc.Radius)-type.DiameterMm);
        }
        var innerBend=BendConstraint.Resolve(type.DiameterMm,minimumPathInner);
        var name=FormattableString.Invariant($"WUI-{type.Grade}-D{type.DiameterMm:0.###}-B{innerBend:0.###}");
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
    public static JsonObject Verify(UIApplication ui,ReviewDocument source,bool solidChecks=true)
    {
        var doc=ResolveDocument(ui,source);var design=Refresh(ui,source);var planned=WallValidation.Check(design);
        var bars=Owned(doc,design);var errors=new List<string>();var rows=new JsonArray();var openingWarnings=new JsonArray();
        double maxPath=0,maxLength=0;
        if(!planned.CanBuild)errors.Add("Creation parameters no longer pass geometry checks.");
        if(bars.Count!=planned.Bars.Count)errors.Add("Native rebar count does not match.");
        var visited=new HashSet<string>();
        var hostSolids=WallHostReader.Solids(doc.GetElement(new ElementId(design.Host.HostId)));
        var openingSolids=new List<Solid>();
        if(design.Host.HostOpenings.Count>0)
        {
            if(!design.Host.OpeningGeometryVerified)errors.Add("Opening solids have not been verified. Host voids cannot be accepted.");
            else foreach(var opening in design.Host.HostOpenings)openingSolids.Add(OpeningSolid(opening,design.Host));
        }
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
            if(err>.1||le>.1)errors.Add($"Path or length of rebar {rb.Id} does not match.");
            bool allowTop=design.Drafts.First(d=>d.Id==draft).AllowTopExtension;
            double openingGapMm=0,unexplainedGapMm=0;
            foreach(var c in actual)
            {
                Curve portion=c;
                if(allowTop&&c is Line)
                {
                    var a=c.GetEndPoint(0);var b=c.GetEndPoint(1);double top=Ft(design.Host.TopZmm);
                    if(a.Z>=top&&b.Z>=top)continue;
                    if(a.Z>top||b.Z>top){var p=a+(b-a)*((top-a.Z)/(b.Z-a.Z));portion=Line.CreateBound(a.Z>top?p:a,b.Z>top?p:b);}
                }
                // Find the actual complement of concrete first, then subtract only geometrically certified finite voids.
                // Summing intersection lengths cannot prove coverage when solids/voids overlap.
                var missing=OutsideSolids(portion,hostSolids);
                foreach(var gap in missing)
                {
                    var residual=OutsideSolids(gap,openingSolids);
                    double unexplained=residual.Sum(x=>x.Length);unexplainedGapMm+=Mm(unexplained);
                    openingGapMm+=Math.Max(0,Mm(gap.Length-unexplained));
                    foreach(var segment in residual)segment.Dispose();gap.Dispose();
                }
                if(!ReferenceEquals(portion,c))portion.Dispose();
            }
            if(unexplainedGapMm>.001)errors.Add($"Rebar {rb.Id} has {unexplainedGapMm:0.###} mm of actual centerline outside the host without an explained cause. It cannot be ignored as an opening.");
            if(openingGapMm>.001)openingWarnings.Add(new JsonObject{["id"]=rb.Id.Value,["draft"]=draft,["member"]=member,
                ["known_opening_gap_mm"]=openingGapMm,["unexplained_gap_mm"]=unexplainedGapMm,
                ["message"]=$"Pending · Rebar {rb.Id} has {openingGapMm:0.###} mm of centerline inside verified openings. Shape retained."});
            rows.Add(new JsonObject{["id"]=rb.Id.Value,["unique_id"]=rb.UniqueId,["draft"]=draft,["member"]=member,["host_id"]=rb.GetHostId().Value,
                ["diameter_mm"]=diameter,["length_mm"]=Mm(actual.Sum(c=>c.Length)),["path_error_mm"]=err,["length_error_mm"]=le,
                ["known_opening_gap_mm"]=openingGapMm,["unexplained_gap_mm"]=unexplainedGapMm});
            foreach(var c in target)c.Dispose();
            foreach(var c in actual)c.Dispose();
        }
        // Independently read actual native paths, checking them against each other and the scene.
        var ownIds=bars.Select(b=>b.Id.Value).ToHashSet();var actualScene=WallHostReader.ReadExisting(doc,design.Host);
        var allowById=bars.ToDictionary(b=>b.Id.Value,b=>design.Drafts.First(d=>d.Id==Metadata(b)!["draft"]!.ToString()).AllowTopExtension);
        foreach(var path in actualScene)path.AllowTopExtension=allowById.GetValueOrDefault(path.ElementId);
        if(!design.Host.ExistingScanComplete)errors.Add("Actual rebar path reading is incomplete.");
        var actualReport=WallValidation.CheckActual(design,actualScene.Where(b=>ownIds.Contains(b.ElementId)).ToList(),actualScene.Where(b=>!ownIds.Contains(b.ElementId)).ToList());
        if(!actualReport.CanBuild)errors.AddRange(actualReport.Issues.Select(i=>i.Message));
        int candidates=0,clashes=0,unknown=0,separated=0,acceptedSolidClashes=0;double maxVolume=0;
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
                    if(paths.TryGetValue(a.Id.Value,out var pa)&&paths.TryGetValue(b.Id.Value,out var pb)
                        &&pa.All(x=>pb.All(y=>WallValidation.CertifiedSeparationMm(x,y)>0))){separated++;continue;}
                    if(!cache.ContainsKey(a.Id.Value))cache[a.Id.Value]=BarSolids(a,view);
                    if(!cache.ContainsKey(b.Id.Value))cache[b.Id.Value]=BarSolids(b,view);
                    if(cache[a.Id.Value].Count==0||cache[b.Id.Value].Count==0){unknown++;continue;}
                    double volume=0;foreach(var sa in cache[a.Id.Value])foreach(var sb in cache[b.Id.Value])
                    {using var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(sa,sb,BooleanOperationsType.Intersect);volume+=intersection.Volume*Math.Pow(304.8,3);}
                    maxVolume=Math.Max(maxVolume,volume);if(volume>1)
                    {
                        clashes++;
                        if(design.CheckSettings.AllowCollisionViolation)
                        {
                            acceptedSolidClashes++;var primary=ownIds.Contains(a.Id.Value)?a:b;var secondary=primary.Id==a.Id?b:a;var metadata=Metadata(primary)!;
                            actualReport.AcceptedCollisions.Add(new(){DraftId=metadata["draft"]!.ToString(),MemberIndex=metadata["member"]!.GetValue<int>(),ExistingElementId=secondary.Id.Value,
                                Kind=ValidationKind.Collision,Message=$"Collision warning only · Actual rebar {a.Id} / {b.Id} solids intersect by {volume:0.###} mm³."});
                        }
                        else errors.Add($"Actual rebar {a.Id} / {b.Id} solids intersect.");
                    }
                }
                catch{unknown++;}
            }
            if(unknown>0)
            {
                if(design.CheckSettings.AllowCollisionViolation)actualReport.AcceptedCollisions.Add(new(){Kind=ValidationKind.Incomplete,Message=$"Collision warning only · Solid intersections for {unknown} actual rebar pairs remain unquantified."});
                else errors.Add($"Solid checks for {unknown} actual rebar pairs are incomplete.");
            }
        }
        foreach(var solid in openingSolids)solid.Dispose();
        int visibleWarnings=actualReport.AcceptedHostOpenings.Count+actualReport.AcceptedCollisions.Count+actualReport.AcceptedCoverViolations.Count;
        return new JsonObject{["status"]=errors.Count==0?"PASS":"FAIL",["message"]=errors.Count==0?(visibleWarnings>0?$"Native path readback matches; {visibleWarnings} warnings need review.":"Native path readback matches."):string.Join("; ",errors.Distinct()),
            ["native_count"]=bars.Count,["bars"]=rows,["max_path_error_mm"]=maxPath,["max_length_error_mm"]=maxLength,
            ["actual_geometry_checks"]=JsonSerializer.SerializeToNode(actualReport,ReviewDocument.JsonOptions),["solid_candidates"]=candidates,
            ["solid_clashes"]=clashes,["solid_unknown"]=unknown,["solid_broadphase_clear_pairs"]=separated,["solid_boolean_pairs"]=candidates-separated,["maximum_intersection_mm3"]=maxVolume,["collision_volume_tolerance_mm3"]=1,
            ["accepted_solid_collision_count"]=acceptedSolidClashes,["solid_collision_check_complete"]=unknown==0,["allow_collision_violation"]=design.CheckSettings.AllowCollisionViolation,
            ["allow_cover_violation"]=design.CheckSettings.AllowCoverViolation,["accepted_cover_violation_count"]=actualReport.AcceptedCoverViolations.Count,
            ["opening_geometry_verified"]=design.Host.OpeningGeometryVerified,["host_opening_warning_count"]=actualReport.AcceptedHostOpenings.Count,
            ["host_opening_native_gap_count"]=openingWarnings.Count,["host_opening_native_gaps"]=openingWarnings,["visible_warning_count"]=visibleWarnings,["code_compliance_verified"]=false,
            ["owner"]=design.ModelOwnerId,["document_path"]=doc.PathName,["scope"]="Actual paths, main body boundaries, and verified openings; cover and collision results follow explicit policies. This does not establish collision-free geometry, structural capacity, or full code compliance."};
    }
    static Solid OpeningSolid(HostOpening opening,HostContext host)
    {
        var start=World(opening.AxisStart,host);var end=World(opening.AxisEnd,host);var vector=end-start;double length=vector.GetLength();
        if(!double.IsFinite(length)||length<=Ft(.001)||!double.IsFinite(opening.RadiusMm)||opening.RadiusMm<=0)throw new InvalidOperationException("Loaded opening dimensions are invalid.");
        var axis=vector.Normalize();var x=axis.CrossProduct(Math.Abs(axis.Z)<.9?XYZ.BasisZ:XYZ.BasisY).Normalize();var y=axis.CrossProduct(x).Normalize();
        using var a=Arc.Create(start,Ft(opening.RadiusMm),0,Math.PI,x,y);using var b=Arc.Create(start,Ft(opening.RadiusMm),Math.PI,2*Math.PI,x,y);
        using var loop=CurveLoop.Create(new List<Curve>{a,b});return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop>{loop},axis,length);
    }
    static List<Curve> OutsideSolids(Curve source,IEnumerable<Solid> solids)
    {
        var pieces=new List<Curve>{source.CreateTransformed(Transform.Identity)};
        try
        {
            foreach(var solid in solids)
            {
                var next=new List<Curve>();
                using var options=new SolidCurveIntersectionOptions{ResultType=SolidCurveIntersectionMode.CurveSegmentsOutside};
                try
                {
                    foreach(var piece in pieces)
                    {
                        using var result=solid.IntersectWithCurve(piece,options);
                        for(int i=0;i<result.SegmentCount;i++)next.Add(result.GetCurveSegment(i));
                    }
                }
                catch {foreach(var part in next)part.Dispose();throw;}
                foreach(var piece in pieces)piece.Dispose();pieces=next;
            }
            return pieces;
        }
        catch {foreach(var piece in pieces)piece.Dispose();throw;}
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
    public static void Show(UIApplication ui,ReviewDocument design)
    {
        var d=ResolveDocument(ui,design);var v=FindView(d);if(v==null)return;
        using(var t=new Transaction(d,"Wall Reinforcement View")){t.Start();if(v.Name==LegacyViewName)v.Name=ViewName;v.SetElementOverrides(new ElementId(design.Host.HostId),new OverrideGraphicSettings().SetSurfaceTransparency(85));t.Commit();}
        var uidoc=ui.OpenAndActivateDocument(d.PathName);uidoc.ActiveView=v;uidoc.Selection.SetElementIds(Owned(d,design).Select(b=>b.Id).ToList());uidoc.RefreshActiveView();
        uidoc.GetOpenUIViews().FirstOrDefault(w=>w.ViewId==v.Id)?.ZoomToFit();
    }
}
