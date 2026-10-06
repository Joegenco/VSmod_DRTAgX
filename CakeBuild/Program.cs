using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
    public const string ProjectName = "DRTAgX";
    public string ProjectRoot { get; }
    public string ModDirectory { get; }
    public string ProjectFile { get; }
    public string ReleasesDirectory { get; }
    public string BuildConfiguration { get; }
    public string Version { get; }
    public string Name { get; }
    public bool SkipJsonValidation { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        BuildConfiguration = context.Argument("configuration", "Release");
        if (BuildConfiguration != "Debug" && BuildConfiguration != "Release")
            throw new ArgumentException("Configuration must be Debug or Release.");
        SkipJsonValidation = context.Argument("skipJsonValidation", false);
        string rootArgument = context.Argument("project-root", string.Empty);
        // Wrappers pass their absolute root. Direct Cake launches locate the
        // source checkout from the executable, never from the caller's CWD.
        ProjectRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(rootArgument)
            ? FindProjectRoot(AppContext.BaseDirectory) : rootArgument);
        ModDirectory = Path.Combine(ProjectRoot, ProjectName);
        ProjectFile = Path.Combine(ModDirectory, ProjectName + ".csproj");
        ReleasesDirectory = Path.Combine(ProjectRoot, "Releases");
        if (!File.Exists(ProjectFile)) throw new FileNotFoundException("Mod project is unavailable.", ProjectFile);
        var modInfo = context.DeserializeJsonFromFile<ModInfo>(Path.Combine(ModDirectory, "modinfo.json"));
        Version = modInfo.Version;
        Name = modInfo.ModID;
        if (string.IsNullOrWhiteSpace(Name) || !System.Version.TryParse(Version, out _) ||
            (Name + "_" + Version).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Mod ID/version must form a valid release filename.");
    }

    private static string FindProjectRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ProjectName, ProjectName + ".csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Pass --project-root pointing to the solution directory.");
    }
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (context.SkipJsonValidation)
        {
            return;
        }

        var jsonFiles = context.GetFiles($"{context.ModDirectory}/assets/**/*.json");
        foreach (var file in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(file.FullPath);
                JToken.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    $"Validation failed for JSON file: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
            }
        }
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetClean(context.ProjectFile,
            new DotNetCleanSettings
            {
                Configuration = context.BuildConfiguration
            });


        context.DotNetPublish(context.ProjectFile,
            new DotNetPublishSettings
            {
                Configuration = context.BuildConfiguration
            });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        string stem = context.Name + "_" + context.Version;
        string runId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(context.ReleasesDirectory, ".staging", stem + "_" + runId);
        Directory.CreateDirectory(staging);
        // Whitelist runtime content: engine assemblies, build caches, tests and
        // debug symbols never enter the mod ZIP. Each run owns fresh staging.
        string published = Path.Combine(context.ModDirectory, "bin", context.BuildConfiguration, "Mods", "mod", "publish");
        context.CopyFile(Path.Combine(published, BuildContext.ProjectName + ".dll"), Path.Combine(staging, BuildContext.ProjectName + ".dll"));
        context.CopyDirectory(Path.Combine(context.ModDirectory, "assets"), Path.Combine(staging, "assets"));
        context.CopyFile(Path.Combine(context.ModDirectory, "modinfo.json"), Path.Combine(staging, "modinfo.json"));
        string icon = Path.Combine(context.ModDirectory, "modicon.png");
        if (File.Exists(icon)) context.CopyFile(icon, Path.Combine(staging, "modicon.png"));

        var version = new Version(context.Version);
        var assemblyVersion = AssemblyName.GetAssemblyName(Path.Combine(staging, BuildContext.ProjectName + ".dll")).Version;
        if (assemblyVersion != new Version(version.Major, version.Minor, version.Build, 0))
            throw new InvalidOperationException("Assembly and modinfo release versions differ.");

        string candidate = Path.Combine(context.ReleasesDirectory, ".staging", stem + "_" + runId + ".zip");
        ZipFile.CreateFromDirectory(staging, candidate, CompressionLevel.Optimal, includeBaseDirectory: false);
        VerifyArchive(staging, candidate);
        string hash;
        using (var stream = File.OpenRead(candidate)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        string archive = Path.Combine(context.ReleasesDirectory, stem + ".zip");
        // Repeated packaging preserves existing archives/checksums, including
        // another build of this same version. Nothing cleans Releases wholesale.
        if (File.Exists(archive) || File.Exists(archive + ".sha256"))
            archive = Path.Combine(context.ReleasesDirectory, stem + "_" + runId + ".zip");
        File.Move(candidate, archive);
        using (var checksum = new FileStream(archive + ".sha256", FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(checksum, new UTF8Encoding(false)))
            writer.WriteLine(hash + "  " + Path.GetFileName(archive));
        Console.WriteLine("Validated release: " + archive);
        Console.WriteLine("SHA-256: " + hash);
    }

    private static void VerifyArchive(string staging, string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/'))
            .ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        string[] files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        if (entries.Count != files.Length) throw new InvalidDataException("Release ZIP file count differs from staging.");
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("Release ZIP is missing " + name);
            using var original = File.OpenRead(file);
            using var packed = entry.Open();
            if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(packed)))
                throw new InvalidDataException("Release ZIP content differs: " + name);
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public class DefaultTask : FrostingTask
{
}
