namespace LecteurWord.Flow;

internal static partial class MetafileReader
{
    // MS-WMF §2.3 and §2.3.6.1. Preserve the original bytes upstream.
    // The observed application envelope surrounds a complete graphical stream.
    // The stored application data is never read as drawing commands.
    sealed class WmfComments(int width,int height)
    {
        enum Envelope { None, Open, Payload, Closed }
        Envelope stage;
        internal ushort? Baseline {get;private set;}
        internal bool Complete=>stage is Envelope.None or Envelope.Closed;

        internal bool Accept(ReadOnlySpan<byte> body,bool hasText)
        {
            if(stage==Envelope.None&&Baseline is null&&body.Length==12&&body[..8].SequenceEqual("MathType"u8)&&U16(body,8)==0)
            {
                Baseline=U16(body,10);
                return true;
            }
            if(Baseline is null)return false;
            if(stage==Envelope.None&&!hasText&&body.Length==26&&U32(body,0)==uint.MaxValue&&U16(body,4)==0&&U32(body,6)==16)
            {
                int left=I32(body,10),top=I32(body,14);
                if(left < -width||left>0||top < -height||top>0||
                    I32(body,18)- (long)left!=width||I32(body,22)-(long)top!=height)return false;
                stage=Envelope.Open;
                return true;
            }
            if(stage==Envelope.Open&&hasText&&body.Length is >39 and <=4096&&body[..8].SequenceEqual("AppsMFCC"u8)&&
                U16(body,8)==1&&U32(body,10)==body.Length-39&&U32(body,14)==body.Length-39&&
                body.Slice(18,21).SequenceEqual("Design Science, Inc.\0"u8))
            {
                stage=Envelope.Payload;
                return true;
            }
            if(stage==Envelope.Payload&&body.Length==10&&U32(body,0)==uint.MaxValue&&U16(body,4)==1&&U32(body,6)==0)
            {
                stage=Envelope.Closed;
                return true;
            }
            return false;
        }
    }
}
