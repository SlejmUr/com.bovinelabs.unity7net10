using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    private static int Main(string[] args)
    {
        var toolPath = AppContext.BaseDirectory;
        var config = JsonSerializer.Deserialize<ProfileConfiguration>(File.ReadAllText(Path.Combine(toolPath, "profile.json")));
        var forwardedArguments = (string[])args.Clone();
        var inputData = JsonNode.Parse(File.ReadAllText(args[1]));
        var compilationData = inputData["ScriptCompilationBuildProgram.Data.ScriptCompilationData"];
        var packReferences = Directory.GetFiles(config.referencePack, "*.dll").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        var packNames = new HashSet<string>(packReferences.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);

        var changedAssemblies = new List<string>();

        foreach (var assembly in compilationData["Assemblies"].AsArray())
        {
            var name = assembly["Name"].GetValue<string>();
            var asmdef = assembly["Asmdef"]?.GetValue<string>();
            var projectPath = assembly["Path"].GetValue<string>();
            var responseFile = string.IsNullOrEmpty(asmdef) ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(asmdef, projectPath)), "csc.rsp");
            if (responseFile == null || !File.Exists(responseFile) || !File.ReadAllText(responseFile).Contains("/define:UNITY7_NET10", StringComparison.Ordinal))
            {
                continue;
            }

            var references = new List<string>();
            foreach (var reference in assembly["PrebuiltReferences"].AsArray())
            {
                var path = reference.GetValue<string>();
                var normalized = path.Replace('\\', '/');
                var legacyFramework = normalized.Contains("/UnityReferenceAssemblies/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("/NetStandard/", StringComparison.OrdinalIgnoreCase);
                var supersededExtension = normalized.Contains("/BCLExtensions/TargetingPacks/", StringComparison.OrdinalIgnoreCase)
                    && packNames.Contains(Path.GetFileName(path));
                if (!legacyFramework && !supersededExtension)
                {
                    references.Add(path);
                }
            }

            references.AddRange(packReferences);
            assembly["PrebuiltReferences"] = new JsonArray(references.Select(path => (JsonNode)JsonValue.Create(path)).ToArray());
            assembly["BclDirectories"] = new JsonArray(JsonValue.Create(config.referencePack));
            changedAssemblies.Add(name);
        }

        var inputDirectory = Path.Combine(toolPath, "inputs");
        Directory.CreateDirectory(inputDirectory);
        var patchedInputPath = Path.Combine(inputDirectory, Path.GetFileName(args[1]));
        File.Copy(args[1], patchedInputPath, true);
        File.WriteAllText(args[1], inputData.ToJsonString());
        File.WriteAllText(patchedInputPath + ".patched.json", inputData.ToJsonString());

        File.WriteAllText(Path.Combine(toolPath, "last-profile-application.json"), JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow,
            assemblies = changedAssemblies,
            referenceCount = packReferences.Length,
            referencePack = config.referencePack,
            originalInput = Path.GetFullPath(args[1]),
            originalSnapshot = patchedInputPath,
        }, new JsonSerializerOptions { WriteIndented = true }));

        var startInfo = new ProcessStartInfo(config.dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(config.originalProgram);
        foreach (var argument in forwardedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        process.WaitForExit();
        return process.ExitCode;
    }

    private sealed class ProfileConfiguration
    {
        public string originalProgram { get; set; }
        public string dotnet { get; set; }
        public string referencePack { get; set; }

    }
}




