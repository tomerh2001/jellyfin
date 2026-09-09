using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    throw new ArgumentException("Expected the unchanged LinuxServer bin directory and candidate Emby.Naming.dll.");
}

var baselineDirectory = Path.GetFullPath(args[0]);
var baselineFile = Path.Combine(baselineDirectory, "Emby.Naming.dll");
var candidateFile = Path.GetFullPath(args[1]);
var baselineContext = new NamingContext(baselineDirectory, baselineFile);
var candidateContext = new NamingContext(baselineDirectory, candidateFile);
var baseline = baselineContext.LoadFromAssemblyPath(baselineFile);
var candidate = candidateContext.LoadFromAssemblyPath(candidateFile);
Require(baseline.GetName().Version == new Version(12, 0, 0, 0), "Unexpected base assembly version.");
Require(baseline.GetName().FullName == candidate.GetName().FullName, "Assembly identity changed.");
Require(References(baseline).SequenceEqual(References(candidate)), "Assembly references changed.");
var baselineApi = PublicApi(baseline);
var candidateApi = PublicApi(candidate);
Require(baselineApi.SequenceEqual(candidateApi), "Public/protected API changed: "
    + string.Join("; ", baselineApi.Except(candidateApi).Concat(candidateApi.Except(baselineApi)).Take(10)));

var cases = new[]
{
    new Case("same-date-distinct-titles", new[]
    {
        "/tv/Example/Example.Show.2024.11.09.First.Story.1080p.mkv",
        "/tv/Example/Example.Show.2024.11.09.Second.Story.1080p.mkv",
        "/tv/Example/Example.Show.2024.11.09.Third.Story.1080p.mkv"
    }, 3, 0),
    new Case("absolute-number-title", new[]
    {
        "/anime/IS Infinite Stratos 2/IS Infinite Stratos 2 - 01 - First Story.mkv",
        "/anime/IS Infinite Stratos 2/IS Infinite Stratos 2 - 02 - Second Story.mkv",
        "/anime/IS Infinite Stratos 2/IS Infinite Stratos 2 - 03 - Third Story.mkv"
    }, 3, 0),
    new Case("explicit-episode-versions", new[]
    {
        "/tv/Example/Example.S01E01.1080p.mkv",
        "/tv/Example/Example.S01E01.720p.mkv",
        "/tv/Example/Example.S01E02.1080p.mkv"
    }, 2, 1)
};

var results = cases.Select(test =>
{
    var original = Resolve(baseline, baselineContext, test.Paths);
    var patched = Resolve(candidate, candidateContext, test.Paths);
    Require(patched.Count == test.Count && patched.Alternates == test.Alternates,
        "Runtime regression failed: " + test.Name);
    Require(patched.Paths.Order().SequenceEqual(test.Paths.Order()), "A source path was lost: " + test.Name);
    if (test.Name == "same-date-distinct-titles")
    {
        Require(original.Count < test.Count, "Pinned base did not reproduce the date grouping regression.");
    }

    if (test.Name == "explicit-episode-versions")
    {
        Require(original.Count == patched.Count && original.Alternates == patched.Alternates,
            "Explicit episode versions changed.");
    }

    return new { test.Name, Original = original, Patched = patched };
}).ToArray();

// Every non-framework dependency loaded by the candidate must be the original image file.
var loadedDependencies = candidateContext.Assemblies.Where(a => a != candidate).Select(a =>
{
    Require(Path.GetDirectoryName(a.Location) == baselineDirectory, "A rebuilt dependency leaked into the probe.");
    return new { Name = a.GetName().Name, Sha256 = Hash(a.Location) };
}).OrderBy(x => x.Name).ToArray();
Require(loadedDependencies.Any(x => x.Name == "MediaBrowser.Model"), "Runtime probe did not load base dependencies.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    AssemblyIdentity = candidate.GetName().FullName,
    AssemblyReferencesIdentical = true,
    PublicProtectedApiIdentical = true,
    PublicProtectedApiEntries = candidateApi.Length,
    BaseSha256 = Hash(baselineFile),
    CandidateSha256 = Hash(candidateFile),
    DependenciesFromUnchangedBase = loadedDependencies,
    Cases = results
}, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(x => x.FullName).Order().ToArray();

