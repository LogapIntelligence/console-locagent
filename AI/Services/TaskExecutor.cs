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

            var prompt = BuildTaskPrompt(task, originalContext);

            try
            {
                var response = await _ollama.SendPromptAsync<SingleTaskResponse>(prompt);

                var targetFile = !string.IsNullOrWhiteSpace(response.FilePath)
                    ? response.FilePath
                    : task.TargetFile;

                if (string.IsNullOrWhiteSpace(targetFile) || !targetFile.Contains('.'))
                {
                    Console.WriteLine($"✗ Task {taskNumber} Failed: Invalid file path '{targetFile}'.");
                    return;
                }

                switch (response.Operation)
                {
                    case FileOperation.Create:
                    case FileOperation.Update:
                        if (string.IsNullOrWhiteSpace(response.Code))
                        {
                            Console.WriteLine($"✗ Task {taskNumber} Failed: No code generated");
                            return;
                        }
                        _fileSystem.WriteFile(targetFile, response.Code);
                        Console.WriteLine($"✓ Task {taskNumber} Complete - {response.Operation}d {targetFile}");
                        break;

                    case FileOperation.Delete:
                        _fileSystem.DeleteFile(targetFile);
                        Console.WriteLine($"✓ Task {taskNumber} Complete - Deleted {targetFile}");
                        break;
                }

                if (!string.IsNullOrWhiteSpace(response.Explanation))
                {
                    Console.WriteLine($"  → {response.Explanation}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Task {taskNumber} Failed: {ex.Message}");
            }
        }

        private string BuildTaskPrompt(TaskDefinition task, string context)
        {
            var targetFile = EnsureFileExtension(task.TargetFile);

            return $@"You are an expert programmer. Execute this atomic task.

TASK: {task.DetailedPrompt}
TARGET FILE: {targetFile}
OPERATION: {task.OperationString}

CONTEXT:
{context}

Generate COMPLETE code for this file. Return ONLY this JSON structure:
{{
  ""code"": ""// Complete file content here"",
  ""file_path"": ""{targetFile}"",
  ""operation"": ""{task.OperationString}"",
  ""explanation"": ""Brief explanation""
}}

The code field must contain the COMPLETE file content, not a snippet.";
        }

        private string EnsureFileExtension(string filePath)
        {
            if (!filePath.Contains('.'))
            {
                if (filePath.Contains("View") || filePath.Contains("view"))
                    return filePath + ".cshtml";
                else if (filePath.Contains("Controller") || filePath.Contains("Model"))
                    return filePath + ".cs";
                else
                    return filePath + ".cs";
            }
            return filePath;
        }
    }
}
