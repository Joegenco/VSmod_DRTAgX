using System;
using System.IO;
using System.Reflection;
using DRTAgX;
using Vintagestory.API.MathTools;

internal static class Program
{
    internal static int Checks;
    internal static void Check(bool value,string label)
    {
        if(!value)throw new Exception(label);++Checks;Console.WriteLine("PASS "+label);
    }
    internal static void Main(string[] args)
    {
        if(args.Length>1 && args[0].StartsWith("--held-arl",StringComparison.Ordinal)) {
            HeldStackProbe.RunInstalled(args[1],args[0]=="--held-arl-before"); return;
        }
        HeldStackProbe.Run();
        QualityPolicyProbe.Run();
        float[] p={0,0,0},dim={1,1,1},bright={10,10,10};
        Check(MovingLightShadowRenderer.Reach(bright)>17&&MovingLightShadowRenderer.Reach(new float[]{100,100,100})==48,"shadow reach retains authored reach and 48-block ceiling");
        int divisor=MovingLightShadowRenderer.ResolutionDivisor;
        Check(MovingLightShadowRenderer.FaceSizeForDimensions(1920,1080)==192/divisor&&MovingLightShadowRenderer.FaceSizeForDimensions(3840,2160)==384/divisor,"actual render resolution scales cube and held maps from 1080p to 4K with reversible divisor");
        Check(MovingLightShadowRenderer.FaceSizeForDimensions(1440,810)==144/divisor&&MovingLightShadowRenderer.FaceSizeForDimensions(960,540)==96/divisor&&MovingLightShadowRenderer.FaceSizeForDimensions(480,270)==48/divisor,"final output dimensions alone determine dynamic map size");
        Check(MovingLightShadowRenderer.FaceSizeForDimensions(0,0)==192/divisor&&MovingLightShadowRenderer.FaceSizeForDimensions(16000,16000)==768/divisor&&MovingLightShadowRenderer.FaceSizeForDimensions(1921,1080)==208/divisor,"allocation bounds and quantized resize steps remain defined");
        Check(MovingLightShadowRenderer.Outranks(1,MovingLightKind.LeftHand,100000,MovingLightKind.Api),"held priority is independent of API source strength");
        Check(MovingLightShadowRenderer.Outranks(2,MovingLightKind.LeftHand,1,MovingLightKind.RightHand),"stronger left hand wins budget one");
        Check(MovingLightShadowRenderer.Outranks(1,MovingLightKind.RightHand,1,MovingLightKind.LeftHand),"right hand wins equal strength");
        Check(!MovingLightShadowRenderer.Outranks(1,MovingLightKind.LeftHand,1,MovingLightKind.RightHand),"tie priority is independent of source ordering");
        Check(TerrainCasterBatch.IntersectsFace(0,.13f,0,.1f,0),"sphere intersecting oblique face plane is retained");
        var rng=new Random(713);
        for(int face=0;face<6;++face)
        {
            bool conservative=true;
            for(int i=0;i<2000;++i)
            {
                float x=(float)rng.NextDouble()*10-5,y=(float)rng.NextDouble()*10-5,z=(float)rng.NextDouble()*10-5;
                float r=(float)rng.NextDouble();
                for(int j=0;j<30;++j)
                {
                    float dx=(float)rng.NextDouble()*2-1,dy=(float)rng.NextDouble()*2-1,dz=(float)rng.NextDouble()*2-1;
                    if(dx*dx+dy*dy+dz*dz>1)continue;
                    if(TerrainCasterBatch.IntersectsFace(x+r*dx,y+r*dy,z+r*dz,0,face)&&!TerrainCasterBatch.IntersectsFace(x,y,z,r,face))conservative=false;
                }
            }
            Check(conservative,"conservative caster sphere culling face "+face);
        }
        foreach(float fov in new[]{45f,90f,130f})foreach(float aspect in new[]{.75f,16f/9f,3.5f})foreach(float side in new[]{-.4f,.4f})
        {
            float[] camera=new float[16];Mat4f.Perspective(camera,fov*MathF.PI/180,aspect,.05f,128f);
            float[] light={side,-.2f,0},matrix=new float[16];HeldProjectionFit.Fallback(camera,light,22,matrix,0);
            bool covered=true;
            foreach(float depth in new[]{.05f,.1f,1f,10f,22f})foreach(int x in new[]{-1,1})foreach(int y in new[]{-1,1})
            {
                float ex=depth*(x+camera[8])/camera[0],ey=depth*(y+camera[9])/camera[5];
                float cx=matrix[0]*ex+matrix[8]*-depth+matrix[12],cy=matrix[5]*ey+matrix[9]*-depth+matrix[13];
                if(Math.Abs(cx)>depth||Math.Abs(cy)>depth)covered=false;
            }
            Check(covered,$"fallback coverage fov={fov} aspect={aspect:F2} hand={side}");
        }
        // Exercise real native field layouts/delegates without constructing a game.
        var pool=(Vintagestory.API.Client.MeshDataPool)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.API.Client.MeshDataPool));
        typeof(Vintagestory.API.Client.MeshDataPool).GetField("poolLocations",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(pool,new System.Collections.Generic.List<Vintagestory.API.Client.ModelDataPoolLocation>());
        Check(NativeShadowFields.Locations(pool)!=null,"typed native pool-location accessor matches installed ABI");
        CheckCasterRestoration(pool);
        if(args.Length>0&&args[0]=="--gpu")GpuChecks.Run(Path.GetFullPath(args[1]));
        if(args.Length>0&&args[0]=="--benchmark")MovingBenchmark.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));
        Console.WriteLine($"Moving-light checks: {Checks} PASS");
    }
    private static void CheckCasterRestoration(Vintagestory.API.Client.MeshDataPool pool)
    {
        static object Empty(Type t)=>System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        var manager=(Vintagestory.API.Client.MeshDataPoolManager)Empty(typeof(Vintagestory.API.Client.MeshDataPoolManager));
        typeof(Vintagestory.API.Client.MeshDataPoolManager).GetField("pools",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(manager,new System.Collections.Generic.List<Vintagestory.API.Client.MeshDataPool>{pool});
        var locations=NativeShadowFields.Locations(pool)!;
        foreach(bool hidden in new[]{false,true})
        {
            var location=(Vintagestory.API.Client.ModelDataPoolLocation)Empty(typeof(Vintagestory.API.Client.ModelDataPoolLocation));location.Hide=hidden;
            var sphere=Empty(typeof(Sphere));
            foreach(string name in new[]{"x","y","z","radius","radiusY","radiusZ"})
            {var field=typeof(Sphere).GetField(name)!;field.SetValue(sphere,Convert.ChangeType(name.StartsWith("radius")?1:0,field.FieldType));}
            typeof(Vintagestory.API.Client.ModelDataPoolLocation).GetField("FrustumCullSphere")!.SetValue(location,sphere);locations.Add(location);
        }
        int[] starts={4,12},sizes={6,9};pool.indicesStartsByte=starts;pool.indicesSizes=sizes;pool.indicesGroupsCount=2;
        var passes=new Vintagestory.API.Client.MeshDataPoolManager[6][];passes[0]=new[]{manager};
        var batch=new TerrainCasterBatch();batch.Begin(passes,new[]{1});
        starts[0]=999;sizes[1]=777;pool.indicesStartsByte=new[]{99};pool.indicesSizes=new[]{98};pool.indicesGroupsCount=1;
        // Every caster is outside this light's reach, so no native GL draw runs.
        batch.Prepare(new Vec3d(),new Vec3d(10000,0,0),1,null);Check(batch.Draw(0)==0,"empty caster classification skips native terrain draw");
        Check(ReferenceEquals(pool.indicesStartsByte,starts)&&ReferenceEquals(pool.indicesSizes,sizes)&&starts[0]==4&&sizes[1]==9&&pool.indicesGroupsCount==2,"caster batch restores native draw-array identities, contents and count");
        Check(!locations[0].Hide&&locations[1].Hide,"caster batch restores mixed original Hide flags exactly");batch.End();
    }
}
