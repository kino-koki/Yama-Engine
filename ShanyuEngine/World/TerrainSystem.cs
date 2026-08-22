using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.World;

public struct TerrainConfig
{
    public int Resolution = 513; public float Size = 2000, MaxHeight = 500;
    public int SplatmapRes = 256; public int ControlTexCount = 4;
    public int DetailRes = 1024; public float DetailDist = 200, GrassDist = 120, TreeDist = 2000;
    public int MaxTrees = 5000, MaxGrass = 10000;
    public float LOD0 = 100, LOD1 = 200, LOD2 = 500, LOD3 = 1000;
    public bool EnablePhysics = true, EnableGrass = true, EnableTrees = true;
    public TerrainConfig() { }
}

public sealed class TerrainHeightmap
{
    private readonly int _res; private readonly float[,] _h; private readonly float _size, _maxH;
    public int Res => _res; public float Size => _size;
    public TerrainHeightmap(int r, float s, float mh) { _res = r; _size = s; _maxH = mh; _h = new float[r, r]; }
    public float GetH(int x, int y) => _h[Math.Clamp(x,0,_res-1), Math.Clamp(y,0,_res-1)];
    public void SetH(int x, int y, float v) { _h[x,y] = Math.Clamp(v,0,_maxH); }

    public float Sample(Vector3 w)
    {
        float hf = _size*0.5f, u=(w.X+hf)/_size, v=(w.Z+hf)/_size;
        if (u<0||u>1||v<0||v>1) return 0;
        float fu=u*(_res-1), fv=v*(_res-1); int x0=(int)fu, y0=(int)fv, x1=Math.Min(x0+1,_res-1), y1=Math.Min(y0+1,_res-1);
        float fx=fu-x0,fy=fv-y0, h00=_h[x0,y0],h10=_h[x1,y0],h01=_h[x0,y1],h11=_h[x1,y1];
        return (h00*(1-fx)+h10*fx)*(1-fy)+(h01*(1-fx)+h11*fx)*fy;
    }

    public Vector3 Normal(Vector3 w)
    {
        float s = 1; float hL=Sample(w-new(1,0,0)),hR=Sample(w+new(1,0,0)),hD=Sample(w-new(0,0,1)),hU=Sample(w+new(0,0,1));
        return Vector3.Normalize(new(hL-hR,2*s,hD-hU));
    }

    public void GenerateFlat() { Array.Clear(_h,0,_h.Length); }

    public void GeneratePerlin(float amp=50, float freq=0.005f, int octs=4)
    {
        for (int x=0;x<_res;x++) for (int y=0;y<_res;y++)
        {
            float h=0,a=amp,f=freq,m=0;
            for (int o=0;o<octs;o++){h+=a*Noise(x*f,y*f);m+=a;a*=0.5f;f*=2;}
            _h[x,y]=Math.Clamp(h/m*amp*0.5f+amp*0.5f,0,_maxH);
        }
    }

    public void AddHill(int cx,int cy,float r,float h)
    {
        for (int x=Math.Max(0,cx-(int)r);x<=Math.Min(_res-1,cx+(int)r);x++)
            for (int y=Math.Max(0,cy-(int)r);y<=Math.Min(_res-1,cy+(int)r);y++)
            { float d=MathF.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy)); if (d<r) _h[x,y]+=h*(1-d/r); }
    }

    public void Erode(int iters=500, float rain=0.01f)
    {
        var rng=new Random(42);
        for (int i=0;i<iters;i++)
        {
            float x=(float)rng.NextDouble()*(_res-1),y=(float)rng.NextDouble()*(_res-1),w=rain,s=0,vx=0,vy=0;
            for (int st=0;st<30&&x>0&&x<_res-1&&y>0&&y<_res-1;st++)
            {
                int xi=(int)x,yi=(int)y;
                float gx=_h[Math.Min(xi+1,_res-1),yi]-_h[Math.Max(xi-1,0),yi];
                float gy=_h[xi,Math.Min(yi+1,_res-1)]-_h[xi,Math.Max(yi-1,0)];
                vx=vx*0.9f+gx*0.1f;vy=vy*0.9f+gy*0.1f;
                float len=MathF.Sqrt(vx*vx+vy*vy);
                if(len>0.001f){x-=vx/len;y-=vy/len;}
                float ms=w*len*0.01f;if(s>ms){_h[xi,yi]+=(s-ms)*0.5f;s=ms;}else s+=0.001f;
                w*=0.99f;
            }
        }
    }

    private static float Noise(float x,float y)
    {
        int ix=(int)MathF.Floor(x),iy=(int)MathF.Floor(y);
        float fx=x-ix,fy=y-iy;fx=fx*fx*(3-2*fx);fy=fy*fy*(3-2*fy);
        float n00=G(ix,iy,x-ix,y-iy),n10=G(ix+1,iy,x-(ix+1),y-iy),n01=G(ix,iy+1,x-ix,y-(iy+1)),n11=G(ix+1,iy+1,x-(ix+1),y-(iy+1));
        return n00*(1-fx)*(1-fy)+n10*fx*(1-fy)+n01*(1-fx)*fy+n11*fx*fy;
    }

    private static float G(int x,int y,float dx,float dy)
    {
        int h=x*374761393+y*668265263;h=(h^(h>>13))*1274126177;
        float a=(h&0x7FFFFFFF)/(float)int.MaxValue*MathF.PI*2;
        return MathF.Cos(a)*dx+MathF.Sin(a)*dy;
    }
}

