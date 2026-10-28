using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace CaissonWallUi;

public static class SlabHostReader
{
    public static HostContext ReadHost(UIApplication ui,long? hostId=null,Document? document=null)
    {
        var doc=document??ui.ActiveUIDocument?.Document??throw new InvalidOperationException("Open a project.");
        var matches=new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e=>e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() is "CJ-SLAB" or "CJ-BASE").ToArray();
        var host=hostId.HasValue?doc.GetElement(new ElementId(hostId.Value)):matches.Length==1?matches[0]:null;
        if(host==null||!RebarHostData.IsValidHost(host))throw new InvalidOperationException("Select one valid rebar host slab marked CJ-SLAB.");
        var solids=WallHostReader.Solids(host);
        if(solids.Count!=1)throw new InvalidOperationException("Only a single solid-of-revolution slab is supported.");
        var box=host.get_BoundingBox(null)??throw new InvalidOperationException("Slab has no solid boundary.");
        var pts=(from x in new[]{box.Min.X,box.Max.X} from y in new[]{box.Min.Y,box.Max.Y} from z in new[]{box.Min.Z,box.Max.Z} select box.Transform.OfPoint(new XYZ(x,y,z))).ToArray();
        var h=new HostContext{DocumentTitle=doc.Title,DocumentKey=doc.PathName+"#"+doc.ProjectInformation.UniqueId,
            HostId=host.Id.Value,HostUniqueId=host.UniqueId,HostName=host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()??"CJ-SLAB",
            OriginXmm=WallHostReader.Mm((pts.Min(p=>p.X)+pts.Max(p=>p.X))/2),
            OriginYmm=WallHostReader.Mm((pts.Min(p=>p.Y)+pts.Max(p=>p.Y))/2),TopZmm=0};
        bool Same(SketchPoint a,SketchPoint b)=>SketchDrawing.Distance(a,b)<.001;
        bool Axis(XYZ a,XYZ o)=>a.Normalize().CrossProduct(XYZ.BasisZ).GetLength()<1e-9&&Math.Abs(WallHostReader.Mm(o.X)-h.OriginXmm)<.001&&Math.Abs(WallHostReader.Mm(o.Y)-h.OriginYmm)<.001;
        var edges=new List<(SketchPoint A,SketchPoint B)>();
        foreach(Face face in solids[0].Faces)
        {
            bool valid=face switch{PlanarFace p=>p.FaceNormal.CrossProduct(XYZ.BasisZ).GetLength()<1e-9,CylindricalFace c=>Axis(c.Axis,c.Origin),ConicalFace c=>Axis(c.Axis,c.Origin),RevolvedFace r=>r.Curve is Line&&Axis(r.Axis,r.Origin),_=>false};
            if(!valid)throw new InvalidOperationException("Slab contains noncoaxial surfaces of revolution.");
            var vertices=new List<SketchPoint>();
            foreach(var loop in face.GetEdgesAsCurveLoops())foreach(var curve in loop)foreach(var p in curve.Tessellate())
            {
                var local=WallHostReader.Local(p,h);var v=new SketchPoint(Math.Sqrt(local.X*local.X+local.Y*local.Y),local.Z);
                if(!vertices.Any(q=>Same(v,q)))vertices.Add(v);
            }
            // A planar disk has one circular boundary; the section closes at the symmetry axis.
            if(vertices.Count==1&&face is PlanarFace)vertices.Add(new(0,vertices[0].Z));
            if(vertices.Count!=2)throw new InvalidOperationException("Slab section is not a straight-edged revolution boundary.");
            var a=vertices[0];var b=vertices[1];
            if(!edges.Any(e=>Same(e.A,a)&&Same(e.B,b)||Same(e.A,b)&&Same(e.B,a)))edges.Add((a,b));
        }
        var axis=edges.SelectMany(e=>new[]{e.A,e.B}).Where(p=>Math.Abs(p.Rho)<.001).GroupBy(p=>Math.Round(p.Z,3)).Select(g=>g.First()).ToArray();
        if(axis.Length!=2)throw new InvalidOperationException("Slab top-face center could not be determined.");
        edges.Add((axis[0],axis[1]));
        var first=edges[0];var profile=new List<SketchPoint>{first.A,first.B};edges.RemoveAt(0);
        while(edges.Count>0)
        {
            var next=edges.Where(e=>Same(e.A,profile[^1])||Same(e.B,profile[^1])).ToArray();
            if(next.Length!=1)throw new InvalidOperationException("Slab section has branches or gaps.");
            var edge=next[0];profile.Add(Same(edge.A,profile[^1])?edge.B:edge.A);edges.Remove(edge);
        }
        if(!Same(profile[0],profile[^1]))throw new InvalidOperationException("Slab section is not closed.");
        profile.RemoveAt(profile.Count-1);h.TopZmm=axis.Max(p=>p.Z);h.HeightMm=h.TopZmm-axis.Min(p=>p.Z);
        h.SectionBoundary=profile.Select(p=>new SketchPoint(p.Rho,p.Z-h.TopZmm)).ToList();
        h.InnerRadiusMm=0;h.OuterRadiusMm=profile.Max(p=>p.Rho);h.IsVerified=true;
        return h;
    }
}
