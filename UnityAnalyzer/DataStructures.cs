using System.Collections.Generic;

namespace UnityProjectAnalyzer
{
    public struct ScriptInfo
    {
        // The unique GUID of the script read from the .meta file
        public string Guid { get; set; }

        // The absolute path of the .cs file
        public string Path { get; set; } 

        // The relative path (e.g., Assets/Scripts/MyScript.cs) - USED FOR THE REPORT
        public string RelativePath { get; set; } 

        // Names of classes that inherit from MonoBehaviour or ScriptableObject
        public List<string> ExtendsMonoBehaviourOrSO { get; set; }

        // Name and type of public/private[SerializeField] fields
        public Dictionary<string, string> SerializedFields { get; set; } // Key: Field Name, Value: Field Type
    }

    /// <summary>
    /// Data about a GameObject (for the hierarchy report)
    /// </summary>
    public record GameObjectData(string FileId, string Name, string TransformFileId);

    /// <summary>
    /// Data about a Transform (for hierarchy reconstruction)
    /// </summary>
    public record TransformData(string FileId, string ParentFileId);
    
    /// <summary>
    /// The result of processing a single scene.
    /// </summary>
    public class SceneProcessingResult
    {
        // List of GameObjects that are scene roots
        public List<GameObjectData> RootObjects { get; }
        
        // GUIDs of scripts used in this scene
        public HashSet<string> UsedGuids { get; }
        
        // GUIDs of scripts where a MonoBehaviour has missing serialized fields
        public List<string> InconsistentGuids { get; }

        public SceneProcessingResult(List<GameObjectData> rootObjects, HashSet<string> usedGuids, List<string> inconsistentGuids)
        {
            RootObjects = rootObjects;
            UsedGuids = usedGuids;
            InconsistentGuids = inconsistentGuids;
        }
    }
}