public sealed class TerrainSplatmap
{
    private readonly float[,,] _d;private readonly int _r,_l;
    public TerrainSplatmap(int r,int l){_r=r;_l=l;_d=new float[r,r,l];}
    public float Get(int x,int y,int l)=>_d[x,y,Math.Clamp(l,0,_l-1)];
    public void Set(int x,int y,int l,float w){_d[x,y,l]=Math.Clamp(w,0,1);}

    public float[] Sample(Vector3 w,float size)
    {
        float h=size*0.5f,u=(w.X+h)/size,v=(w.Z+h)/size;
        if(u<0||u>1||v<0||v>1)return new float[_l];
        float fu=u*(_r-1),fv=v*(_r-1);int x0=(int)fu,y0=(int)fv,x1=Math.Min(x0+1,_r-1),y1=Math.Min(y0+1,_r-1);
        float fx=fu-x0,fy=fv-y0;var r=new float[_l];
        for(int l=0;l<_l;l++)r[l]=_d[x0,y0,l]*(1-fx)*(1-fy)+_d[x1,y0,l]*fx*(1-fy)+_d[x0,y1,l]*(1-fx)*fy+_d[x1,y1,l]*fx*fy;
        return r;
    }

    public void Paint(int cx,int cy,float r,int l,float s)
    {
        for(int x=Math.Max(0,cx-(int)r);x<=Math.Min(_r-1,cx+(int)r);x++)
            for(int y=Math.Max(0,cy-(int)r);y<=Math.Min(_r-1,cy+(int)r);y++)
            {
                float d=MathF.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy));
                if(d<r){float f=1-d/r;float rem=1;for(int l2=0;l2<_l;l2++){if(l2==l)_d[x,y,l2]=Math.Min(1,_d[x,y,l2]+s*f);else _d[x,y,l2]*=(1-s*f*0.5f);rem-=_d[x,y,l2];}}
            }
    }
}

public sealed class TerrainGrass
{
    public struct Patch{public Vector3 P;public float R,V;public int I;}
    private readonly List<Patch> _p=new();private readonly Random _r=new(42);
    public int Count=>_p.Count;

    public void Gen(TerrainHeightmap hm,float sp=2f,float j=0.5f,int max=10000)
    {
        _p.Clear();float hf=hm.Size*0.5f;
        for(float x=-hf;x<hf&&_p.Count<max;x+=sp)for(float z=-hf;z<hf&&_p.Count<max;z+=sp)
        {float jx=(float)(_r.NextDouble()-0.5)*j,jz=(float)(_r.NextDouble()-0.5)*j;var pos=new Vector3(x+jx,0,z+jz);pos.Y=hm.Sample(pos);if(pos.Y>0.1f)_p.Add(new Patch{P=pos,R=(float)(_r.NextDouble()*MathF.PI*2),V=_r.Next(4),I=_r.Next(3)});}
    }

    public Patch[] Visible(Vector3 cam,float md)
    {float ms=md*md;var v=new List<Patch>();foreach(var p in _p)if(Vector3.DistanceSquared(p.P,cam)<ms)v.Add(p);return v.ToArray();}
}

public sealed class TerrainTrees
{
    public struct T{public Vector3 P;public float S,R;public int I;}
    private readonly List<T> _t=new();private readonly Random _r=new(42);
    public int Count=>_t.Count;

