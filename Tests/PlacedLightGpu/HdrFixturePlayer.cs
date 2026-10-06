using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>
/// Implement the player contract, including its nonpublic abstract member.
/// DispatchProxy cannot override that member from its generated assembly.
/// The fixture exposes one entity and never starts a real player or world.
/// </summary>
internal static class HdrFixturePlayer
{
    internal static IClientPlayer Create(EntityPlayer entity)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("HdrFixturePlayer"), AssemblyBuilderAccess.Run);
        // Access is granted only to this in-memory test adapter, never to the mod.
        var access = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)]);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(access, [typeof(IClientPlayer).Assembly.GetName().Name]));
        var module = assembly.DefineDynamicModule("player");
        var type = module.DefineType("HdrFixturePlayerInstance", TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(typeof(IClientPlayer));
        var field = type.DefineField("Entity", typeof(EntityPlayer), FieldAttributes.Public);
        var interfaces = typeof(IClientPlayer).GetInterfaces().Append(typeof(IClientPlayer));
        int ordinal = 0;
        foreach (var method in interfaces.SelectMany(i => i.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).Where(m => m.IsAbstract)) {
            if (method.IsGenericMethodDefinition) throw new NotSupportedException("Generic fixture player member: " + method.Name);
            var implementation = type.DefineMethod("member" + ordinal++, MethodAttributes.Public | MethodAttributes.Virtual |
                MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());
            var il = implementation.GetILGenerator();
            if (method.Name == "get_Entity") {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            }
            else {
                il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Throw);
            }
            type.DefineMethodOverride(implementation, method);
        }
        var instance = Activator.CreateInstance(type.CreateType());
        instance.GetType().GetField("Entity").SetValue(instance, entity);
        return (IClientPlayer)instance;
    }
}

namespace System.Runtime.CompilerServices
{
    // The runtime recognizes this attribute name for dynamic fixture assemblies.
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