static string[] PublicApi(Assembly assembly)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    var lines = new List<string>();
    foreach (var type in assembly.GetExportedTypes())
    {
        lines.Add($"TYPE {type.FullName} {type.Attributes} : {type.BaseType} [{string.Join(',', type.GetInterfaces().Select(x => x.ToString()).Order())}]");
        foreach (var parameter in type.GetGenericArguments().Where(x => x.IsGenericParameter))
        {
            lines.Add($"TYPEPARAM {type.FullName} {parameter} {parameter.GenericParameterAttributes} {string.Join(',', parameter.GetGenericParameterConstraints().Select(x => x.ToString()).Order())}");
        }

        foreach (var member in type.GetMembers(flags))
        {
            if (member is MethodBase method && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
            {
                var parameters = method.GetParameters().Select(p => $"{p.ParameterType}:{p.Attributes}:{p.RawDefaultValue}");
                lines.Add($"METHOD {type.FullName} {method} {method.Attributes} [{string.Join(',', parameters)}]");
            }
            else if (member is FieldInfo field && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
            {
                lines.Add($"FIELD {type.FullName} {field} {field.Attributes} {(field.IsLiteral ? field.GetRawConstantValue() : null)}");
            }
            else if (member is PropertyInfo property && property.GetAccessors(true).Any(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            {
                lines.Add($"PROPERTY {type.FullName} {property} {property.Attributes}");
            }
            else if (member is EventInfo eventInfo && eventInfo.GetAddMethod(true) is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly))
            {
                lines.Add($"EVENT {type.FullName} {eventInfo} {eventInfo.Attributes}");
            }
        }
    }

    return lines.Order().ToArray();
}

static Resolution Resolve(Assembly naming, NamingContext context, string[] paths)
{
    var options = Activator.CreateInstance(naming.GetType("Emby.Naming.Common.NamingOptions", true)!);
    var resolverType = naming.GetType("Emby.Naming.Video.VideoListResolver", true)!;
    var resolver = Activator.CreateInstance(resolverType, options);
    var fileType = naming.GetType("Emby.Naming.Video.VideoFileInfo", true)!;
    var fileResolver = naming.GetType("Emby.Naming.Video.VideoResolver", true)!.GetMethod("Resolve")!;
    var files = Array.CreateInstance(fileType, paths.Length);
    for (var i = 0; i < paths.Length; i++)
    {
        files.SetValue(fileResolver.Invoke(null, new object?[] { paths[i], false, options, true, "" }), i);
    }

    var collectionType = context.LoadFromAssemblyName(new AssemblyName("Jellyfin.Data")).GetType("Jellyfin.Data.Enums.CollectionType", true)!;
    var videos = ((IEnumerable)resolverType.GetMethod("Resolve")!.Invoke(resolver,
        new object?[] { files, true, true, "", Enum.Parse(collectionType, "tvshows") })!).Cast<object>().ToArray();
    var alternates = videos.SelectMany(v => ((IEnumerable)v.GetType().GetProperty("AlternateVersions")!.GetValue(v)!).Cast<object>()).ToArray();
    var resolvedPaths = videos.Concat(alternates)
        .SelectMany(v => ((IEnumerable)v.GetType().GetProperty("Files")!.GetValue(v)!).Cast<object>())
        .Select(f => (string)f.GetType().GetProperty("Path")!.GetValue(f)!).ToArray();
    return new Resolution(videos.Length, alternates.Length, resolvedPaths);
}

sealed class NamingContext(string directory, string namingFile) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "Emby.Naming")
        {
            return LoadFromAssemblyPath(namingFile);
        }

        if (name.Name is null || name.Name == "System" || name.Name.StartsWith("System.", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

sealed record Case(string Name, string[] Paths, int Count, int Alternates);
sealed record Resolution(int Count, int Alternates, string[] Paths);
