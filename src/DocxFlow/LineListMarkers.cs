using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal sealed partial class LineComposer
{
    internal IReadOnlyDictionary<string,MeasuredListMarker> MeasureListMarkers(
        IEnumerable<LayoutParagraph> layout,IReadOnlyDictionary<string,TextParagraph> paragraphs,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(composing)throw new InvalidOperationException("LINE_REENTRANT");
        var result=new Dictionary<string,MeasuredListMarker>(StringComparer.Ordinal);
        cancellation=token;
        work=glyphs=0;
        clock.Restart();
        try
        {
            foreach(var entry in layout)
            {
                Check();
                if(entry.List is {Numbering.LevelId:{} level} label&&paragraphs.TryGetValue(entry.Id,out var paragraph))
                    result.Add(entry.Id,MeasureListMarker(paragraph,label,level));
            }
            return result;
        }
        finally{clock.Stop();cancellation=default;}
    }

    MeasuredListMarker MeasureListMarker(TextParagraph paragraph,ListLabel label,string level)
    {
        var properties=styles.Read(paragraph,level,nativeNumbering:true);
        var style=session.ListMarkerStyle(paragraph,level);
        var source=model.Source(level);
        var issues=ImmutableArray.CreateBuilder<FlowIssue>();
        long anchor=properties.Left+properties.First;
        if(label.Text.Length==0)return Result(textStart:anchor);
        if(paragraph.RightToLeft||style.RightToLeft||properties.Alignment is LineAlignment.Center or LineAlignment.Right)
            return Refuse("LINE_LIST_DIRECTION","Direction ou alignement du paragraphe de liste hors profil.");
        if(style.SuppressText||style.Underline is not ("none" or "single" or "double"))
            return Refuse("LINE_LIST_MARK_FORMAT","Visibilité ou soulignement du libellé hors profil.");

        var marker=Decoration("listMarker",label.Text,style,source,0);
        if(marker?.Fragment.Measure is not {} measured)
            return Refuse("LINE_LIST_GLYPH_MISSING","Libellé original conservé. Face ou glyphe de numérotation indisponible.");
        long width=measured.Metrics.Width;
        if(width<0)return Refuse("LINE_LIST_WIDTH","Avance du libellé hors profil.");
        if(marker.Fragment.Face?.Status=="substituted")
            issues.Add(new("LINE_LIST_FACE_SUBSTITUTED",source.Location,marker.Fragment.Face.Reason,false));

        long x=anchor-(label.Alignment switch{"right"=>width,"center"=>width/2,_=>0});
        long end=x+width;
        long textStart=end;
        LineTabStop? tab=null;
        if(label.Suffix=="space")
        {
            long? spacing=marker.Fragment.LegacySymbol is not null?session.SymbolSpaceAdvance(marker.Fragment,Check,Count):
                Decoration("listSpace"," ",style,source,0)?.Fragment.Measure?.Metrics.Width;
            if(spacing is not (>0 and var advance))
                return Refuse("LINE_LIST_SPACE_MISSING","Espace du suffixe sans avance qualifiée.");
            textStart+=advance;
        }
        else if(label.Suffix=="tab")
        {
            tab=NextStop(properties,0,end);
            if(tab.Value.Kind is not (LineTabKind.Left or LineTabKind.Numbering)||tab.Value.Leader!="none")
                return Refuse("LINE_LIST_TAB_KIND","Taquet ou motif du suffixe de liste hors profil.");
            textStart=tab.Value.Position;
        }
        return Result(marker with{X=x},x,textStart,tab);

        MeasuredListMarker Result(LineDecoration? decoration=null,long? x=null,long? textStart=null,LineTabStop? tab=null) =>
            new(label,properties,style,decoration,x,textStart,tab,issues.ToImmutable());
        MeasuredListMarker Refuse(string code,string message)
        {
            issues.Add(new(code,source.Location,message,false));
            return Result();
        }
    }
}
