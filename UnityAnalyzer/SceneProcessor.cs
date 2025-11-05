using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using YamlDotNet.RepresentationModel;

namespace UnityProjectAnalyzer
{
    // Assuming these structs/classes are defined elsewhere in the project
    // public record ScriptInfo(string RelativePath, Dictionary<string, string> SerializedFields);
    // public record GameObjectData(string FileId, string Name, string TransformFileId);
    // public record TransformData(string FileId, string ParentFileId);
    // public record SceneProcessingResult(List<GameObjectData> RootObjects, HashSet<string> UsedGuids, List<string> InconsistentGuids);

    public static class SceneProcessor
    {
        // Main method for processing all scenes in parallel
        public static async Task ProcessAllScenesAsync(
            string projectPath,
            string outputPath,
            Dictionary<string, ScriptInfo> allScriptsByGuid,
            HashSet<string> usedScriptGuids,
            object usedGuidsLock,
            ConcurrentBag<string> inconsistentMonoBehaviourGuids)
        {
            var assetsPath = Path.Combine(projectPath, "Assets");
            var sceneFiles = Directory.EnumerateFiles(assetsPath, "*.unity", SearchOption.AllDirectories).ToList();

            await Task.Run(() => 
            {
                Parallel.ForEach(sceneFiles, sceneFilePath =>
                {
                    var sceneName = Path.GetFileNameWithoutExtension(sceneFilePath);
                    
                    Console.WriteLine($"   -> Processing scene: {sceneName}");
                    var result = ProcessSingleScene(sceneFilePath, allScriptsByGuid);

                    if (result != null)
                    {
                        // Global synchronization of used GUIDs
                        lock (usedGuidsLock)
                        {
                            foreach (var guid in result.UsedGuids)
                            {
                                usedScriptGuids.Add(guid);
                            }
                        }

                        // Add inconsistent GUIDs
                        foreach (var guid in result.InconsistentGuids)
                        {
                            inconsistentMonoBehaviourGuids.Add(guid);
                        }

                        // Generate hierarchy report (dump)
                        WriteSceneDump(outputPath, sceneName, result.RootObjects, result.UsedGuids, result.InconsistentGuids, allScriptsByGuid);
                    }
                });
            });
        }

        // Processes a single scene
        private static SceneProcessingResult? ProcessSingleScene(
            string sceneFilePath,
            Dictionary<string, ScriptInfo> allScriptsByGuid)
        {
            if (!File.Exists(sceneFilePath)) return null;

            try
            {
                // Reading all lines
                var lines = File.ReadAllLines(sceneFilePath);
                // Filtering lines that are not part of the YAML body
                var yamlContent = string.Join(Environment.NewLine, 
                    lines.Where(line => line.StartsWith("---") || line.StartsWith("!u!") || !line.StartsWith("%")));

                // If we don't find the YAML body, exit.
                if (string.IsNullOrWhiteSpace(yamlContent)) return null;

                var input = new StringReader(yamlContent);
                var yaml = new YamlStream();
                
                // Now load only the filtered content that starts with '---'
                yaml.Load(input); 

                // Local collections to store data extracted from YAML
                var fileIdToGameObject = new Dictionary<string, GameObjectData>();
                var fileIdToTransform = new Dictionary<string, TransformData>();
                var usedGuids = new HashSet<string>();
                var inconsistentGuids = new List<string>();

                foreach (var document in yaml.Documents)
                {
                    if (document.RootNode is YamlMappingNode rootNode)
                    {
                        // A YAML document in a .unity file contains exactly one mapping.
                        foreach (var entry in rootNode.Children)
                        {
                            // Extract FileID from the key. The key is in the format '&[FileID]'
                            var fileIdWithPrefix = (entry.Key as YamlScalarNode)?.Value;
                            var fileId = fileIdWithPrefix?.TrimStart('&');

                            // Check if the value is a YamlMappingNode, which is the expected type for components/GameObjects
                            if (!(entry.Value is YamlMappingNode node)) continue;

                            if (string.IsNullOrEmpty(fileId)) continue;

                            // Defensive access to Tag.Value
                            var tag = node.Tag;
                            var tagValue = tag.IsEmpty ? null : tag.Value;

                            // Type 1: GameObject
                            if (tagValue != null && tagValue.Contains("!u!1"))
                            {
                                ExtractGameObjectData(fileId, node, fileIdToGameObject);
                            }
                            // Type 4: Transform 
                            else if (tagValue != null && tagValue.Contains("!u!4"))
                            {
                                ExtractTransformData(fileId, node, fileIdToTransform);
                            }
                            // Type 114: MonoBehaviour (attached script)
                            else if (tagValue != null && tagValue.Contains("!u!114"))
                            {
                                ExtractMonoBehaviourData(node, usedGuids, inconsistentGuids, allScriptsByGuid);
                            }
                        }
                    }
                }

                var rootObjects = BuildHierarchy(fileIdToGameObject, fileIdToTransform);
                
                return new SceneProcessingResult(rootObjects, usedGuids, inconsistentGuids);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error parsing scene {sceneFilePath}: {ex.Message}");
                return null;
            }
        }

