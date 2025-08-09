using AI.Models;
using AI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AgenticProgramming
{
    public class AgenticProgrammingApp
    {
        private readonly IOllamaService _ollama;
        private readonly IFileSystemService _fileSystem;
        private readonly ITaskExecutor _taskExecutor;

        public AgenticProgrammingApp(bool debugMode = false)
        {
            var model = "gpt-oss:20b";
            _ollama = new OllamaService(model: model, debugMode: debugMode);
            _fileSystem = new FileSystemService();
            _taskExecutor = new TaskExecutor(_ollama, _fileSystem);
        }

        public async Task RunAsync()
        {
            Console.WriteLine("=== Agentic Atomic Programming Console ===");
            Console.WriteLine($"Working Directory: {_fileSystem.WorkingDirectory}");
            Console.WriteLine("Commands: 'cd <path>', 'test', 'exit'\n");

            while (true)
            {
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                if (input.ToLower() == "exit")
                    break;

                if (input.ToLower() == "test")
                {
                    await TestOllamaConnection();
                    continue;
                }

                if (input.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var path = input.Substring(3).Trim();
                        _fileSystem.ChangeDirectory(path);
                        Console.WriteLine($"Changed to: {_fileSystem.WorkingDirectory}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                    continue;
                }

                await ProcessPromptAsync(input);
            }
        }

        private async Task TestOllamaConnection()
        {
            Console.WriteLine("\nTesting Ollama connection...");
            try
            {
                var testPrompt = @"Return this exact JSON: {""status"": ""ok"", ""message"": ""Ollama is working""}";
                var result = await _ollama.SendPromptAsync<Dictionary<string, string>>(testPrompt);
                Console.WriteLine($"✓ Ollama test successful: {result["message"]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Ollama test failed: {ex.Message}");
                Console.WriteLine("Make sure Ollama is running: ollama serve");
            }
        }

        private async Task ProcessPromptAsync(string userPrompt)
        {
            try
            {
                Console.WriteLine("\nAnalyzing Request...");
                var classification = await ClassifyPromptAsync(userPrompt);

                if (classification.Type == PromptType.QuestionAnswer)
                {
                    await HandleQuestionAnswerAsync(userPrompt);
                }
                else
                {
                    await HandleCodingPromptAsync(userPrompt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError processing prompt: {ex.Message}");
            }
        }

        private async Task<ClassificationResponse> ClassifyPromptAsync(string prompt)
        {
            var classificationPrompt = $@"Classify this request as either Coding or QuestionAnswer.

Request: ""{prompt}""

Rules:
- If it creates/updates/deletes files: Coding
- If it asks about existing code: QuestionAnswer

Return JSON:
{{""type"": ""Coding"", ""reasoning"": ""creates new files""}}";

            return await _ollama.SendPromptAsync<ClassificationResponse>(classificationPrompt);
        }

        private async Task HandleQuestionAnswerAsync(string prompt)
        {
            Console.WriteLine("Getting Related Context...");

            var fileStructure = _fileSystem.GetFileStructure();
            var context = await GetRelevantContextAsync(prompt, fileStructure);

            Console.WriteLine("Generating Answer...");

            var answerPrompt = $@"Answer: {prompt}

Files:
{string.Join("\n", fileStructure.Take(30))}

Context:
{context}

Return JSON:
{{
  ""answer"": ""detailed answer here"",
  ""references"": [""file1.cs"", ""file2.cs""]
}}";

            var response = await _ollama.SendPromptAsync<GeneralAnswerResponse>(answerPrompt);

            Console.WriteLine($"\n{response.Answer}");
            if (response.References.Any())
            {
                Console.WriteLine($"\nReferences: {string.Join(", ", response.References)}");
            }
        }

        private async Task HandleCodingPromptAsync(string prompt)
        {
            Console.WriteLine("Getting Related Context...");

            var fileStructure = _fileSystem.GetFileStructure();
            Console.WriteLine($"Found {fileStructure.Count} files in project");

            ShowSampleFiles(fileStructure);

            var context = await GetRelevantContextAsync(prompt, fileStructure);

            Console.WriteLine("Creating Tasks...");

            var taskList = await GenerateTaskListAsync(prompt, context, fileStructure);

            if (taskList.Tasks == null || !taskList.Tasks.Any())
            {
                Console.WriteLine("No tasks generated. Try being more specific.");
                return;
            }

            Console.WriteLine($"\n{taskList.Tasks.Count} Tasks Queued");
            Console.WriteLine($"Summary: {taskList.Summary ?? "No summary"}");

            ShowPlannedTasks(taskList.Tasks);

            for (int i = 0; i < taskList.Tasks.Count; i++)
            {
                var task = taskList.Tasks[i];
                if (string.IsNullOrWhiteSpace(task.TargetFile))
                {
                    Console.WriteLine($"Task {i + 1} skipped - no target file");
                    continue;
                }

                await _taskExecutor.ExecuteTaskAsync(task, context, i + 1, taskList.Tasks.Count);
            }

            Console.WriteLine("\n✓ All tasks completed!");
        }

        private void ShowSampleFiles(List<string> fileStructure)
        {
            var exampleFiles = fileStructure.Where(f => f.Contains('.')).Take(5).ToList();
            if (exampleFiles.Any())
            {
                Console.WriteLine("Example files in project:");
                foreach (var file in exampleFiles)
                {
                    Console.WriteLine($"  - {file}");
                }
            }
        }

        private void ShowPlannedTasks(List<TaskDefinition> tasks)
        {
            Console.WriteLine("\nPlanned tasks:");
            foreach (var task in tasks)
            {
                Console.WriteLine($"  - {task.TaskName} -> {task.TargetFile} ({task.OperationString})");
            }
            Console.WriteLine();
        }

        private async Task<string> GetRelevantContextAsync(string prompt, List<string> fileStructure)
        {
            var actualFiles = fileStructure.Where(f => f.Contains('.')).Take(50).ToList();

            var contextPrompt = $@"Find files relevant to: {prompt}

Available files:
{string.Join("\n", actualFiles)}

Return JSON with COMPLETE file paths:
{{
  ""relevant_files"": [""Views/Home/Index.cshtml"", ""Controllers/HomeController.cs""],
  ""reasoning"": ""These files control the home page""
}}";

            var response = await _ollama.SendPromptAsync<ContextRetrievalResponse>(contextPrompt);

            var contextBuilder = new StringBuilder();
            foreach (var file in response.RelevantFiles.Take(10))
            {
                if (!file.Contains('.'))
                {
                    Console.WriteLine($"  Skipping invalid path: {file}");
                    continue;
                }

                var content = _fileSystem.ReadFile(file);
                if (content != null)
                {
                    contextBuilder.AppendLine($"\n=== {file} ===");
                    var truncatedContent = content.Length > 1000
                        ? content.Substring(0, 1000) + "\n... [truncated]"
                        : content;
                    contextBuilder.AppendLine(truncatedContent);
                }
            }

            return contextBuilder.ToString();
        }

        private async Task<TaskListResponse> GenerateTaskListAsync(string prompt, string context, List<string> fileStructure)
        {
            var taskPrompt = $@"Generate atomic tasks to: {prompt}

Current files:
{string.Join("\n", fileStructure.Take(20))}

Context:
{context}

Each task = ONE file operation with FULL paths including extensions!

Return JSON:
{{
  ""tasks"": [
    {{
      ""task_name"": ""Create Food Model"",
      ""target_file"": ""Models/Food.cs"",
      ""operation"": ""Create"",
      ""detailed_prompt"": ""Create a Food model class with properties: Id, Name, Description, Price, Category"",
      ""dependencies"": []
    }}
  ],
  ""summary"": ""Create food model for the application""
}}";

            return await _ollama.SendPromptAsync<TaskListResponse>(taskPrompt);
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.Write("Enable debug mode? (y/n, default: n): ");
                var debugInput = Console.ReadLine();
                var debugMode = debugInput?.ToLower() == "y";

                var app = new AgenticProgrammingApp(debugMode);
                await app.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
        }
    }
}