using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

internal enum MovingLightKind { Entity, Api, RightHand, LeftHand }

// Owns primitive Before-stage snapshots, never native light records. Stable hand
// identity replaces positional guessing, so a nearby dropped torch stays independent.
internal sealed class MovingLightSources
{
    private readonly record struct Source(long Id, MovingLightKind Kind, double X, double Y, double Z, float R, float G, float B);
    private readonly Source[] _sources = new Source[100];
    private readonly ConditionalWeakTable<IPointLight, ApiId> _ids = new();
    private sealed class ApiId(long value) { internal readonly long Value = value; }
    private long _nextId;
    private int _count;
    private Vec3f _rgb = new();
    private readonly ConditionalWeakTable<IPointLight, ApiId>.CreateValueCallback _createId;
    private readonly double[] _inverse = new double[16];
    private object? _world;
    private SystemRenderPlayerEffects? _effects;
    private static readonly System.Func<ClientMain, ClientSystem[]?> Systems = NativeShadowFields.Reader<ClientMain, ClientSystem[]>("clientSystems");
    private static readonly System.Func<SystemRenderPlayerEffects, int> Limit = NativeShadowFields.Reader<SystemRenderPlayerEffects, int>("maxDynLights");
    private static readonly System.Func<ClientMain, List<IPointLight>?> ApiLights = NativeShadowFields.Reader<ClientMain, List<IPointLight>>("pointlights");
    private static readonly System.Func<EntityPlayer, byte[]?> BaseLight = NativeShadowFields.Reader<EntityPlayer, byte[]>("baseLightHsv");
    internal bool Available { get; private set; }
    internal int NativeLimit { get; private set; }
    internal readonly MovingLightKind[] Kinds = new MovingLightKind[16];
    internal readonly long[] Identities = new long[16];
    internal bool FirstPerson { get; private set; }
    internal MovingLightSources() => _createId = NewApiId;
    internal void Reset(){Available=false;_count=0;_world=null;_effects=null;_ids.Clear();Array.Clear(Identities);}

    internal void Capture(ICoreClientAPI api, ClientMain world)
    {
        _count=0; Available=false;
        if (!ReferenceEquals(_world,world))
        {
            _world=world; _effects=null; _ids.Clear(); _nextId=0;
            if (Systems(world) is { } systems) foreach(var s in systems) if(s is SystemRenderPlayerEffects effects) { _effects=effects; break; }
        }
        if (_effects == null || ApiLights(world) is not { } apiLights) return;
        NativeLimit=Math.Clamp(Limit(_effects),0,100);
        var player=api.World.Player.Entity;
        Mat4d.Invert(_inverse,world.CurrentModelViewMatrixd);
        bool right=AddHand(api,world,player.RightHandItemSlot,MovingLightKind.RightHand,player.EntityId);
        bool left=AddHand(api,world,player.LeftHandItemSlot,MovingLightKind.LeftHand,player.EntityId);
        foreach(var entity in api.World.LoadedEntities.Values)
        {
            // Replace the player's combined hand record exactly once. Preserve
            // intrinsic player light separately when the native base field emits.
            byte[]? hsv=entity.EntityId==player.EntityId && (right||left) ? BaseLight(player) : entity.LightHsv;
            if (hsv is not { Length: >= 3 } || hsv[2]==0) continue;
            double x=entity.Pos.X,y=entity.Pos.InternalY,z=entity.Pos.Z;
            double dx=x-_inverse[12],dy=y-_inverse[13],dz=z-_inverse[14];
            if(dx*dx+dy*dy+dz*dz>120.0*120.0)continue;
            if(Convert(world,api,hsv)) Add(new Source(entity.EntityId,MovingLightKind.Entity,x,y,z,_rgb.X,_rgb.Y,_rgb.Z));
        }
        foreach(var light in apiLights)
        {
            var p=light.Pos; var c=light.Color;
            if(p==null||c==null)continue;
            var id=_ids.GetValue(light, _createId);
            // Native Vec3f light publication uses Z,Y,X, like its HSV path.
            Add(new Source(id.Value,MovingLightKind.Api,p.X,p.Y,p.Z,c.Z,c.Y,c.X));
        }
        Available=true;
    }