        // Extracts Name and TransformFileId from a GameObject (type !u!1)
        private static void ExtractGameObjectData(
            string fileId, 
            YamlMappingNode node, 
            Dictionary<string, GameObjectData> fileIdToGameObject)
        {
            var nameNode = node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "m_Name");
            var name = (nameNode.Value as YamlScalarNode)?.Value ?? $"Unnamed (FileID: {fileId})";

            var componentsNode = node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "m_Component");
            
            // Check for m_Component as a SequenceNode
            if (componentsNode.Value is YamlSequenceNode componentsSequence)
            {
                // Iterate through components to find the Transform reference. 
                // Typically, Transform is the first component.
                foreach (var componentEntry in componentsSequence.Children.OfType<YamlMappingNode>())
                {
                    // Each component is a mapping containing the 'component' key which maps to the component's FileID
                    var componentReferenceNode = componentEntry.Children.FirstOrDefault().Value as YamlMappingNode;
                    
                    if (componentReferenceNode != null)
                    {
                        var fileIDKeyNode = componentReferenceNode.Children.FirstOrDefault().Key as YamlScalarNode;
                        
                        // We look for the Transform's FileID. A GameObject always has a Transform that is not '0'.
                        if (fileIDKeyNode != null && fileIDKeyNode.Value == "fileID")
                        {
                            var transformFileId = (componentReferenceNode.Children.FirstOrDefault().Value as YamlScalarNode)?.Value;
                            
                            // Assume the first valid component found is the Transform (which has a fileID != '0')
                            if (transformFileId != null && transformFileId != "0")
                            {
                                fileIdToGameObject[fileId] = new GameObjectData(fileId, name, transformFileId);
                                return; 
                            }
                        }
                    }
                }
            }
        }

        // Extracts ParentFileId from a Transform (type !u!4)
        private static void ExtractTransformData(
            string fileId, 
            YamlMappingNode node, 
            Dictionary<string, TransformData> fileIdToTransform)
        {
            var parentNode = node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "m_Parent");
            if (parentNode.Value is YamlMappingNode parentMappingNode)
            {
                var parentFileId = (parentMappingNode.Children.FirstOrDefault().Value as YamlScalarNode)?.Value;
                
                // If parentFileId is '0', it is a root object
                parentFileId ??= "0"; 

                fileIdToTransform[fileId] = new TransformData(fileId, parentFileId);
            }
        }

        // Extracts the GUID and checks for consistency (type !u!114 - MonoBehaviour)
        private static void ExtractMonoBehaviourData(
            YamlMappingNode node,
            HashSet<string> usedGuids,
            List<string> inconsistentGuids,
            Dictionary<string, ScriptInfo> allScriptsByGuid)
        {
            var scriptNode = node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "m_Script");
            if (scriptNode.Value is YamlMappingNode scriptMapping)
            {
                var guidNode = scriptMapping.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "guid");
                var guid = (guidNode.Value as YamlScalarNode)?.Value;

                if (guid != null)
                {
                    usedGuids.Add(guid);

                    if (allScriptsByGuid.TryGetValue(guid, out var scriptInfo))
                    {
                        var serializedFieldsInScene = node.Children
                            .Where(c => 
                                (c.Key as YamlScalarNode)?.Value != null &&
                                // Exclusion of standard fields of any MonoBehaviour
                                !((c.Key as YamlScalarNode)?.Value?.StartsWith("m_")) == true &&
                                (c.Key as YamlScalarNode)?.Value != "m_Script")
                            .Select(c => (c.Key as YamlScalarNode)?.Value)
                            .Where(name => name != null)
                            .ToHashSet();

                        // Check: Fields in the scene vs. fields in the code
                        if (serializedFieldsInScene.Any(fieldName => !scriptInfo.SerializedFields.ContainsKey(fieldName!)))
                        {
                            inconsistentGuids.Add(guid);
                        }
                    }
                }
            }
        }

        // Reconstructs the Parent-Child hierarchy and returns the root objects
        private static List<GameObjectData> BuildHierarchy(
            Dictionary<string, GameObjectData> fileIdToGameObject,
            Dictionary<string, TransformData> fileIdToTransform)
        {
            var rootObjects = new List<GameObjectData>();
            
            // A set of Transform File IDs that are children (to find what is a root)
            var childTransforms = fileIdToTransform.Values
                .Where(td => td.ParentFileId != "0")
                .Select(td => td.FileId)
                .ToHashSet();

            // Identify root objects (those whose Transform does not appear as a child)
            foreach (var goData in fileIdToGameObject.Values)
            {
                // A Transform is a root if:
                // 1. Its ParentFileId (from TransformData) is '0'
                // 2. Its TransformID (from GameObjectData) is not in the set of children (childTransforms)
                
                // We look for the corresponding TransformData to check m_Parent (case 1)
                var transformDataFound = fileIdToTransform.TryGetValue(goData.TransformFileId, out var transformData);

                if (transformDataFound && transformData!.ParentFileId == "0")
                {
                    // Case 1: The object's Transform has m_Parent: {fileID: 0}
                    rootObjects.Add(goData);
                }
                else if (!childTransforms.Contains(goData.TransformFileId) && !transformDataFound)
                {
                    // Case 2 (Fallback): If TransformData is not found, but the TransformID is not in the list of children, 
                    // we assume it is a root
                    rootObjects.Add(goData);
                }
               
            }

            return rootObjects.OrderBy(go => go.Name).ToList();
        }

        // Writes the hierarchy report (dump)
        private static void WriteSceneDump(
            string outputPath, 
            string sceneName, 
            List<GameObjectData> rootObjects, 
            HashSet<string> usedGuids, 
            List<string> inconsistentGuids,
            Dictionary<string, ScriptInfo> allScriptsByGuid)
        {
            var dumpPath = Path.Combine(outputPath, $"{sceneName}.unity.dump");

            using (var writer = new StreamWriter(dumpPath))
            {
                writer.WriteLine($"SCENE DUMP: {sceneName}.unity");
                
                if (rootObjects.Any())
                {
                    foreach (var root in rootObjects)
                    {
                        writer.WriteLine($"- {root.Name} (FileID: {root.FileId} | Transform: {root.TransformFileId})");
                    }
                }
                else
                {
                    writer.WriteLine("No root objects found in the scene.");
                }

                writer.WriteLine("\n[ SCRIPT GUIDs USED IN THIS SCENE ]");
                if (usedGuids.Any())
                {
                    foreach (var guid in usedGuids)
                    {
                        var path = allScriptsByGuid.TryGetValue(guid, out var scriptInfo) ? scriptInfo.RelativePath : "UNKNOWN";
                        writer.WriteLine($"- {guid} ({path})");
                    }
                }
                else
                {
                    writer.WriteLine("No scripts found used in the scene.");
                }
                
                writer.WriteLine("\n[ SCRIPT GUIDs WITH INCONSISTENCIES (MISSING FIELDS) ]");
                if (inconsistentGuids.Any())
                {
                    foreach (var guid in inconsistentGuids.Distinct())
                    {
                        var path = allScriptsByGuid.TryGetValue(guid, out var scriptInfo) ? scriptInfo.RelativePath : "UNKNOWN";
                        writer.WriteLine($"- {guid} ({path})");
                    }
                }
                else
                {
                    writer.WriteLine("No scripts found with inconsistencies.");
                }
                
            }
        }
    }
}