using AI.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ILogger _logger;
        private const int MAX_RETRIES = 2;

        public TaskExecutor(IOllamaService ollama, IFileSystemService fileSystem)
        {
            _ollama = ollama;
            _fileSystem = fileSystem;
            _logger = Log.ForContext<TaskExecutor>();
        }

        public async Task ExecuteTaskAsync(TaskDefinition task, string originalContext, int taskNumber, int totalTasks)
        {
            var stopwatch = Stopwatch.StartNew();
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);

            _logger.Information("[{TaskId}] Starting task {TaskNumber}/{TotalTasks}: {TaskName}",
                taskId, taskNumber, totalTasks, task.TaskName);
            _logger.Debug("[{TaskId}] Task details - Target: {TargetFile}, Operation: {Operation}, Prompt: {Prompt}",
                taskId, task.TargetFile, task.OperationString, task.DetailedPrompt);

            Console.WriteLine($"\nTask {taskNumber}/{totalTasks} [{task.TargetFile}] : {task.TaskName}");

            if (string.IsNullOrWhiteSpace(task.TargetFile))
            {
                _logger.Error("[{TaskId}] Task {TaskNumber} failed: No target file specified", taskId, taskNumber);
                Console.WriteLine($"✗ Task {taskNumber} Failed: No target file specified");
                return;
            }

            // Ensure the file has an extension
            var targetFile = EnsureFileExtension(task.TargetFile);
            _logger.Debug("[{TaskId}] Target file after extension check: {TargetFile}", taskId, targetFile);

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                var attemptStopwatch = Stopwatch.StartNew();
                _logger.Information("[{TaskId}] Attempt {Attempt}/{MaxRetries} for task {TaskNumber}",
                    taskId, attempt, MAX_RETRIES, taskNumber);

                try
                {
                    var prompt = BuildTaskPrompt(task, originalContext, attempt > 1);
                    _logger.Debug("[{TaskId}] Task prompt built, length: {Length} chars", taskId, prompt.Length);
                    _logger.Verbose("[{TaskId}] Full task prompt: {Prompt}", taskId, prompt);

                    _logger.Debug("[{TaskId}] Sending prompt to Ollama for code generation", taskId);
                    var response = await _ollama.SendPromptAsync<SingleTaskResponse>(prompt);

                    _logger.Debug("[{TaskId}] Response received - FilePath: {FilePath}, Operation: {Operation}, Code length: {CodeLength} chars",
                        taskId, response.FilePath, response.OperationString, response.Code?.Length ?? 0);

                    // Use the response file path if provided, otherwise use the task's target
                    var finalTargetFile = !string.IsNullOrWhiteSpace(response.FilePath)
                        ? EnsureFileExtension(response.FilePath)
                        : targetFile;

                    _logger.Debug("[{TaskId}] Final target file: {FinalTargetFile}", taskId, finalTargetFile);

                    if (string.IsNullOrWhiteSpace(finalTargetFile) || !finalTargetFile.Contains('.'))
                    {
                        _logger.Warning("[{TaskId}] Invalid file path: {FilePath}", taskId, finalTargetFile);

                        if (attempt < MAX_RETRIES)
                        {
                            _logger.Information("[{TaskId}] Will retry due to invalid file path", taskId);
                            Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: Invalid file path, retrying...");
                            continue;
                        }

                        _logger.Error("[{TaskId}] Task {TaskNumber} failed after {Attempts} attempts: Invalid file path",
                            taskId, taskNumber, attempt);
                        Console.WriteLine($"✗ Task {taskNumber} Failed: Invalid file path '{finalTargetFile}'.");
                        return;
                    }

                    switch (response.Operation)
                    {
                        case FileOperation.Create:
                        case FileOperation.Update:
                            if (string.IsNullOrWhiteSpace(response.Code))
                            {
                                _logger.Warning("[{TaskId}] No code generated for {Operation} operation",
                                    taskId, response.Operation);

                                if (attempt < MAX_RETRIES)
                                {
                                    _logger.Information("[{TaskId}] Will retry due to missing code", taskId);
                                    Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: No code generated, retrying...");
                                    continue;
                                }

                                _logger.Error("[{TaskId}] Task {TaskNumber} failed: No code generated after {Attempts} attempts",
                                    taskId, taskNumber, attempt);
                                Console.WriteLine($"✗ Task {taskNumber} Failed: No code generated");
                                return;
                            }

                            // Log the first part of the generated code for debugging
                            _logger.Debug("[{TaskId}] Generated code preview (first 500 chars): {CodePreview}",
                                taskId, response.Code.Substring(0, Math.Min(500, response.Code.Length)));

                            // Clean and validate the code
                            var cleanedCode = CleanGeneratedCode(response.Code);
                            _logger.Debug("[{TaskId}] Code cleaned, original length: {Original}, cleaned length: {Cleaned}",
                                taskId, response.Code.Length, cleanedCode.Length);

                            _logger.Information("[{TaskId}] Writing {ByteCount} bytes to {FilePath}",
                                taskId, cleanedCode.Length, finalTargetFile);

                            _fileSystem.WriteFile(finalTargetFile, cleanedCode);

                            attemptStopwatch.Stop();
                            _logger.Information("[{TaskId}] Task {TaskNumber} completed successfully in {ElapsedMs}ms - {Operation}d {FilePath}",
                                taskId, taskNumber, attemptStopwatch.ElapsedMilliseconds, response.Operation, finalTargetFile);

                            Console.WriteLine($"✓ Task {taskNumber} Complete - {response.Operation}d {finalTargetFile}");
                            break;

                        case FileOperation.Delete:
                            _logger.Information("[{TaskId}] Deleting file: {FilePath}", taskId, finalTargetFile);
                            _fileSystem.DeleteFile(finalTargetFile);

                            attemptStopwatch.Stop();
                            _logger.Information("[{TaskId}] Task {TaskNumber} completed successfully in {ElapsedMs}ms - Deleted {FilePath}",
                                taskId, taskNumber, attemptStopwatch.ElapsedMilliseconds, finalTargetFile);

                            Console.WriteLine($"✓ Task {taskNumber} Complete - Deleted {finalTargetFile}");
                            break;
                    }

                    if (!string.IsNullOrWhiteSpace(response.Explanation))
                    {
                        _logger.Debug("[{TaskId}] Task explanation: {Explanation}", taskId, response.Explanation);
                        Console.WriteLine($"  → {response.Explanation}");
                    }

                    stopwatch.Stop();
                    _logger.Information("[{TaskId}] Task {TaskNumber} fully completed in {TotalMs}ms after {Attempts} attempt(s)",
                        taskId, taskNumber, stopwatch.ElapsedMilliseconds, attempt);

                    // Success - exit retry loop
                    return;
                }
                catch (Exception ex)
                {
                    attemptStopwatch.Stop();
                    _logger.Error(ex, "[{TaskId}] Task {TaskNumber} attempt {Attempt} failed after {ElapsedMs}ms",
                        taskId, taskNumber, attempt, attemptStopwatch.ElapsedMilliseconds);

                    if (attempt < MAX_RETRIES)
                    {
                        var delay = 1000 * attempt;
                        _logger.Information("[{TaskId}] Will retry after {DelayMs}ms delay", taskId, delay);
                        Console.WriteLine($"  Retry {attempt}/{MAX_RETRIES}: {ex.Message}");
                        await Task.Delay(delay); // Exponential backoff
                    }
                    else
                    {
                        stopwatch.Stop();
                        _logger.Error("[{TaskId}] Task {TaskNumber} failed permanently after {Attempts} attempts and {TotalMs}ms total time",
                            taskId, taskNumber, MAX_RETRIES, stopwatch.ElapsedMilliseconds);
                        Console.WriteLine($"✗ Task {taskNumber} Failed after {MAX_RETRIES} attempts: {ex.Message}");
                    }
                }
            }
        }

        private string BuildTaskPrompt(TaskDefinition task, string context, bool isRetry = false)
        {
            var targetFile = EnsureFileExtension(task.TargetFile);

            _logger.Debug("Building task prompt - Target: {TargetFile}, IsRetry: {IsRetry}", targetFile, isRetry);

            var retryInstructions = isRetry ? @"
IMPORTANT: This is a retry. Please ensure:
1. The response is COMPLETE and not truncated
2. All code is properly formatted
3. The JSON structure is valid" : "";

            // Determine the file type for better context
            var fileTypeHint = GetFileTypeHint(targetFile);
            _logger.Debug("File type hint: {FileTypeHint} for file: {TargetFile}", fileTypeHint, targetFile);

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
            var hint = extension switch
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

            _logger.Verbose("File type hint for extension {Extension}: {Hint}", extension, hint);
            return hint;
        }

        private string CleanGeneratedCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                _logger.Warning("CleanGeneratedCode called with null or empty code");
                return code;
            }

            var originalLength = code.Length;

            // Unescape common escape sequences
            code = code.Replace("\\n", "\n")
                      .Replace("\\r", "\r")
                      .Replace("\\t", "\t")
                      .Replace("\\\"", "\"");

            // Remove any potential JSON formatting artifacts
            if (code.StartsWith("\"") && code.EndsWith("\""))
            {
                code = code.Substring(1, code.Length - 2);
                _logger.Debug("Removed surrounding quotes from code");
            }

            // Fix common formatting issues
            code = System.Text.RegularExpressions.Regex.Replace(code, @"\\u([0-9a-fA-F]{4})", m =>
            {
                var hex = m.Groups[1].Value;
                var codePoint = Convert.ToInt32(hex, 16);
                return char.ConvertFromUtf32(codePoint);
            });

            if (originalLength != code.Length)
            {
                _logger.Debug("Code cleaned - Original length: {Original}, Cleaned length: {Cleaned}, Difference: {Diff}",
                    originalLength, code.Length, originalLength - code.Length);
            }

            return code;
        }

        private string EnsureFileExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.Warning("EnsureFileExtension called with null or empty path");
                return filePath;
            }

            // If already has an extension, return as-is
            if (Path.HasExtension(filePath))
            {
                _logger.Verbose("File path already has extension: {FilePath}", filePath);
                return filePath;
            }

            // Try to infer extension from path or context
            var lowerPath = filePath.ToLower();
            string inferredExtension = null;

            if (lowerPath.Contains("controller"))
                inferredExtension = ".cs";
            else if (lowerPath.Contains("model"))
                inferredExtension = ".cs";
            else if (lowerPath.Contains("service"))
                inferredExtension = ".cs";
            else if (lowerPath.Contains("view") || lowerPath.Contains("page"))
                inferredExtension = ".cshtml";
            else if (lowerPath.Contains("script"))
                inferredExtension = ".js";
            else if (lowerPath.Contains("style"))
                inferredExtension = ".css";
            else
                inferredExtension = ".cs"; // Default to .cs for C# projects

            _logger.Information("Inferred extension {Extension} for file path: {FilePath}",
                inferredExtension, filePath);

            return filePath + inferredExtension;
        }
    }
}