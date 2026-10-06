using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Opt-in inventory of declared mod-owned targets, including unattached histories and arrays.</summary>
internal static class DeclaredFrameResources
{
    internal static void Write(ICoreClientAPI api, string directory)
    {
        var objects=new HashSet<object>(ReferenceEqualityComparer.Instance);
        var resources=new Dictionary<int,(TextureTarget Target,List<string> Owners)>();
        var rows=new List<object>();
        var errors=new List<string>();
        void Add(int id,TextureTarget target,string owner) {
            if(id<=0 || !GL.IsTexture(id)) return;
            if(resources.TryGetValue(id,out var prior)) {
                if(prior.Target!=target) errors.Add("Conflicting declared target for "+id);
                else prior.Owners.Add(owner);
            } else resources.Add(id,(target,new(){owner}));
        }
        void Framebuffer(FrameBufferRef buffer,string path) {
            if(buffer.Disposed)return;
            if(buffer.ColorTextureIds!=null) for(int i=0;i<buffer.ColorTextureIds.Length;++i) Add(buffer.ColorTextureIds[i],TextureTarget.Texture2D,path+"/color"+i);
            Add(buffer.DepthTextureId,TextureTarget.Texture2D,path+"/depth");
        }
        void Visit(object? owner,string path,int depth) {
            if(owner==null || depth>6 || !objects.Add(owner)) return;
            if(owner is FrameBufferRef framebuffer) {Framebuffer(framebuffer,path);return;}
            if(owner is FrameBufferRef[] buffers) {for(int i=0;i<buffers.Length;++i)Visit(buffers[i],path+"/"+i,depth+1);return;}
            var type=owner.GetType();string name=type.Name;
            if(type.Namespace?.StartsWith("DRTAgX",StringComparison.Ordinal)!=true && type.Namespace?.StartsWith("SheyderMod.Features",StringComparison.Ordinal)!=true)return;
            foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) {
                object? value=field.GetValue(owner);string key=field.Name,label=path+"/"+key;
                if(value is int id) {
                    // These names/targets are declared by owned source or the captured Sheyder ABI.
                    TextureTarget? target=name switch {
                        "HdrMipResources" when key is "Bloom" or "Meter" => TextureTarget.Texture2D,
                        "MovingLightShadowRenderer" when key=="_depthAtlas" => TextureTarget.Texture2D,
                        "StaticTerrainShadowMaps" when key=="_texture" => TextureTarget.Texture2DArray,
                        "VolumetricFilter" when key=="_texture" => TextureTarget.Texture2D,
                        "AtmosphereSkyResources" when key is "_trans" or "_multiple" or "<Previous>k__BackingField" or "<Current>k__BackingField" => TextureTarget.Texture2D,
                        "AtmosphereFogVolume" when key=="<Texture>k__BackingField" => TextureTarget.Texture3D,
                        "VolumetricFogRenderer" when key is "_scatterTexId" or "_depthTexId" => TextureTarget.Texture2D,
                        "WaterEnvCubemap" when key=="_texId" => TextureTarget.TextureCubeMap,
                        _ => null
                    };
                    if(target.HasValue)Add(id,target.Value,label);
                } else if(value is int[] ids && name=="HdrMipResources" && key is "Scene" or "Scratch" or "Exposure") {
                    for(int i=0;i<ids.Length;++i)Add(ids[i],TextureTarget.Texture2D,label+"/"+i);
                } else if(value is FrameBufferRef || value is FrameBufferRef[] || value?.GetType().Namespace is string ns &&
                    (ns.StartsWith("DRTAgX",StringComparison.Ordinal) || ns.StartsWith("SheyderMod.Features",StringComparison.Ordinal))) Visit(value,label,depth+1);
            }
        }
        for(int i=0;i<api.Render.FrameBuffers.Count;++i)Visit(api.Render.FrameBuffers[i],"native/"+i,0);
        Visit(api.ModLoader.GetModSystem("DRTAgX.DrtagxModSystem"),"DRTAgX",0);
        object? sheyder=api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if(sheyder!=null && AccessTools.Field(sheyder.GetType(),"_renderers")?.GetValue(sheyder) is IEnumerable renderers)
            foreach(object renderer in renderers)Visit(renderer,renderer.GetType().FullName!,0);
        // The captured provider keeps SSR outside its common renderer list, including its histories/cubemap.
        if(sheyder!=null)Visit(AccessTools.Field(sheyder.GetType(),"_waterSsr")?.GetValue(sheyder),"SheyderWaterSSR",0);
        int active=GL.GetInteger(GetPName.ActiveTexture);GL.ActiveTexture(TextureUnit.Texture0);
        var bindings=new Dictionary<TextureTarget,int>();
        long total=0;
        try {
            foreach(var resource in resources) {
                int id=resource.Key;var target=resource.Value.Target;
                if(!bindings.ContainsKey(target))bindings.Add(target,GL.GetInteger(target switch {
                    TextureTarget.Texture2DArray=>GetPName.TextureBinding2DArray,TextureTarget.Texture3D=>GetPName.TextureBinding3D,
                    TextureTarget.TextureCubeMap=>GetPName.TextureBindingCubeMap,_=>GetPName.TextureBinding2D
                }));
                GL.BindTexture(target,id);
                var levelTarget=target==TextureTarget.TextureCubeMap?TextureTarget.TextureCubeMapPositiveX:target;
                int Param(GetTextureParameter p) {GL.GetTexLevelParameter(levelTarget,0,p,out int value);return value;}
                int w=Param(GetTextureParameter.TextureWidth),h=Param(GetTextureParameter.TextureHeight);
                int layers=target is TextureTarget.Texture3D or TextureTarget.Texture2DArray?Param(GetTextureParameter.TextureDepth):target==TextureTarget.TextureCubeMap?6:1;
                int r=Param(GetTextureParameter.TextureRedSize),g=Param(GetTextureParameter.TextureGreenSize),b=Param(GetTextureParameter.TextureBlueSize),a=Param(GetTextureParameter.TextureAlphaSize),d=Param(GetTextureParameter.TextureDepthSize);
                long bytes=(long)w*h*layers*(r+g+b+a+d)/8;total+=bytes;
                GL.GetTexParameter(target,GetTextureParameter.TextureMaxLevel,out int maxLevel);
                int allocated=0;
                for(int level=0;level<=Math.Min(maxLevel,31);++level) {GL.GetTexLevelParameter(levelTarget,level,GetTextureParameter.TextureWidth,out int lw);if(lw==0)break;++allocated;}
                rows.Add(new{Id=id,Target=target.ToString(),resource.Value.Owners,Width=w,Height=h,Layers=layers,Samples=1,
                    Format=Param(GetTextureParameter.TextureInternalFormat),R=r,G=g,B=b,A=a,Depth=d,MaxSampleLevel=maxLevel,AllocatedLevels=allocated,LogicalBaseBytes=bytes});
                var error=GL.GetError();if(error!=ErrorCode.NoError)errors.Add(id+": "+error);
            }
        } finally {foreach(var pair in bindings)GL.BindTexture(pair.Key,pair.Value);GL.ActiveTexture((TextureUnit)active);}
        // GL IDs are deduplicated within this snapshot; the directory/time is its observed generation.
        File.WriteAllText(Path.Combine(directory,"declared-resources.json"),JsonConvert.SerializeObject(new{
            Utc=DateTime.UtcNow,Textures=rows,LogicalBaseBytes=total,Errors=errors,
            Notes="Logical channel storage, not driver allocation. Physical FBO attachment aliases are in resource-inventory.json. Unknown targets/owners remain excluded; this is not a complete native ownership contract."
        },Formatting.Indented));
    }
}
