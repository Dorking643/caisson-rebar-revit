using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CaissonWallUi;

/// <summary>Managed preview only. The section is a cut through eta, not a projection of all bars.</summary>
public sealed class SlabCanvas : Control
{
    readonly bool _plan;
    readonly List<(string Id,int Member,PointF[] Points,float Width)> _hits=new();
    double _scale=1,_centerX,_centerY;
    bool _fitted,_panning;
    Point _last;
    string? _dragId;
    int _dragMember;
    Point _dragStartScreen;
    Point3 _dragStartWorld=new();
    double _dragScale,_dragCos,_dragSin;
    bool _dragging,_endingCapture,_placementMode;
    public double RadiusMm {get;set;}=2500;
    public double HeightMm {get;set;}=400;
    public double SideCoverMm {get;set;}=40;
    public double TopCoverMm {get;set;}=40;
    public double BottomCoverMm {get;set;}=40;
    public double SectionAngleDeg {get;set;}
    public double SectionOffsetMm {get;set;}
    public string SectionLabel {get;set;}="A";
    public bool ShowOrthogonalCut {get;set;}
    public double OrthogonalSectionAngleDeg {get;set;}=90;
    public double OrthogonalSectionOffsetMm {get;set;}
    public List<BarPath3> Bars {get;set;}=new();
    public List<ExistingBarPath> ExistingBars {get;set;}=new();
    public List<SketchPoint> SectionBoundary {get;set;}=new();
    public HashSet<string> ProblemGroupIds {get;set;}=new();
    public HashSet<string> PendingGroupIds {get;set;}=new();
    public HashSet<string> AcceptedCollisionGroupIds {get;set;}=new();
    public HashSet<string> AcceptedCoverGroupIds {get;set;}=new();
    public string? SelectedGroupId {get;set;}
    public int SelectedMemberIndex {get;set;}=-1;
    public bool ShowsSelectedShape=>!_plan&&SelectedSectionPaths().Any();
    public bool PlacementMode
    {
        get=>_placementMode;
        set{if(value!=_placementMode){CancelDrag();_placementMode=value;}}
    }
    /// <summary>True from a bar press until release, including the drag threshold interval.</summary>
    public bool IsDragGesture=>_dragId!=null;
    public event Action<string,int>? BarSelected;
    public event Action<SlabDragGesture>? BarDragStarted;
    public event Action<SlabDragGesture>? BarDragMoved;
    public event Action<bool>? BarDragCompleted;
    public event Action<Point3>? PointClicked;
    public event Action? FinishRequested;
    public event Action? DeleteRequested;
    public SlabCanvas(bool plan)
    {
        _plan=plan;DoubleBuffered=true;Dock=DockStyle.Fill;BackColor=Color.White;TabStop=true;
        SetStyle(ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        Cursor=Cursors.Cross;AccessibleName=plan?"Slab plan":"Slab section";
    }
    public void Fit()
    {
        double radius=Math.Max(1,RadiusMm);
        _centerX=0;_centerY=_plan?0:-Math.Max(1,HeightMm)/2;
        _scale=Math.Max(.000001,Math.Min(Math.Max(40,ClientSize.Width-100)/(2*radius),Math.Max(40,ClientSize.Height-108)/(_plan?2*radius:Math.Max(HeightMm*1.8,2*radius*.42))));
        _fitted=true;Invalidate();
    }
    public void FitSectionToSelected()
    {
        if(_plan){Fit();return;}
        double minU=double.PositiveInfinity,maxU=double.NegativeInfinity,minZ=double.PositiveInfinity,maxZ=double.NegativeInfinity;
        void Include(double u,double z,double padding=0)
        {
            if(!double.IsFinite(u)||!double.IsFinite(z))return;
            minU=Math.Min(minU,u-padding);maxU=Math.Max(maxU,u+padding);minZ=Math.Min(minZ,z-padding);maxZ=Math.Max(maxZ,z+padding);
        }
        double offset=Math.Abs(SectionOffsetMm);
        if(SectionBoundary.Count>=3)
        {
            foreach(var p in ClippedHostSection())
            {
                double u=Math.Sqrt(Math.Max(0,p.Rho*p.Rho-offset*offset));Include(-u,p.Z);Include(u,p.Z);
            }
        }
        else if(double.IsFinite(RadiusMm)&&RadiusMm>0&&offset<=RadiusMm&&double.IsFinite(HeightMm)&&HeightMm>0)
        {
            double half=Math.Sqrt(Math.Max(0,RadiusMm*RadiusMm-offset*offset));Include(-half,0);Include(half,-HeightMm);
        }
        foreach(var bar in SelectedSectionPaths())foreach(var curve in bar.Curves)
        {
            try{foreach(var p in SlabSectionGeometry.ExtremePoints(curve,SectionAngleDeg)){var q=ToSection(p);Include(q.U,q.Z,Math.Max(0,bar.DiameterMm/2));}}
            catch{continue;}
        }
        if(!double.IsFinite(minU)||!double.IsFinite(minZ)){Fit();return;}
        double left=34,top=48,width=Math.Max(20,ClientSize.Width-62),height=Math.Max(20,ClientSize.Height-124);
        _scale=Math.Clamp(Math.Min(width/Math.Max(1,maxU-minU),height/Math.Max(1,maxZ-minZ)),.00001,50);
        _centerX=(minU+maxU)/2+(ClientSize.Width/2.0-(left+width/2))/_scale;
        _centerY=(minZ+maxZ)/2+((top+height/2)-ClientSize.Height/2.0)/_scale;
        _fitted=true;Invalidate();
    }
    IEnumerable<BarPath3> SelectedSectionPaths()
    {
        if(_plan||SelectedGroupId==null)yield break;
        foreach(var bar in Bars.Where(b=>b.DraftId==SelectedGroupId&&(SelectedMemberIndex<0||b.MemberIndex==SelectedMemberIndex)))
        {
            bool coplanar=false;
            try{coplanar=bar.Curves.Count>0&&bar.Curves.All(c=>SlabSectionGeometry.Intersect(c,SectionAngleDeg,SectionOffsetMm).Coplanar);}
            catch{ }
            if(coplanar)yield return bar;
        }
    }
    PointF Map(double x,double y)=>new((float)(ClientSize.Width/2.0+(x-_centerX)*_scale),(float)(ClientSize.Height/2.0-(y-_centerY)*_scale));
    (double X,double Y) Unmap(Point p)=>(_centerX+(p.X-ClientSize.Width/2.0)/_scale,_centerY-(p.Y-ClientSize.Height/2.0)/_scale);
    public (double U,double Eta,double Z) ToSection(Point3 p)
    {
        double a=SectionAngleDeg*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a);
        return(p.X*c+p.Y*s,-p.X*s+p.Y*c,p.Z);
    }
    public Point3 FromSection(double u,double z)
    {
        double a=SectionAngleDeg*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a);
        return new(u*c-SectionOffsetMm*s,u*s+SectionOffsetMm*c,z);
    }
    PointF Project(Point3 p){if(_plan)return Map(p.X,p.Y);var q=ToSection(p);return Map(q.U,q.Z);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(!_fitted)Fit();
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;_hits.Clear();
        using var concrete=new SolidBrush(Color.FromArgb(244,246,247));
        using var outline=new Pen(Color.FromArgb(119,136,149),1.3f);
        using var cover=new Pen(Color.FromArgb(185,196,204),1){DashStyle=DashStyle.Dash};
        using var axis=new Pen(Color.FromArgb(191,203,212),1){DashStyle=DashStyle.DashDot};
        double r=Math.Max(1,RadiusMm),h=Math.Max(1,HeightMm);
        if(_plan)
        {
            var a=Map(-r,r);float d=(float)(2*r*_scale);g.FillEllipse(concrete,a.X,a.Y,d,d);g.DrawEllipse(outline,a.X,a.Y,d,d);
            double cr=Math.Max(0,r-SideCoverMm);var b=Map(-cr,cr);g.DrawEllipse(cover,b.X,b.Y,(float)(2*cr*_scale),(float)(2*cr*_scale));
            g.DrawLine(axis,Map(-r*1.08,0),Map(r*1.08,0));g.DrawLine(axis,Map(0,-r*1.08),Map(0,r*1.08));
            DrawCut(SectionAngleDeg,SectionOffsetMm,SectionLabel,Color.FromArgb(92,109,153));
            if(ShowOrthogonalCut)DrawCut(OrthogonalSectionAngleDeg,OrthogonalSectionOffsetMm,"B",Color.FromArgb(147,105,73));
            void DrawCut(double angle,double offset,string name,Color color)
            {
                if(!double.IsFinite(angle)||!double.IsFinite(offset)||Math.Abs(offset)>r)return;
                double th=angle*Math.PI/180,c=Math.Cos(th),s=Math.Sin(th),half=Math.Sqrt(Math.Max(0,r*r-offset*offset))+r*.04;
                using var cut=new Pen(color,1){DashStyle=DashStyle.Dash};
                PointF At(double u)=>Map(u*c-offset*s,u*s+offset*c);
                g.DrawLine(cut,At(-half),At(half));
                var size=g.MeasureString(name,Font);
                foreach(double u in new[]{-half,half})
                {
                    var p=At(u);
                    LabelAt(g,name,p,color,(float)(s*(9+size.Width/2)-size.Width/2),(float)(c*(9+size.Height/2)-size.Height/2));
                }
            }
            LabelAt(g,"X",Map(r*1.09,0),Color.DimGray,2,-Font.Height-6);
            LabelAt(g,"Y",Map(0,r*1.09),Color.DimGray,-Font.Height-6,-Font.Height-2);
            Label(g,"Ob",Map(0,0),Color.DimGray);
        }
        else
        {
            double half=Math.Sqrt(Math.Max(0,r*r-SectionOffsetMm*SectionOffsetMm));
            var a=Map(-half,0);var b=Map(half,-h);var rect=RectangleF.FromLTRB(a.X,a.Y,b.X,b.Y);
            if(SectionBoundary.Count>=3)DrawHostSection(g,concrete,outline);
            else{g.FillRectangle(concrete,rect);g.DrawRectangle(outline,rect.X,rect.Y,rect.Width,rect.Height);}
            double cr=Math.Max(0,r-SideCoverMm),ch=Math.Sqrt(Math.Max(0,cr*cr-SectionOffsetMm*SectionOffsetMm));
            if(ch>0&&TopCoverMm+BottomCoverMm<h){var ca=Map(-ch,-TopCoverMm);var cb=Map(ch,-h+BottomCoverMm);g.DrawRectangle(cover,ca.X,ca.Y,cb.X-ca.X,cb.Y-ca.Y);}
            g.DrawLine(axis,Map(-r*1.08,0),Map(r*1.08,0));g.DrawLine(axis,Map(0,-h*1.18),Map(0,h*.22));
            LabelAt(g,HorizontalAxis,Map(r*1.09,0),Color.DimGray,2,-Font.Height-6);
            LabelAt(g,"Z",Map(0,0),Color.DimGray,6,-Font.Height-22);
            LabelAt(g,"0",Map(0,0),Color.DimGray,-Font.Height,-Font.Height-3);
            Label(g,$"−{h:0.###}",Map(-half,-h),Color.DimGray);
        }
        foreach(var p in ExistingBars)DrawPath(g,p.Curves,p.DiameterMm,"",p.PositionIndex,Color.FromArgb(181,189,196),false);
        foreach(var p in Bars.Where(b=>b.DraftId!=SelectedGroupId))DrawBar(g,p,false);
        foreach(var p in Bars.Where(b=>b.DraftId==SelectedGroupId))DrawBar(g,p,true);
        using var heading=new SolidBrush(Color.FromArgb(35,58,76));
        bool along=ShowsSelectedShape;
        string title=_plan?"Plan · X–Y":$"{SectionLabel}–{SectionLabel} · {HorizontalAxis}–Z · {SectionPositionLabel}"+(along?" · Along bar":"");
        g.DrawString(title,Font,heading,new RectangleF(12,10,Math.Max(1,Width-24),Math.Max(38,Font.Height*2+2)));
        if(along){using var selectedBorder=new Pen(Color.FromArgb(57,133,189),1.5f);g.DrawRectangle(selectedBorder,1,1,Math.Max(0,Width-3),Math.Max(0,Height-3));}
        using var dimension=new SolidBrush(Color.DimGray);
        string dimensionText=_plan?$"D = {2*r:0.###} mm":$"H = {h:0.###} mm";
        var dimensionSize=g.MeasureString(dimensionText,Font);
        g.DrawString(dimensionText,Font,dimension,Math.Max(10,(Width-dimensionSize.Width)/2),Math.Max(48,Height-48));
        using var muted=new SolidBrush(Color.FromArgb(116,131,143));
        const string fullNavigation="Wheel: zoom · Middle drag: pan · Double-click: fit";
        string navigation=g.MeasureString(fullNavigation,Font).Width>Width-20?"Wheel zoom · Middle pan · 2× fit":fullNavigation;
        g.DrawString(navigation,Font,muted,10,Math.Max(30,Height-25));
    }
    string HorizontalAxis=>AxisNear(0)?"X":AxisNear(90)?"Y":"u";
    string SectionPositionLabel=>AxisNear(0)?$"Y {SectionOffsetMm:0.###} mm":AxisNear(90)?$"X {-SectionOffsetMm:0.###} mm":$"θ{SectionAngleDeg:0.###}° · η{SectionOffsetMm:0.###} mm";
    bool AxisNear(double angle)=>Math.Abs(Math.IEEERemainder(SectionAngleDeg-angle,360))<.001;
    void DrawBar(Graphics g,BarPath3 p,bool selected)
    {
        var color=ProblemGroupIds.Contains(p.DraftId)?Color.FromArgb(202,54,49):PendingGroupIds.Contains(p.DraftId)||AcceptedCollisionGroupIds.Contains(p.DraftId)||AcceptedCoverGroupIds.Contains(p.DraftId)?Color.FromArgb(168,128,63):selected?Color.FromArgb(0,112,163):Color.FromArgb(52,126,153);
        DrawPath(g,p.Curves,p.DiameterMm,p.DraftId,p.MemberIndex,color,selected);
        // One visible positioning handle per selected group/member. Dragging it
        // moves the group; it never changes an individual line or bend.
        if(selected&&!PlacementMode&&p.MemberIndex==(SelectedMemberIndex>=0?SelectedMemberIndex:Bars.Where(b=>b.DraftId==p.DraftId).Min(b=>b.MemberIndex)))
        {
            Point3? anchor=null;
            foreach(var curve in p.Curves)
            {
                try
                {
                    if(_plan){anchor=curve.Start;break;}
                    var trace=SlabSectionGeometry.Intersect(curve,SectionAngleDeg,SectionOffsetMm);
                    anchor=trace.Coplanar?curve.Start:trace.Intersections.FirstOrDefault();
                    if(anchor!=null)break;
                }
                catch{ }
            }
            if(anchor!=null)
            {
                var point=Project(anchor);float size=8f*DeviceDpi/96f;
                using var fill=new SolidBrush(Color.White);using var outline=new Pen(Color.FromArgb(0,112,163),1.5f);
                g.FillEllipse(fill,point.X-size/2,point.Y-size/2,size,size);g.DrawEllipse(outline,point.X-size/2,point.Y-size/2,size,size);
                _hits.Add((p.DraftId,p.MemberIndex,new[]{point},size));
            }
        }
    }
    void DrawHostSection(Graphics g,Brush fill,Pen outline)
    {
        double clip=Math.Abs(SectionOffsetMm);var polygon=ClippedHostSection();
        if(polygon.Count<3)return;
        foreach(int sign in new[]{-1,1})
        {
            var points=new List<PointF>();
            for(int i=0;i<polygon.Count;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Count];int n=Math.Clamp((int)Math.Ceiling(SketchDrawing.Distance(a,b)*_scale/5),1,1000);
                for(int j=0;j<n;j++){double t=(double)j/n,rho=a.Rho+t*(b.Rho-a.Rho);points.Add(Map(sign*Math.Sqrt(Math.Max(0,rho*rho-clip*clip)),a.Z+t*(b.Z-a.Z)));}
            }
            if(points.Count>=3)g.FillPolygon(fill,points.ToArray());
            for(int i=0;i<polygon.Count;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Count];if(Math.Abs(a.Rho-clip)<1e-7&&Math.Abs(b.Rho-clip)<1e-7)continue;
                int n=Math.Clamp((int)Math.Ceiling(SketchDrawing.Distance(a,b)*_scale/5),1,1000);var edge=new PointF[n+1];
                for(int j=0;j<=n;j++){double t=(double)j/n,rho=a.Rho+t*(b.Rho-a.Rho);edge[j]=Map(sign*Math.Sqrt(Math.Max(0,rho*rho-clip*clip)),a.Z+t*(b.Z-a.Z));}g.DrawLines(outline,edge);
            }
        }
    }
    List<SketchPoint> ClippedHostSection()
    {
        double clip=Math.Abs(SectionOffsetMm);var polygon=new List<SketchPoint>();
        for(int i=0;i<SectionBoundary.Count;i++)
        {
            var a=SectionBoundary[i];var b=SectionBoundary[(i+1)%SectionBoundary.Count];bool ia=a.Rho>=clip,ib=b.Rho>=clip;
            if(ia)polygon.Add(a.Copy());
            if(ia!=ib){double t=(clip-a.Rho)/(b.Rho-a.Rho);polygon.Add(new(clip,a.Z+t*(b.Z-a.Z)));}
        }
        return polygon;
    }
    void DrawPath(Graphics g,List<Curve3> curves,double diameter,string id,int member,Color color,bool selected)
    {
        float width=(float)Math.Clamp(diameter*_scale,.8,600);
        using var pen=new Pen(color,width){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        using var halo=new Pen(Color.FromArgb(115,49,149,235),width+6){StartCap=LineCap.Round,EndCap=LineCap.Round};
        using var pointHalo=new SolidBrush(Color.FromArgb(115,49,149,235));
        foreach(var curve in curves)
        {
            List<Point3> samples;SlabSectionTrace? trace=null;
            try
            {
                if(!_plan)trace=SlabSectionGeometry.Intersect(curve,SectionAngleDeg,SectionOffsetMm);
                samples=_plan||trace!.Coplanar?SlabGeometry.Sample(curve,Math.Max(1,3/_scale)):trace.Intersections;
            }
            catch{continue;}
            if((_plan||trace!.Coplanar)&&samples.Count>=2)
            {
                var points=samples.Select(Project).ToArray();
                if(selected&&(SelectedMemberIndex<0||SelectedMemberIndex==member))g.DrawLines(halo,points);
                g.DrawLines(pen,points);if(id.Length>0)_hits.Add((id,member,points,width));
            }
            else if(!_plan)
            {
                // Analytic centreline-plane intersections; arc roots are independent of zoom and display tessellation.
                PointF? last=null;
                foreach(var point in samples)
                {
                    var q=ToSection(point);var p=Map(q.U,q.Z);
                    if(last is PointF old&&Distance(old,p)<1)continue;last=p;
                    using var brush=new SolidBrush(color);float size=Math.Max(.8f,width);
                    if(selected&&(SelectedMemberIndex<0||SelectedMemberIndex==member))g.FillEllipse(pointHalo,p.X-(size+6)/2,p.Y-(size+6)/2,size+6,size+6);
                    g.FillEllipse(brush,p.X-size/2,p.Y-size/2,size,size);
                    if(id.Length>0)_hits.Add((id,member,new[]{p},width));
                }
            }
        }
    }
    void Label(Graphics g,string text,PointF p,Color color){using var brush=new SolidBrush(color);g.DrawString(text,Font,brush,p.X+4,p.Y+3);}
    void LabelAt(Graphics g,string text,PointF p,Color color,float dx,float dy){using var brush=new SolidBrush(color);g.DrawString(text,Font,brush,p.X+dx,p.Y+dy);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);Focus();
        if(e.Button==MouseButtons.Middle){CancelDrag();_panning=true;_last=e.Location;Capture=true;return;}
        if(e.Button!=MouseButtons.Left)return;
        if(!PlacementMode)
        {
            var hit=Hit(e.Location);
            if(hit.Id!=null)
            {
                CancelDrag();_dragId=hit.Id;_dragMember=hit.Member;_dragStartScreen=e.Location;_dragScale=_scale;
                double angle=SectionAngleDeg*Math.PI/180;_dragCos=Math.Cos(angle);_dragSin=Math.Sin(angle);
                var start=Unmap(e.Location);_dragStartWorld=_plan?new(start.X,start.Y,0):FromSection(start.X,start.Y);
                Capture=true;
                // The parent can inspect IsDragGesture to avoid refitting the
                // section under the pointer while a selection is being pressed.
                BarSelected?.Invoke(hit.Id,hit.Member);return;
            }
        }
        var q=Unmap(e.Location);
        if(_plan)PointClicked?.Invoke(new(q.X,q.Y,0));
        else PointClicked?.Invoke(FromSection(q.X,q.Y));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(_panning){_centerX-=(e.X-_last.X)/_scale;_centerY+=(e.Y-_last.Y)/_scale;_last=e.Location;Invalidate();return;}
        if(IsDragGesture){MoveDrag(e.Location);return;}
        Cursor=!PlacementMode&&Hit(e.Location).Id!=null?Cursors.SizeAll:Cursors.Cross;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.Button==MouseButtons.Middle){_panning=false;Capture=false;return;}
        if(e.Button!=MouseButtons.Left||!IsDragGesture)return;
        MoveDrag(e.Location);EndDrag(false);
    }
    (string? Id,int Member) Hit(Point point)
    {
        var hit=_hits.Select((h,index)=>(h,index,d:DistanceTo(h.Points,point)-h.Width/2)).Where(x=>x.d<=6).OrderBy(x=>x.d).ThenByDescending(x=>x.index).FirstOrDefault();
        return(hit.h.Id,hit.h.Member);
    }
    void MoveDrag(Point point)
    {
        if(_dragId==null)return;
        double dx=point.X-_dragStartScreen.X,dy=point.Y-_dragStartScreen.Y;
        if(!_dragging&&dx*dx+dy*dy<Math.Pow(4d*DeviceDpi/96,2))return;
        var delta=_plan?new Point3(dx/_dragScale,-dy/_dragScale,0):new Point3(dx/_dragScale*_dragCos,dx/_dragScale*_dragSin,-dy/_dragScale);
        var gesture=new SlabDragGesture(_dragId,_dragMember,new(_dragStartWorld.X,_dragStartWorld.Y,_dragStartWorld.Z),delta,_plan);
        if(!_dragging){_dragging=true;Cursor=Cursors.SizeAll;BarDragStarted?.Invoke(gesture);}
        if(_dragId!=null)BarDragMoved?.Invoke(gesture);
    }
    public void CancelDrag()=>EndDrag(true);
    void EndDrag(bool cancelled)
    {
        if(_dragId==null)return;
        bool edited=_dragging;_dragId=null;_dragging=false;
        _endingCapture=true;try{Capture=false;}finally{_endingCapture=false;}
        Cursor=Cursors.Cross;
        if(edited)BarDragCompleted?.Invoke(cancelled);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if(!_endingCapture&&!Capture){_panning=false;CancelDrag();}
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(IsDragGesture)return;
        base.OnMouseWheel(e);var before=Unmap(e.Location);_scale=Math.Clamp(_scale*Math.Pow(1.18,e.Delta/120.0),.00001,50);
        var after=Unmap(e.Location);_centerX+=before.X-after.X;_centerY+=before.Y-after.Y;Invalidate();
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e){base.OnMouseDoubleClick(e);if(e.Button==MouseButtons.Left){CancelDrag();Fit();}}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(e.KeyCode==Keys.Escape){CancelDrag();FinishRequested?.Invoke();e.Handled=true;e.SuppressKeyPress=true;}if(e.KeyCode==Keys.Delete){CancelDrag();DeleteRequested?.Invoke();e.Handled=true;}
    }
    static double Distance(PointF a,PointF b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    static double DistanceTo(PointF[] pts,Point p)
    {
        if(pts.Length==1)return Distance(pts[0],p);double best=double.PositiveInfinity;
        for(int i=1;i<pts.Length;i++){var a=pts[i-1];var b=pts[i];double dx=b.X-a.X,dy=b.Y-a.Y,l=dx*dx+dy*dy,t=l<1e-12?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/l,0,1);best=Math.Min(best,Distance(new((float)(a.X+t*dx),(float)(a.Y+t*dy)),p));}return best;
    }
}

