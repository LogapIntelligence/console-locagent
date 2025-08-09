using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AI.Models
{
    public enum ResponseType
    {
        ContextRetrieval,
        GeneralAnswer,
        TaskList,
        SingleTask,
        Classification
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PromptType
    {
        Coding,
        QuestionAnswer
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FileOperation
    {
        Create,
        Update,
        Delete
    }

    public class ClassificationResponse
    {
        [JsonPropertyName("type")]
        public string TypeString { get; set; }

        [JsonIgnore]
        public PromptType Type => System.Enum.TryParse<PromptType>(TypeString, true, out var result) ? result : PromptType.Coding;

        [JsonPropertyName("reasoning")]
        public string Reasoning { get; set; }
    }

    public class ContextRetrievalResponse
    {
        [JsonPropertyName("relevant_files")]
        public List<string> RelevantFiles { get; set; } = new();

        [JsonPropertyName("reasoning")]
        public string Reasoning { get; set; }
    }

    public class TaskDefinition
    {
        [JsonPropertyName("task_name")]
        public string TaskName { get; set; }

        [JsonPropertyName("target_file")]
        public string TargetFile { get; set; }

        [JsonPropertyName("operation")]
        public string OperationString { get; set; }

        [JsonIgnore]
        public FileOperation Operation => System.Enum.TryParse<FileOperation>(OperationString, true, out var result) ? result : FileOperation.Update;

        [JsonPropertyName("detailed_prompt")]
        public string DetailedPrompt { get; set; }

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();
    }

    public class TaskListResponse
    {
        [JsonPropertyName("tasks")]
        public List<TaskDefinition> Tasks { get; set; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; }
    }

    public class SingleTaskResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; set; }

        [JsonPropertyName("file_path")]
        public string FilePath { get; set; }

        [JsonPropertyName("operation")]
        public string OperationString { get; set; }

        [JsonIgnore]
        public FileOperation Operation => System.Enum.TryParse<FileOperation>(OperationString, true, out var result) ? result : FileOperation.Update;

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; }
    }

    public class GeneralAnswerResponse
    {
        [JsonPropertyName("answer")]
        public string Answer { get; set; }

        [JsonPropertyName("references")]
        public List<string> References { get; set; } = new();
    }
}
