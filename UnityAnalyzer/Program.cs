using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;

namespace UnityProjectAnalyzer
{
    // The main class of the console application
    public static class Program
    {
        // Data structures shared between threads
        private static ConcurrentDictionary<string, ScriptInfo> AllScriptsByGuid = new ConcurrentDictionary<string, ScriptInfo>();
        private static readonly HashSet<string> UsedScriptGuids = new HashSet<string>();
        private static readonly object UsedGuidsLock = new object();
        // Collection for inconsistent GUIDs
        private static readonly ConcurrentBag<string> InconsistentMonoBehaviourGuids = new ConcurrentBag<string>();

        public static async Task Main(string[] args)
        {
            // Checks if two paths were provided (Unity project path and output path)
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: dotnet run -- <unity_project_path> <output_directory_path>");
                Console.Error.WriteLine("Example: dotnet run -- /path/to/MyUnityProject /path/to/Output");
                Console.Error.WriteLine("\nNOTE: After 'dotnet run', use '--' to pass arguments to the C# application.");
                return;
            }

            string projectPath = args[0];
            string outputPath = args[1];

            if (!Directory.Exists(projectPath))
            {
                Console.Error.WriteLine($"Error: The Unity project path '{projectPath}' is not valid.");
                return;
            }

            try
            {
                // Create the output folder if it doesn't exist
                Directory.CreateDirectory(outputPath);
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine($"Starting project analysis: {projectPath}");
                Console.WriteLine("--------------------------------------------------");


                // --- STEP 1: SCRIPT DATA COLLECTION (.meta + Roslyn) ---
                Console.WriteLine("Step 1/3: Collecting all script GUIDs and serialized fields (Roslyn/Parallel)...");
                
                // Assumes FileSystemAnalyzer and its method are defined elsewhere
                await FileSystemAnalyzer.CollectAllScriptGuidsAndFieldsAsync(projectPath, AllScriptsByGuid); 

                Console.WriteLine($"   -> Found {AllScriptsByGuid.Count} unique scripts to analyze.");


                // --- STEP 2: SCENE PROCESSING (Hierarchy + Used GUIDs) ---
                Console.WriteLine("Step 2/3: Processing scenes and extracting hierarchies and used GUIDs (Parallel)...");
                
                // CORRECTED: Added the missing argument: InconsistentMonoBehaviourGuids
                // Assumes SceneProcessor and its method are defined elsewhere
                await SceneProcessor.ProcessAllScenesAsync(
                    projectPath, 
                    outputPath, 
                    // Converts ConcurrentDictionary to Dictionary for the SceneProcessor (if it expects Dictionary)
                    AllScriptsByGuid.ToDictionary(k => k.Key, v => v.Value), 
                    UsedScriptGuids, 
                    UsedGuidsLock,
                    InconsistentMonoBehaviourGuids); 
                
                Console.WriteLine($"   -> All scenes processed and used GUIDs collected.");


                // --- STEP 3: GENERATING THE FINAL REPORT ---
                Console.WriteLine("Step 3/3: Generating the final analysis report...");
                WriteFinalReport(outputPath);

                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine($"Analysis completed successfully. Results are in folder: {outputPath}");
                Console.WriteLine("--------------------------------------------------");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"A critical error occurred: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
        }

        private static void WriteFinalReport(string outputPath)
        {
            // 1. Data Preparation
            var inconsistentGuidsSet = new HashSet<string>(InconsistentMonoBehaviourGuids);
            
            // Unused Scripts: (All Scripts) - (Scripts Used EVER)
            var unusedScripts = AllScriptsByGuid.Keys
                .Where(guid => !UsedScriptGuids.Contains(guid))
                .Select(guid => AllScriptsByGuid[guid].RelativePath) // Use RelativePath
                .OrderBy(p => p)
                .ToList();

            // Inconsistent Scripts: (Scripts that have at least one MonoBehaviour with missing fields)
            var inconsistentScripts = inconsistentGuidsSet
                .Where(guid => AllScriptsByGuid.ContainsKey(guid))
                .Select(guid => AllScriptsByGuid[guid].RelativePath) // Use RelativePath
                .OrderBy(p => p)
                .ToList();

            string reportPath = Path.Combine(outputPath, "AnalysisReport.txt");
            string csvPath = Path.Combine(outputPath, "UnusedScripts.csv");

            // Writing the Text Report
            using (var writer = new StreamWriter(reportPath))
            {
                writer.WriteLine("==================================================");
                writer.WriteLine("          UNITY PROJECT ANALYSIS REPORT           ");
                writer.WriteLine("==================================================");
                writer.WriteLine($"Analysis Date: {DateTime.Now}");
                writer.WriteLine($"Unique Scripts Found: {AllScriptsByGuid.Count}");
                writer.WriteLine($"Scripts Used in Scenes: {UsedScriptGuids.Count}");
                writer.WriteLine("--------------------------------------------------");
                
                writer.WriteLine($"\n[ 1. UNUSED SCRIPTS ({unusedScripts.Count}) ]");
                writer.WriteLine("(Scripts found in the project, but NOT ATTACHED to any GameObject in scenes)");
                if (unusedScripts.Any())
                {
                    foreach (var path in unusedScripts)
                    {
                        writer.WriteLine($"- {path}");
                    }
                }
                else
                {
                    writer.WriteLine("No unused C# scripts found.");
                }
                
                writer.WriteLine("--------------------------------------------------");
                
                writer.WriteLine($"\n[ 2. SCRIPTS WITH INCONSISTENCIES ({inconsistentScripts.Count}) ]");
                writer.WriteLine("(Scripts ATTACHED to GameObjects that have serialized fields found in .unity files, but which are MISSING from the actual C# code. These may cause data loss.)");
                if (inconsistentScripts.Any())
                {
                    foreach (var path in inconsistentScripts)
                    {
                        writer.WriteLine($"- {path}");
                    }
                }
                else
                {
                    writer.WriteLine("No scripts found with serialization inconsistencies (missing fields).");
                }
                writer.WriteLine("==================================================");
            }
            
            // Writing the CSV Report (only unused scripts)
            using (var writer = new StreamWriter(csvPath))
            {
                writer.WriteLine("script_name"); 
                foreach (var path in unusedScripts)
                {
                    writer.WriteLine(path);
                }
            }

            Console.WriteLine($"- Summary report saved to: {Path.GetFileName(reportPath)}");
            Console.WriteLine($"- Unused scripts list (CSV) saved to: {Path.GetFileName(csvPath)} ({unusedScripts.Count} unused scripts)");
        }
    }
}