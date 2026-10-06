using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DRTAgX;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

// Exercise the production stack query and, optionally, the installed ARL postfix
// in an isolated process. No renderer or world/save initialization is needed.
internal static class HeldStackProbe
{
    private sealed class StackItem : Item
    {
        internal ItemStack? Seen;
        public override byte[] GetLightHsv(IBlockAccessor accessor, BlockPos pos, ItemStack stack = null!) {
            if (pos != null) throw new Exception("Held item queried as a placed block");
            Seen=stack;
            return new byte[] { 5,2,(byte)stack.Attributes.GetInt("level") };
        }
    }
    private sealed class StackBlock : Block
    {
        public override byte[] GetLightHsv(IBlockAccessor accessor, BlockPos pos, ItemStack stack = null!) {
            if (pos != null || stack == null) throw new Exception("Held block lost stack context");
            return LightHsv;
        }
    }
    internal static void Run()
    {
        Program.Check(MovingLightSources.HeldLightHsv(null!,null)==null,"empty hand has no light query");
        var item=new StackItem();var stack=new ItemStack(item);
        foreach(int level in new[]{0,9,21}) {
            stack.Attributes.SetInt("level",level);
            var hsv=MovingLightSources.HeldLightHsv(null!,stack);
            Program.Check(hsv is {Length:3} && hsv[2]==level && ReferenceEquals(item.Seen,stack),"held item retains per-instance light and null position, level="+level);
        }
        var block=new StackBlock { LightHsv=new byte[]{6,1,18} };
        Program.Check(MovingLightSources.HeldLightHsv(null!,new ItemStack(block)) is [6,1,18],"held block uses stack context without querying a world block");
    }
    internal static void RunInstalled(string libraryPath,bool before)
    {
        AssemblyLoadContext.Default.Resolving+=(context,name)=> {
            string root=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
            foreach(string dir in new[]{root,Path.Combine(root,"Lib"),Path.Combine(root,"Mods")}) {
                string path=Path.Combine(dir,name.Name+".dll");
                if(File.Exists(path))return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        var library=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(libraryPath));
        var postfix=library.GetType("AttributeRenderingLibrary.HarmonyPatches.Properties.CollectibleObject_GetLightHsv_Patch",true)!.GetMethod("Postfix")!;
        Program.Check(postfix.GetParameters()[0].ParameterType==typeof(Block),"installed ARL postfix has incompatible Block receiver on CollectibleObject target");
        var item=new Item { Code=new AssetLocation("ancientforestarmor:probe"),LightHsv=new byte[]{0,0,0} };
        var behavior=(CollectibleBehavior)Activator.CreateInstance(library.GetType("AttributeRenderingLibrary.CollectibleBehaviorShapeTexturesFromAttributes",true)!,item)!;
        item.CollectibleBehaviors=new[]{behavior};
        var stack=new ItemStack(item);
        // Supply a world object so the old path reaches the invalid Item->Block
        // virtual dispatch, rather than merely failing on an absent Core.Api.
        var api=DispatchProxy.Create<ICoreAPI,WorldProxy>();
        ((WorldProxy)(object)api).World=DispatchProxy.Create<IWorldAccessor,WorldProxy>();
        library.GetType("AttributeRenderingLibrary.Core",true)!.GetField("Api")!.SetValue(null,api);
        var harmony=new Harmony("drtagx.test.held-arl");
        var target=AccessTools.Method(typeof(CollectibleObject),"GetLightHsv");
        harmony.Patch(target,postfix:new HarmonyMethod(postfix));
        if(before) {
            Console.WriteLine("REPRO old DRT held-item query: non-null position with installed ARL postfix");
            item.GetLightHsv(null!,new BlockPos(0),stack);
            throw new Exception("Old unsafe query unexpectedly returned");
        }
        try {
            Program.Check(MovingLightSources.HeldLightHsv(null!,stack) is {Length:3} hsv && hsv[2]==0,"real ARL armor behavior does not crash or emit false light");
            item.LightHsv=new byte[]{5,2,17};
            Program.Check(MovingLightSources.HeldLightHsv(null!,stack) is [5,2,17],"real ARL postfix preserves base emission on an emitting held item");
            // ARL resolves variant colors through this dictionary, independently
            // of a placed-block position. Its variant API is invoked by reflection.
            var variants=Activator.CreateInstance(library.GetType("AttributeRenderingLibrary.Variants",true)!)!;
            variants.GetType().GetMethod("Set",new[]{typeof(string),typeof(string)})!.Invoke(variants,new object[]{"material","iron"});
            variants.GetType().GetMethod("ToStack")!.Invoke(variants,new object[]{stack});
            byte[] variantColor={12,3,19};
            behavior.GetType().GetProperty("LightHsvByType")!.SetValue(behavior,new Dictionary<string,byte[]>{{"*",variantColor}});
            Program.Check(ReferenceEquals(MovingLightSources.HeldLightHsv(null!,stack),variantColor),"real ARL per-stack variant emission survives safe held query");
        }
        finally { harmony.Unpatch(target,postfix); }
    }
    public class WorldProxy : DispatchProxy
    {
        internal IWorldAccessor? World;
        protected override object? Invoke(MethodInfo? method,object?[]? arguments)=>method?.Name=="get_World" ? World :
            method!.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
