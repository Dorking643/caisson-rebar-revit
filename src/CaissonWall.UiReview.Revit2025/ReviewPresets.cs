using System;
using System.Collections.Generic;
using System.Linq;

namespace CaissonWallUi;

public static class ReviewPresets
{
    public static readonly string[] Grades = { "HPB300", "HRB400", "HRB400E", "HRB500", "HRB500E" };
    public static readonly double[] Diameters = { 6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32, 36, 40, 50 };
    public static void Ensure(ReviewDocument doc)
    {
        EnsureTypes(doc.Types);
        foreach(var shape in Shapes())
            if(!doc.Shapes.Any(s=>s.Id==shape.Id)) doc.Shapes.Add(shape);
    }
    public static void EnsureTypes(List<ReviewBarType> types)
    {
        // Unknown grades from earlier drafts stay unknown; their identities are retained.
        foreach(var type in types)
            if(string.IsNullOrWhiteSpace(type.Grade) && Grades.Contains(type.Name)) type.Grade=type.Name;
        var specs=new[]{("HRB400",8d),("HRB400",10d),("HRB400",12d),("HRB400",14d),("HRB400",16d),("HRB400",18d),("HRB400",20d),("HRB400",22d),("HRB400",25d),("HPB300",8d),("HPB300",10d),("HRB500",16d),("HRB500",20d),("HRB500",25d)};
        foreach(var (grade,d) in specs)
            if(!types.Any(t=>t.Grade==grade && t.DiameterMm==d)){
                var id=$"preset-bar-{grade}-{d:0}";
                if(types.Any(t=>t.Id==id))id=Guid.NewGuid().ToString("N");
                types.Add(new(){Id=id,Name=grade,Grade=grade,DiameterMm=d});
            }
    }
    public static IEnumerable<ShapeTemplate> Shapes()
    {
        yield return Path("straight","Straight",(0,0),(0,-1000));
        yield return Path("l","L Shape",(0,0),(0,-800),(200,-800));
        yield return Path("u","U Shape",(0,0),(0,-600),(300,-600),(300,0));
        yield return Path("c","C Shape",(180,0),(0,0),(0,-800),(180,-800));
        yield return Path("bent","Bent Shape",(0,0),(0,-600),(100,-750),(250,-750));
    }
    public static string DisplayName(ShapeTemplate shape)
    {
        // Display aliases keep the saved source descriptions and geometry intact.
        // The same aliases are used by the library and the shape editor.
        var name=shape.Name;
        foreach(var (source,english) in new[]{
            ("N1 原图双端折起（端段323/131/600，未圆弯）","N1 · Bent Ends"),
            ("主网直筋（N2/N3共用，参考跨度4000）","N2/N3 · Straight"),
            ("N4 短直筋（表单长1117，仅示意）","N4 · Short Straight"),
            ("N5 原图马凳（300/200/300/200/300，高200）","N5 · Chair"),
            ("N6 插筋（竖1200＋上水平240）","N6 · Starter"),
            ("N7 插筋（水平1100＋向下240）","N7 · Starter"),
            ("N1 · 内径64 · 六处相切圆弧","N1 · Inside Ø64")})
            if(name.StartsWith(source,StringComparison.Ordinal))return english+name[source.Length..];
        name=System.Text.RegularExpressions.Regex.Replace(name,@"^(?:实读|Loaded)\s*·\s*","");
        return name switch
        {
            "直筋" => "Straight",
            "L 形" => "L Shape",
            "U 形" => "U Shape",
            "C 形" => "C Shape",
            "折线形" => "Bent Shape",
            _ => name
        };
    }
    static ShapeTemplate Path(string key,string name,params (double u,double v)[] points)
    {
        var s=new ShapeTemplate{Id="preset-shape-"+key,Name=name};
        for(int i=1;i<points.Length;i++) s.Segments.Add(new(){Kind=SegmentKind.Line,Start=new(points[i-1].u,points[i-1].v),End=new(points[i].u,points[i].v)});
        return s;
    }
    public static ShapeTemplate NewFrom(ShapeTemplate source,string name)
    {
        var copy=source.Copy();copy.Id=Guid.NewGuid().ToString("N");copy.Name=name;return copy;
    }
}
