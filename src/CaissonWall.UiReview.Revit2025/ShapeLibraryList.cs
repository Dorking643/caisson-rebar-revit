using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CaissonWallUi;

public sealed class ShapeLibraryList : ListBox
{
    public ShapeLibraryList() {
        Dock=DockStyle.Fill;IntegralHeight=false;DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=54;HorizontalScrollbar=false;
        FormattingEnabled=true;
        Format+=(_,e)=>{if(e.ListItem is ShapeTemplate shape)e.Value=ReviewPresets.DisplayName(shape);};
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0||e.Index>=Items.Count)return;
        bool selected=(e.State&DrawItemState.Selected)!=0;
        using var fill=new SolidBrush(selected?Color.FromArgb(222,239,248):Color.White);
        using var ink=new SolidBrush(Color.FromArgb(38,60,76));
        using var pen=new Pen(selected?Color.FromArgb(0,119,163):Color.FromArgb(92,126,146),1.8f);
        e.Graphics.FillRectangle(fill,e.Bounds);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        if(Items[e.Index] is ShapeTemplate s)
        {
            // Display the same rounded copy that insertion will use; retain the editable
            // control polygon in the library until the user saves a shape explicitly.
            if(ShapeDefaults.TryPrepare(s,out var preview,out _))s=preview;
            var pts=s.Segments.SelectMany(SketchDrawing.Polyline).ToArray();
            if(pts.Length>0){
                double xmin=pts.Min(p=>p.Rho),xmax=pts.Max(p=>p.Rho),ymin=pts.Min(p=>p.Z),ymax=pts.Max(p=>p.Z);
                double scale=Math.Min(44/Math.Max(1,xmax-xmin),34/Math.Max(1,ymax-ymin));
                PointF Map(SketchPoint p)=>new((float)(e.Bounds.Left+31+(p.Rho-(xmin+xmax)/2)*scale),(float)(e.Bounds.Top+27-(p.Z-(ymin+ymax)/2)*scale));
                foreach(var seg in s.Segments)e.Graphics.DrawLines(pen,SketchDrawing.Polyline(seg).Select(Map).ToArray());
            }
            var rect=new Rectangle(e.Bounds.Left+63,e.Bounds.Top+5,Math.Max(1,e.Bounds.Width-67),e.Bounds.Height-10);
            TextRenderer.DrawText(e.Graphics,ReviewPresets.DisplayName(s),e.Font,rect,Color.FromArgb(38,60,76),TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        }
        if((e.State&DrawItemState.Focus)!=0)e.DrawFocusRectangle();
    }
}
