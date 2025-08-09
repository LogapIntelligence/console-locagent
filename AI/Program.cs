using AI.Models;
using AI.Services;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ILogger _logger;

        public AgenticProgrammingApp(bool debugMode = false)
        {
            var model = "gpt-oss:20b";
            _ollama = new OllamaService(model: model, debugMode: debugMode);
            _fileSystem = new FileSystemService();
            _taskExecutor = new TaskExecutor(_ollama, _fileSystem);
            _logger = Log.ForContext<AgenticProgrammingApp>();

            _logger.Information("AgenticProgrammingApp initialized with model: {Model}, debugMode: {DebugMode}", model, debugMode);
        }

        public async Task RunAsync()
        {
            _logger.Information("Starting Agentic Atomic Programming Console");
            _logger.Information("Working Directory: {WorkingDirectory}", _fileSystem.WorkingDirectory);

            Console.WriteLine("=== Agentic Atomic Programming Console ===");
            Console.WriteLine($"Working Directory: {_fileSystem.WorkingDirectory}");
            Console.WriteLine("Commands: 'cd <path>', 'test', 'exit'\n");

            while (true)
            {
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                _logger.Information("User input received: {Input}", input);

                if (input.ToLower() == "exit")
                {
                    _logger.Information("Exit command received, shutting down");
                    break;
                }

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
                        _logger.Information("Attempting to change directory to: {Path}", path);
                        _fileSystem.ChangeDirectory(path);
                        Console.WriteLine($"Changed to: {_fileSystem.WorkingDirectory}");
                        _logger.Information("Successfully changed directory to: {NewPath}", _fileSystem.WorkingDirectory);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to change directory");
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                    continue;
                }

                await ProcessPromptAsync(input);
            }

            _logger.Information("Application shutting down");
        }

        private async Task TestOllamaConnection()
        {
            _logger.Information("Testing Ollama connection");
            Console.WriteLine("\nTesting Ollama connection...");

            try
            {
                var testPrompt = @"Return this exact JSON: {""status"": ""ok"", ""message"": ""Ollama is working""}";
                _logger.Debug("Sending test prompt: {Prompt}", testPrompt);

                var result = await _ollama.SendPromptAsync<Dictionary<string, string>>(testPrompt);

                _logger.Information("Ollama test successful: {Message}", result["message"]);
                Console.WriteLine($"✓ Ollama test successful: {result["message"]}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Ollama test failed");
                Console.WriteLine($"✗ Ollama test failed: {ex.Message}");
                Console.WriteLine("Make sure Ollama is running: ollama serve");
            }
        }

        private async Task ProcessPromptAsync(string userPrompt)
        {
            var stopwatch = Stopwatch.StartNew();
            var processId = Guid.NewGuid().ToString("N").Substring(0, 8);

            _logger.Information("[{ProcessId}] Starting to process prompt: {Prompt}", processId, userPrompt);

            try
            {
                Console.WriteLine("\nAnalyzing Request...");
                _logger.Debug("[{ProcessId}] Classifying prompt", processId);

                var classification = await ClassifyPromptAsync(userPrompt);

                _logger.Information("[{ProcessId}] Prompt classified as: {Type} - Reasoning: {Reasoning}",
                    processId, classification.Type, classification.Reasoning);

                if (classification.Type == PromptType.QuestionAnswer)
                {
                    _logger.Information("[{ProcessId}] Handling as Question/Answer prompt", processId);
                    await HandleQuestionAnswerAsync(userPrompt, processId);
                }
                else
                {
                    _logger.Information("[{ProcessId}] Handling as Coding prompt", processId);
                    await HandleCodingPromptAsync(userPrompt, processId);
                }

                stopwatch.Stop();
                _logger.Information("[{ProcessId}] Prompt processing completed in {ElapsedMs}ms",
                    processId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error(ex, "[{ProcessId}] Error processing prompt after {ElapsedMs}ms",
                    processId, stopwatch.ElapsedMilliseconds);
                Console.WriteLine($"\nError processing prompt: {ex.Message}");
            }
        }

        private async Task<ClassificationResponse> ClassifyPromptAsync(string prompt)
        {
            _logger.Debug("Classifying prompt: {Prompt}", prompt.Substring(0, Math.Min(100, prompt.Length)));

            var classificationPrompt = $@"Classify this request as either Coding or QuestionAnswer.

Request: ""{prompt}""

Rules:
- If it creates/updates/deletes files: Coding
- If it asks about existing code: QuestionAnswer

Return JSON:
{{""type"": ""Coding"", ""reasoning"": ""creates new files""}}";

            return await _ollama.SendPromptAsync<ClassificationResponse>(classificationPrompt);
        }

        private async Task HandleQuestionAnswerAsync(string prompt, string processId = null)
        {
            processId = processId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            _logger.Information("[{ProcessId}] Starting Question/Answer handling", processId);

            Console.WriteLine("Getting Related Context...");

            var fileStructure = _fileSystem.GetFileStructure();
            _logger.Debug("[{ProcessId}] Found {FileCount} files in project", processId, fileStructure.Count);

            var context = await GetRelevantContextAsync(prompt, fileStructure, processId);
            _logger.Debug("[{ProcessId}] Context retrieved, length: {ContextLength} chars", processId, context.Length);

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

            _logger.Debug("[{ProcessId}] Sending answer prompt", processId);
            var response = await _ollama.SendPromptAsync<GeneralAnswerResponse>(answerPrompt);

            _logger.Information("[{ProcessId}] Answer generated with {ReferenceCount} references",
                processId, response.References?.Count ?? 0);

            Console.WriteLine($"\n{response.Answer}");
            if (response.References?.Any() == true)
            {
                Console.WriteLine($"\nReferences: {string.Join(", ", response.References)}");
            }
        }

        private async Task HandleCodingPromptAsync(string prompt, string processId = null)
        {
            processId = processId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            _logger.Information("[{ProcessId}] Starting Coding task handling", processId);

            Console.WriteLine("Getting Related Context...");

            var fileStructure = _fileSystem.GetFileStructure();
            _logger.Information("[{ProcessId}] Found {FileCount} files in project", processId, fileStructure.Count);
            Console.WriteLine($"Found {fileStructure.Count} files in project");

            ShowSampleFiles(fileStructure);

            var context = await GetRelevantContextAsync(prompt, fileStructure, processId);
            _logger.Debug("[{ProcessId}] Context retrieved, length: {ContextLength} chars", processId, context.Length);

            Console.WriteLine("Creating Tasks...");
            _logger.Information("[{ProcessId}] Generating task list", processId);

            var taskList = await GenerateTaskListAsync(prompt, context, fileStructure, processId);

            if (taskList.Tasks == null || !taskList.Tasks.Any())
            {
                _logger.Warning("[{ProcessId}] No tasks generated", processId);
                Console.WriteLine("No tasks generated. Try being more specific.");
                return;
            }

            _logger.Information("[{ProcessId}] Generated {TaskCount} tasks. Summary: {Summary}",
                processId, taskList.Tasks.Count, taskList.Summary);

            Console.WriteLine($"\n{taskList.Tasks.Count} Tasks Queued");
            Console.WriteLine($"Summary: {taskList.Summary ?? "No summary"}");

            ShowPlannedTasks(taskList.Tasks);

            for (int i = 0; i < taskList.Tasks.Count; i++)
            {
                var task = taskList.Tasks[i];
                if (string.IsNullOrWhiteSpace(task.TargetFile))
                {
                    _logger.Warning("[{ProcessId}] Task {TaskNumber} skipped - no target file", processId, i + 1);
                    Console.WriteLine($"Task {i + 1} skipped - no target file");
                    continue;
                }

                _logger.Information("[{ProcessId}] Executing task {TaskNumber}/{TotalTasks}: {TaskName} -> {TargetFile}",
                    processId, i + 1, taskList.Tasks.Count, task.TaskName, task.TargetFile);

                await _taskExecutor.ExecuteTaskAsync(task, context, i + 1, taskList.Tasks.Count);
            }

            _logger.Information("[{ProcessId}] All {TaskCount} tasks completed", processId, taskList.Tasks.Count);
            Console.WriteLine("\n✓ All tasks completed!");
        }

        private void ShowSampleFiles(List<string> fileStructure)
        {
            var exampleFiles = fileStructure.Where(f => f.Contains('.')).Take(5).ToList();
            if (exampleFiles.Any())
            {
                _logger.Debug("Sample files: {Files}", string.Join(", ", exampleFiles));
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
                var logMessage = $"{task.TaskName} -> {task.TargetFile} ({task.OperationString})";
                _logger.Debug("Planned task: {TaskDetails}", logMessage);
                Console.WriteLine($"  - {logMessage}");
            }
            Console.WriteLine();
        }

        private async Task<string> GetRelevantContextAsync(string prompt, List<string> fileStructure, string processId = null)
        {
            processId = processId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            _logger.Debug("[{ProcessId}] Getting relevant context for prompt", processId);

            var actualFiles = fileStructure.Where(f => f.Contains('.')).Take(50).ToList();
            _logger.Debug("[{ProcessId}] Considering {FileCount} actual files for context", processId, actualFiles.Count);

            var contextPrompt = $@"Find files relevant to: {prompt}

Available files:
{string.Join("\n", actualFiles)}

Return JSON with COMPLETE file paths:
{{
  ""relevant_files"": [""Views/Home/Index.cshtml"", ""Controllers/HomeController.cs""],
  ""reasoning"": ""These files control the home page""
}}";

            var response = await _ollama.SendPromptAsync<ContextRetrievalResponse>(contextPrompt);

            _logger.Information("[{ProcessId}] Found {FileCount} relevant files. Reasoning: {Reasoning}",
                processId, response.RelevantFiles?.Count ?? 0, response.Reasoning);

            var contextBuilder = new StringBuilder();
            var filesRead = 0;

            foreach (var file in response.RelevantFiles?.Take(10) ?? Enumerable.Empty<string>())
            {
                if (!file.Contains('.'))
                {
                    _logger.Warning("[{ProcessId}] Skipping invalid path: {File}", processId, file);
                    Console.WriteLine($"  Skipping invalid path: {file}");
                    continue;
                }

                var content = _fileSystem.ReadFile(file);
                if (content != null)
                {
                    filesRead++;
                    var truncatedContent = content.Length > 1000
                        ? content.Substring(0, 1000) + "\n... [truncated]"
                        : content;

                    _logger.Debug("[{ProcessId}] Added file to context: {File} ({Length} chars, truncated: {IsTruncated})",
                        processId, file, content.Length, content.Length > 1000);

                    contextBuilder.AppendLine($"\n=== {file} ===");
                    contextBuilder.AppendLine(truncatedContent);
                }
                else
                {
                    _logger.Warning("[{ProcessId}] Could not read file: {File}", processId, file);
                }
            }

            _logger.Information("[{ProcessId}] Context built from {FileCount} files, total length: {Length} chars",
                processId, filesRead, contextBuilder.Length);

            return contextBuilder.ToString();
        }

        private async Task<TaskListResponse> GenerateTaskListAsync(string prompt, string context, List<string> fileStructure, string processId = null)
        {
            processId = processId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            _logger.Debug("[{ProcessId}] Generating task list", processId);

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

            _logger.Debug("[{ProcessId}] Task generation prompt prepared, length: {Length} chars",
                processId, taskPrompt.Length);

            return await _ollama.SendPromptAsync<TaskListResponse>(taskPrompt);
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            // Configure Serilog
            var logLevel = LogEventLevel.Information;

            Console.Write("Select log level (1=Verbose, 2=Debug, 3=Information, 4=Warning, 5=Error) [default: 3]: ");
            var levelInput = Console.ReadLine();
            if (!string.IsNullOrEmpty(levelInput) && int.TryParse(levelInput, out var level))
            {
                logLevel = level switch
                {
                    1 => LogEventLevel.Verbose,
                    2 => LogEventLevel.Debug,
                    3 => LogEventLevel.Information,
                    4 => LogEventLevel.Warning,
                    5 => LogEventLevel.Error,
                    _ => LogEventLevel.Information
                };
            }

            // Create logs directory if it doesn't exist
            var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logsDirectory);

            // Configure Serilog with both console and file sinks
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    Path.Combine(logsDirectory, "ai-app-.txt"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    retainedFileCountLimit: 7)
                .Enrich.FromLogContext()
                .CreateLogger();

            Log.Information("Application starting up");
            Log.Information("Log level set to: {LogLevel}", logLevel);
            Log.Information("Log files will be written to: {LogsDirectory}", logsDirectory);

            try
            {
                Console.Write("Enable debug mode? (y/n, default: n): ");
                var debugInput = Console.ReadLine();
                var debugMode = debugInput?.ToLower() == "y";

                Log.Information("Debug mode: {DebugMode}", debugMode);

                var app = new AgenticProgrammingApp(debugMode);
                await app.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error occurred");
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
            finally
            {
                Log.Information("Application shutting down");
                Log.CloseAndFlush();
            }
        }
    }
}