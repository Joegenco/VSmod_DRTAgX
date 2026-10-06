using System;
using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// Only the asset getter is simulated. Compute compilation, dispatch and binding
// restoration run through the production owner against a real hidden GL context.
public class ProbeAssets : DispatchProxy
{
    private System.Func<MethodInfo, object[], object> _get;
    protected override object Invoke(MethodInfo method, object[] arguments) => _get(method, arguments);

    private static T Create<T>(System.Func<MethodInfo, object[], object> get) where T : class
    {
        T proxy = DispatchProxy.Create<T, ProbeAssets>();
        ((ProbeAssets)(object)proxy)._get = get;
        return proxy;
    }

    internal static ICoreClientAPI Api(byte[] data, Action warning = null)
    {
        // Proxy generation inspects native API signatures. Resolve their optional
        // dependencies from the installed game rather than adding test packages.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string install = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(install)) return null;
            string library = Path.Combine(install, "Lib", name.Name + ".dll");
            if (!File.Exists(library)) library = Path.Combine(install, name.Name + ".dll");
            return File.Exists(library) ? context.LoadFromAssemblyPath(library) : null;
        };
        var asset = Create<IAsset>((method, _) => method.Name == "get_Data" ? data : throw new NotSupportedException(method.Name));
        var manager = Create<IAssetManager>((method, _) => method.Name == "Get" ? asset : throw new NotSupportedException(method.Name));
        var logger = Create<ILogger>((method, _) => { if (method.Name == "Warning") warning?.Invoke(); return null; });
        return Create<ICoreClientAPI>((method, _) => method.Name switch {
            "get_Assets" => manager, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
    }

    internal static ICoreClientAPI AtmosphereApi(string directory)
    {
        Api([]); // Install the native API dependency resolver before proxy generation.
        var manager = Create<IAssetManager>((method, arguments) =>
        {
            if (method.Name != "Get") throw new NotSupportedException(method.Name);
            string name = Path.GetFileName(((AssetLocation)arguments[0]).Path);
            string source = File.ReadAllText(Path.Combine(directory, name));
            return Create<IAsset>((assetMethod, _) => assetMethod.Name switch
            {
                "ToText" => source,
                "get_Data" => System.Text.Encoding.UTF8.GetBytes(source),
                _ => throw new NotSupportedException(assetMethod.Name)
            });
        });
        return Create<ICoreClientAPI>((method, _) => method.Name == "get_Assets" ? manager : throw new NotSupportedException(method.Name));
    }
}
