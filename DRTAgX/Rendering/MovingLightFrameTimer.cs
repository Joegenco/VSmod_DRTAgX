using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

// Four timestamps per sample: prepare/draw start/end and relight start/end.
// Poll only completed slots. No GPU wait/readback exists in shipping frames.
internal sealed class MovingLightFrameTimer : IDisposable
{
    private readonly int[] _queries=new int[32];
    private readonly bool[] _pending=new bool[8];
    private readonly double[] _samples=new double[128],_sorted=new double[128];
    private int _next,_current=-1,_count,_sample;
    internal void BeginShadows(bool enabled)
    {
        _current=-1;if(!enabled)return;Poll();int slot=_next;if(_pending[slot])return;
        if(_queries[slot*4]==0)for(int j=0;j<4;++j)_queries[slot*4+j]=GL.GenQuery();
        _current=slot;_next=(slot+1)%8;GL.QueryCounter(_queries[slot*4],QueryCounterTarget.Timestamp);
    }
    internal void EndShadows(){if(_current>=0)GL.QueryCounter(_queries[_current*4+1],QueryCounterTarget.Timestamp);}
    internal void BeginRelight(){if(_current>=0)GL.QueryCounter(_queries[_current*4+2],QueryCounterTarget.Timestamp);}
    internal void EndRelight(){if(_current<0)return;GL.QueryCounter(_queries[_current*4+3],QueryCounterTarget.Timestamp);_pending[_current]=true;_current=-1;}
    internal void Poll()
    {
        for(int i=0;i<8;++i)
        {
            if(!_pending[i])continue;
            GL.GetQueryObject(_queries[i*4+3],GetQueryObjectParam.QueryResultAvailable,out int available);if(available==0)continue;
            GL.GetQueryObject(_queries[i*4],GetQueryObjectParam.QueryResult,out long a);GL.GetQueryObject(_queries[i*4+1],GetQueryObjectParam.QueryResult,out long b);
            GL.GetQueryObject(_queries[i*4+2],GetQueryObjectParam.QueryResult,out long c);GL.GetQueryObject(_queries[i*4+3],GetQueryObjectParam.QueryResult,out long d);
            _samples[_sample]=Math.Max(0,b-a+d-c)/1_000_000.0;_sample=(_sample+1)%128;_count=Math.Min(128,_count+1);_pending[i]=false;
        }
    }
    internal (double Median,double P95) Statistics(){if(_count==0)return(0,0);Array.Copy(_samples,_sorted,_count);Array.Sort(_sorted,0,_count);return(_sorted[_count/2],_sorted[Math.Min(_count-1,(int)Math.Ceiling(_count*.95)-1)]);}
    internal void Clear(){_count=_sample=0;_current=-1;}
    public void Dispose(){for(int i=0;i<_queries.Length;++i){if(_queries[i]!=0)GL.DeleteQuery(_queries[i]);_queries[i]=0;}Array.Clear(_pending);Clear();}
}