    public void Gen(TerrainHeightmap hm,int c,float mn=1,float mx=450,int max=5000)
    {
        _t.Clear();float hf=hm.Size*0.5f;
        for(int i=0;i<c&&_t.Count<max;i++)
        {float x=(float)(_r.NextDouble()-0.5)*hm.Size,z=(float)(_r.NextDouble()-0.5)*hm.Size;var p=new Vector3(x,0,z);p.Y=hm.Sample(p);if(p.Y<mn||p.Y>mx)continue;_t.Add(new T{P=p,S=(float)(_r.NextDouble()*0.5f+0.75f),R=(float)_r.NextDouble()*MathF.PI*2,I=_r.Next(3)});}
    }

    public T[] Visible(Vector3 cam,float md){float ms=md*md;var v=new List<T>();foreach(var t in _t)if(Vector3.DistanceSquared(t.P,cam)<ms)v.Add(t);return v.ToArray();}
}

public sealed class TerrainWater
{
    public float Level=0;public Vector2 FlowDir=new(1,0);public float FlowSpeed=1;
    public float WaveAmplitude=0.5f,WaveFrequency=0.02f,WaveSpeed=0.5f;
    public Vector4 ShallowColor=new(0.2f,0.5f,0.7f,1),DeepColor=new(0,0.1f,0.2f,1);
    public float Clarity=5f,ReflectionIntensity=0.3f;

    public float WaveHeight(Vector3 pos,float time)
    {
        float h=0;
        h+=MathF.Sin(pos.X*WaveFrequency+time*WaveSpeed)*WaveAmplitude;
        h+=MathF.Sin(pos.X*WaveFrequency*1.3f+pos.Z*WaveFrequency*0.7f+time*WaveSpeed*0.8f)*WaveAmplitude*0.6f;
        h+=MathF.Sin(pos.Z*WaveFrequency*1.7f+time*WaveSpeed*1.2f)*WaveAmplitude*0.4f;
        return h;
    }

    public Vector3 WaveNormal(Vector3 pos,float time,float eps=0.5f)
    {
        float hx=WaveHeight(pos+new Vector3(eps,0,0),time)-WaveHeight(pos-new Vector3(eps,0,0),time);
        float hz=WaveHeight(pos+new Vector3(0,0,eps),time)-WaveHeight(pos-new Vector3(0,0,eps),time);
        return Vector3.Normalize(new(-hx/(2*eps),1,-hz/(2*eps)));
    }
}

public sealed class TerrainDebugger
{
    public struct DebugInfo
    {
        public Vector3 Pos;public float Height,NormalX,NormalY,NormalZ,Slope;
        public float[] SplatWeights;public bool Underwater;
    }

    public DebugInfo SampleAt(TerrainHeightmap hm,TerrainSplatmap? sm,TerrainWater? w,Vector3 pos)
    {
        float h=hm.Sample(pos);var n=hm.Normal(pos);
        float slope=MathF.Acos(Math.Clamp(n.Y,-1,1))*(180f/MathF.PI);
        return new DebugInfo{Pos=pos,Height=h,NormalX=n.X,NormalY=n.Y,NormalZ=n.Z,Slope=slope,SplatWeights=sm?.Sample(pos,hm.Size),Underwater=w!=null&&h<w.Level};
    }

    public string GetProfile(TerrainHeightmap hm)
    {
        float mn=float.MaxValue,mx=float.MinValue,avg=0;
        for(int x=0;x<hm.Res;x++)for(int y=0;y<hm.Res;y++){float h=hm.GetH(x,y);mn=MathF.Min(mn,h);mx=MathF.Max(mx,h);avg+=h;}
        avg/=hm.Res*hm.Res;
        return $"Terrain: {hm.Res}x{hm.Res}, Size:{hm.Size}, H range:{mn:F1}-{mx:F1}, Avg:{avg:F1}";
    }
}

public sealed class TerrainCollider
{
    private readonly TerrainHeightmap _hm;
    public TerrainCollider(TerrainHeightmap hm){_hm=hm;}

    public bool Raycast(Vector3 origin,Vector3 dir,out float t,out Vector3 hitPoint)
    {
        t=0;hitPoint=default;float hf=_hm.Size*0.5f;float step=10;
        for(int i=0;i<500;i++)
        {
            var p=origin+dir*t;if(MathF.Abs(p.X)>hf||MathF.Abs(p.Z)>hf)return false;
            float th=_hm.Sample(p);if(p.Y<=th){hitPoint=new(p.X,th,p.Z);return true;}
            t+=step;
        }
        return false;
    }

    public bool Sample(Vector3 pos,out float h,out Vector3 normal)
    {
        h=_hm.Sample(pos);normal=_hm.Normal(pos);
        return pos.Y<=h+0.1f;
    }
}