/// <summary>Total millimetre displacement in the pointer-down coordinate frame.</summary>
public sealed record SlabDragGesture(string GroupId,int MemberIndex,Point3 StartWorld,Point3 DeltaWorld,bool IsPlan);

/// <summary>Centreline traces on a vertical section plane. All lengths are millimetres.</summary>
public sealed record SlabSectionTrace(bool Coplanar,List<Point3> Intersections);

public static class SlabSectionGeometry
{
    public const double PlaneToleranceMm=1e-7;
    public static SlabSectionTrace Intersect(Curve3 curve,double angleDeg,double offsetMm)
    {
        if(!double.IsFinite(angleDeg)||!double.IsFinite(offsetMm))throw new ArgumentException("Invalid section coordinates.");
        var points=curve.Kind==SegmentKind.Line?new[]{curve.Start,curve.End}:new[]{curve.Start,curve.Middle,curve.End};
        if(points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Z)))throw new ArgumentException("Invalid centerline coordinates.");
        double angle=angleDeg*Math.PI/180;var normal=new Point3(-Math.Sin(angle),Math.Cos(angle),0);
        double Signed(Point3 p)=>Dot(normal,p)-offsetMm;
        var result=new List<Point3>();
        if(curve.Kind==SegmentKind.Line)
        {
            double a=Signed(curve.Start),b=Signed(curve.End);
            if(Math.Abs(a)<=PlaneToleranceMm&&Math.Abs(b)<=PlaneToleranceMm)return new(true,result);
            double delta=b-a;if(Math.Abs(delta)<=PlaneToleranceMm)return new(false,result);
            double t=-a/delta;
            if(t>=-1e-12&&t<=1+1e-12)result.Add(Add(curve.Start,Scale(Sub(curve.End,curve.Start),Math.Clamp(t,0,1))));
            return new(false,result);
        }
        if(curve.Kind!=SegmentKind.Arc)throw new ArgumentException("Only lines and arcs are supported.");
        var (center,ex,ey,radius,sweep)=ArcFrame(curve);
        // n·(center + r ex cosφ + r ey sinφ) = offset. Solve both exact roots on this directed arc.
        double ca=radius*Dot(normal,ex),sa=radius*Dot(normal,ey),constant=Signed(center),amplitude=Math.Sqrt(ca*ca+sa*sa);
        if(amplitude<=PlaneToleranceMm)return new(Math.Abs(constant)<=PlaneToleranceMm,result);
        double ratio=-constant/amplitude;
        if(ratio < -1-PlaneToleranceMm/amplitude||ratio > 1+PlaneToleranceMm/amplitude)return new(false,result);
        double phase=Math.Atan2(sa,ca),root=Math.Acos(Math.Clamp(ratio,-1,1)),angleTolerance=PlaneToleranceMm/Math.Max(radius,1);
        foreach(double raw in new[]{phase-root,phase+root})
        {
            double phi=Positive(raw);if(2*Math.PI-phi<=angleTolerance)phi=0;
            if(phi>sweep+angleTolerance)continue;phi=Math.Clamp(phi,0,sweep);
            var p=Add(center,Scale(Add(Scale(ex,Math.Cos(phi)),Scale(ey,Math.Sin(phi))),radius));
            if(result.All(q=>Length(Sub(p,q))>PlaneToleranceMm))result.Add(p);
        }
        return new(false,result);
    }
    // Exact extrema of the real curve in the section's horizontal coordinate and in Z.
    internal static List<Point3> ExtremePoints(Curve3 curve,double angleDeg)
    {
        var points=new List<Point3>{curve.Start,curve.End};if(curve.Kind==SegmentKind.Line)return points;
        var (center,ex,ey,radius,sweep)=ArcFrame(curve);double angle=angleDeg*Math.PI/180;
        foreach(var axis in new[]{new Point3(Math.Cos(angle),Math.Sin(angle),0),new Point3(0,0,1)})
        {
            double phi=Math.Atan2(Dot(ey,axis),Dot(ex,axis));
            foreach(double raw in new[]{phi,phi+Math.PI})
            {
                double t=Positive(raw);if(t>sweep)continue;
                points.Add(Add(center,Scale(Add(Scale(ex,Math.Cos(t)),Scale(ey,Math.Sin(t))),radius)));
            }
        }
        return points;
    }
    static (Point3 Center,Point3 Ex,Point3 Ey,double Radius,double Sweep) ArcFrame(Curve3 curve)
    {
        var u=Sub(curve.Middle,curve.Start);var v=Sub(curve.End,curve.Start);var n=Cross(u,v);double nn=Dot(n,n);
        if(nn<=1e-12*Dot(u,u)*Dot(v,v)||nn==0)throw new ArgumentException("The arc points are collinear or numerically unstable.");
        var center=Add(curve.Start,Scale(Add(Scale(Cross(n,u),Dot(v,v)),Scale(Cross(v,n),Dot(u,u))),1/(2*nn)));
        double radius=Length(Sub(curve.Start,center));var ex=Scale(Sub(curve.Start,center),1/radius);var ey=Cross(Scale(n,1/Math.Sqrt(nn)),ex);
        double Angle(Point3 p){var d=Sub(p,center);return Positive(Math.Atan2(Dot(d,ey),Dot(d,ex)));}
        double sweep=Angle(curve.End),middle=Angle(curve.Middle);
        if(!double.IsFinite(radius)||radius<=0||sweep<=0||middle<=0||middle>=sweep)throw new ArgumentException("Invalid three-point arc path.");
        return(center,ex,ey,radius,sweep);
    }
    static double Positive(double a){a%=2*Math.PI;return a<0?a+2*Math.PI:a;}
    static Point3 Add(Point3 a,Point3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    static Point3 Sub(Point3 a,Point3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    static Point3 Scale(Point3 a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
    static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    static Point3 Cross(Point3 a,Point3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    static double Length(Point3 p)=>Math.Sqrt(Dot(p,p));
}
