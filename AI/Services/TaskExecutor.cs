using AI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AI.Services
{
    public interface ITaskExecutor
    {
        Task ExecuteTaskAsync(TaskDefinition task, string originalContext, int taskNumber, int totalTasks);
    }

    public class TaskExecutor : ITaskExecutor
    {
        private readonly IOllamaService _ollama;
        private readonly IFileSystemService _fileSystem;
        private const int MAX_RETRIES = 2;

        public TaskExecutor(IOllamaService ollama, IFileSystemService fileSystem)
        {
            _ollama = ollama;
            _fileSystem = fileSystem;
        }

        public async Task ExecuteTaskAsync(TaskDefinition task, string originalContext, int taskNumber, int totalTasks)
        {
            Console.WriteLine($"\nTask {taskNumber}/{totalTasks} [{task.TargetFile}] : {task.TaskName}");

            if (string.IsNullOrWhiteSpace(task.TargetFile))
            {
                Console.WriteLine($"✗ Task {taskNumber} Failed: No target file specified");
                return;
            }

            // Ensure the file has an extension
            var targetFile = EnsureFileExtension(task.TargetFile);

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    var prompt = BuildTaskPrompt(task, originalContext, attempt > 1);
                    var response = await _ollama.SendPromptAsync<SingleTaskResponse>(prompt);

                    // Use the response file path if provided, otherwise use the task's target
                    var finalTargetFile = !string.IsNullOrWhiteSpace(response.FilePath)
                        ? EnsureFileExtension(response.FilePath)
                        : targetFile;

                    if (string.IsNullOrWhiteSpace(finalTargetFile) || !finalTargetFile.Contains('.'))
                    {
                        if (attempt < MAX_RETRIES)
                        {
                            Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: Invalid file path, retrying...");
                            continue;
                        }
                        Console.WriteLine($"✗ Task {taskNumber} Failed: Invalid file path '{finalTargetFile}'.");
                        return;
                    }

                    switch (response.Operation)
                    {
                        case FileOperation.Create:
                        case FileOperation.Update:
                            if (string.IsNullOrWhiteSpace(response.Code))
                            {
                                if (attempt < MAX_RETRIES)
                                {
                                    Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: No code generated, retrying...");
                                    continue;
                                }
                                Console.WriteLine($"✗ Task {taskNumber} Failed: No code generated");
                                return;
                            }

                            // Clean and validate the code
                            var cleanedCode = CleanGeneratedCode(response.Code);

                            _fileSystem.WriteFile(finalTargetFile, cleanedCode);
                            Console.WriteLine($"✓ Task {taskNumber} Complete - {response.Operation}d {finalTargetFile}");
                            break;

                        case FileOperation.Delete:
                            _fileSystem.DeleteFile(finalTargetFile);
                            Console.WriteLine($"✓ Task {taskNumber} Complete - Deleted {finalTargetFile}");
                            break;
                    }

                    if (!string.IsNullOrWhiteSpace(response.Explanation))
                    {
                        Console.WriteLine($"  → {response.Explanation}");
                    }

                    // Success - exit retry loop
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt < MAX_RETRIES)
                    {
                        Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: {ex.Message}");
                        await Task.Delay(1000 * attempt); // Exponential backoff
                    }
                    else
                    {
                        Console.WriteLine($"✗ Task {taskNumber} Failed after {MAX_RETRIES} attempts: {ex.Message}");
                    }
                }
            }
        }

        private string BuildTaskPrompt(TaskDefinition task, string context, bool isRetry = false)
        {
            var targetFile = EnsureFileExtension(task.TargetFile);

            var retryInstructions = isRetry ? @"
IMPORTANT: This is a retry. Please ensure:
1. The response is COMPLETE and not truncated
2. All code is properly formatted
3. The JSON structure is valid" : "";

            // Determine the file type for better context
            var fileTypeHint = GetFileTypeHint(targetFile);

            return $@"You are an expert programmer. Execute this atomic task.

TASK: {task.DetailedPrompt}
TARGET FILE: {targetFile}
OPERATION: {task.OperationString}
FILE TYPE: {fileTypeHint}

{retryInstructions}

CONTEXT:
{context}

Generate the COMPLETE code for the file '{targetFile}'.

For a {fileTypeHint} file, ensure you include all necessary:
- Using statements / imports
- Namespace declarations
- Class definitions
- Complete method implementations
- Proper formatting

Return ONLY this JSON structure with NO truncation:
{{
  ""code"": ""<COMPLETE FILE CONTENT HERE>"",
  ""file_path"": ""{targetFile}"",
  ""operation"": ""{task.OperationString}"",
  ""explanation"": ""Brief explanation of what was done""
}}

CRITICAL: The 'code' field must contain the ENTIRE file content, not a snippet or placeholder.";
        }

        private string GetFileTypeHint(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLower();
            return extension switch
            {
                ".cs" => "C# class",
                ".cshtml" => "Razor view",
                ".json" => "JSON configuration",
                ".xml" => "XML configuration",
                ".csproj" => "C# project file",
                ".css" => "CSS stylesheet",
                ".js" => "JavaScript",
                ".ts" => "TypeScript",
                ".html" => "HTML",
                _ => "text"
            };
        }

        private string CleanGeneratedCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code;

            // Unescape common escape sequences
            code = code.Replace("\\n", "\n")
                      .Replace("\\r", "\r")
                      .Replace("\\t", "\t")
                      .Replace("\\\"", "\"");

            // Remove any potential JSON formatting artifacts
            if (code.StartsWith("\"") && code.EndsWith("\""))
            {
                code = code.Substring(1, code.Length - 2);
            }

            // Fix common formatting issues
            code = System.Text.RegularExpressions.Regex.Replace(code, @"\\u([0-9a-fA-F]{4})", m =>
            {
                var hex = m.Groups[1].Value;
                var codePoint = Convert.ToInt32(hex, 16);
                return char.ConvertFromUtf32(codePoint);
            });

            return code;
        }

        private string EnsureFileExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            // If already has an extension, return as-is
            if (Path.HasExtension(filePath))
                return filePath;

            // Try to infer extension from path or context
            var lowerPath = filePath.ToLower();

            if (lowerPath.Contains("controller"))
                return filePath + ".cs";
            if (lowerPath.Contains("model"))
                return filePath + ".cs";
            if (lowerPath.Contains("service"))
                return filePath + ".cs";
            if (lowerPath.Contains("view") || lowerPath.Contains("page"))
                return filePath + ".cshtml";
            if (lowerPath.Contains("script"))
                return filePath + ".js";
            if (lowerPath.Contains("style"))
                return filePath + ".css";

            // Default to .cs for C# projects
            return filePath + ".cs";
        }
    }
}