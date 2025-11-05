using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using YamlDotNet.RepresentationModel;

namespace UnityProjectAnalyzer
{
    public static class FileSystemAnalyzer
    {
        // Main method for data collection
        public static async Task CollectAllScriptGuidsAndFieldsAsync(
            string projectPath, 
            ConcurrentDictionary<string, ScriptInfo> allScriptsByGuid)
        {
            var assetsPath = Path.Combine(projectPath, "Assets");
            if (!Directory.Exists(assetsPath)) return;

            var csFiles = Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories).ToList();
            
            // Use Parallel.ForEach to speed up Roslyn processing
            await Task.Run(() => 
            {
                Parallel.ForEach(csFiles, csFilePath =>
                {
                    ProcessScript(projectPath, csFilePath, allScriptsByGuid);
                });
            });
        }

        // Processes a single .cs file and its associated .meta file
        private static void ProcessScript(
            string projectPath, 
            string csFilePath, 
            ConcurrentDictionary<string, ScriptInfo> allScriptsByGuid)
        {
            var metaFilePath = csFilePath + ".meta";
            
            // 1. Extract the GUID from the .meta file
            var guid = ExtractGuidFromMeta(metaFilePath);
            if (string.IsNullOrEmpty(guid)) return;

            // 2. Extract MonoBehaviour/ScriptableObject classes and serialized fields
            (List<string> classNames, Dictionary<string, string> serializableFields) = ExtractScriptDetails(csFilePath);
            
            if (!classNames.Any()) return; 

            // 3. Create and add ScriptInfo
            var relativePath = Path.GetRelativePath(projectPath, csFilePath).Replace('\\', '/');
            
            var scriptInfo = new ScriptInfo
            {
                Guid = guid,
                Path = csFilePath,
                RelativePath = relativePath,
                ExtendsMonoBehaviourOrSO = classNames,
                SerializedFields = serializableFields
            };

            allScriptsByGuid.TryAdd(guid, scriptInfo);
        }

        // Extracts the GUID from a YAML .meta file
        private static string ExtractGuidFromMeta(string metaFilePath)
        {
            if (!File.Exists(metaFilePath)) return null;

            try
            {
                var input = new StringReader(File.ReadAllText(metaFilePath));
                var yaml = new YamlStream();
                yaml.Load(input);

                // Look for the "guid" node
                var root = yaml.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;

                if (root == null) return null;
                
                // Search for the "guid" key
                var guidEntry = root.Children
                    .FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "guid");
                
                // Check if the "guid" node and its value exist
                if (guidEntry.Key != null && guidEntry.Value is YamlScalarNode guidScalar)
                {
                    return guidScalar.Value;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                // Ignore .meta files that are not parsable YAML (or corrupt)
                Console.WriteLine($"Error parsing meta file {metaFilePath}: {ex.Message}");
                return null;
            }
        }

        // Extracts C# details using Roslyn
        private static (List<string> ClassNames, Dictionary<string, string> SerializedFields) ExtractScriptDetails(string csFilePath)
        {
            var classNames = new List<string>();
            var serializedFields = new Dictionary<string, string>();

            try
            {
                var code = File.ReadAllText(csFilePath);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classDecl in classDeclarations)
                {
                    bool isMonoBehaviour = false;
                    
                    // Check if it inherits from MonoBehaviour or ScriptableObject
                    if (classDecl.BaseList != null)
                    {
                        var baseTypes = classDecl.BaseList.Types.Select(t => t.Type.ToString());
                        if (baseTypes.Any(t => t == "MonoBehaviour" || t == "ScriptableObject"))
                        {
                            isMonoBehaviour = true;
                            classNames.Add(classDecl.Identifier.Text);
                        }
                    }

                    if (isMonoBehaviour)
                    {
                        var fieldDeclarations = classDecl.DescendantNodes().OfType<FieldDeclarationSyntax>();

                        foreach (var fieldDecl in fieldDeclarations)
                        {
                            bool isPublic = fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                            bool hasSerializeField = fieldDecl.AttributeLists
                                .SelectMany(al => al.Attributes)
                                .Any(a => a.Name.ToString().Contains("SerializeField"));

                            // Exclude fields marked with [NonSerialized]
                            bool isNonSerialized = fieldDecl.AttributeLists
                                .SelectMany(al => al.Attributes)
                                .Any(a => a.Name.ToString().Contains("NonSerialized"));

                            if (isNonSerialized) continue;

                            if (isPublic || hasSerializeField)
                            {
                                var type = fieldDecl.Declaration.Type.ToString();
                                
                                foreach (var variable in fieldDecl.Declaration.Variables)
                                {
                                    serializedFields.TryAdd(variable.Identifier.Text, type);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Roslyn error processing file {csFilePath}: {ex.Message}");
            }

            return (classNames, serializedFields);
        }
    }
}