    private ApiId NewApiId(IPointLight _) => new(++_nextId);
    private void Add(Source s) { if(_count<_sources.Length)_sources[_count++]=s; }
    private bool AddHand(ICoreClientAPI api,ClientMain world,ItemSlot? slot,MovingLightKind kind,long playerId)
    {
        var stack=slot?.Itemstack;
        if(stack==null)return false;
        try
        {
            var hsv=HeldLightHsv(api.World.BlockAccessor,stack);
            if(hsv is not { Length: >= 3 }||hsv[2]==0||!Convert(world,api,hsv))return false;
            Add(new Source(playerId,kind,0,0,0,_rgb.X,_rgb.Y,_rgb.Z)); return true;
        }
        catch { return false; } // Recoverable collectible errors omit this source.
    }

    internal static byte[]? HeldLightHsv(IBlockAccessor accessor,ItemStack? stack)
    {
        // A held stack is not a placed block at the player's coordinates. The
        // API explicitly allows null pos for stack queries; retain the stack
        // so per-instance emission/behaviors still resolve normally. Supplying
        // a position also sends ARL's Block-typed postfix down a block-interface
        // path for Item receivers, which can terminate the CLR before any catch.
        return stack?.Collectible?.GetLightHsv(accessor,null,stack);
    }
    private bool Convert(ClientMain world,ICoreClientAPI api,byte[] hsv)
    {
        var hue=world.WorldMap.hueLevels; var sat=world.WorldMap.satLevels; var levels=api.World.BlockLightLevels;
        int n=hsv[2];
        if(hsv[0]>=hue.Length||hsv[1]>=sat.Length||n>=levels.Length)return false;
        int rgba=ColorUtil.HsvToRgba(hue[hsv[0]],sat[hsv[1]],(int)(levels[n]*255f));
        ColorUtil.ToRGBVec3f(rgba,ref _rgb);
        float r=_rgb.Z*n,g=_rgb.Y*n,b=_rgb.X*n;
        _rgb.Set(r,g,b); return float.IsFinite(r+g+b)&&r+g+b>0;
    }

    internal int Publish(ICoreClientAPI api,double[] view,float[][] positions,float[][] colors)
    {
        FirstPerson=api.World.Player.CameraMode==EnumCameraMode.FirstPerson;
        int count=Math.Min(_count,Math.Min(16,NativeLimit));
        var player=api.World.Player.Entity;
        for(int i=0;i<count;++i)
        {
            var s=_sources[i]; var p=positions[i]; var c=colors[i]; Kinds[i]=s.Kind; Identities[i]=s.Id;
            bool held=s.Kind is MovingLightKind.RightHand or MovingLightKind.LeftHand;
            float side=s.Kind==MovingLightKind.LeftHand ? -.4f : .4f;
            if(held&&FirstPerson) { p[0]=side; p[1]=-.2f; p[2]=0; }
            else
            {
                double x=s.X,y=s.Y+.3,z=s.Z;
                if(held)
                {
                    // Body yaw, not camera yaw: third-person orbiting must not
                    // rotate the light to the other side of the player's body.
                    x=player.Pos.X+Math.Cos(player.BodyYaw)*side;
                    y=player.Pos.InternalY+1.5;
                    z=player.Pos.Z-Math.Sin(player.BodyYaw)*side;
                }
                p[0]=(float)(view[0]*x+view[4]*y+view[8]*z+view[12]);
                p[1]=(float)(view[1]*x+view[5]*y+view[9]*z+view[13]);
                p[2]=(float)(view[2]*x+view[6]*y+view[10]*z+view[14]);
            }
            c[0]=s.R;c[1]=s.G;c[2]=s.B;
        }
        return count;
    }
}

