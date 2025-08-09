using AI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AI.Services
{
    public interface IOllamaService
    {
        Task<T> SendPromptAsync<T>(string prompt, bool stream = false);
    }

    public class OllamaService : IOllamaService
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly JsonParser _jsonParser;
        private readonly bool _debugMode;

        public OllamaService(string model = "gpt-oss:20b", string baseUrl = "http://localhost:11434", bool debugMode = false)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _model = model;
            _baseUrl = baseUrl;
            _debugMode = debugMode;
            _jsonParser = new JsonParser();
        }

        public async Task<T> SendPromptAsync<T>(string prompt, bool stream = false)
        {
            var attempts = new[] { true, false };
            Exception lastException = null;
            string lastResponse = null;

            foreach (var useJsonFormat in attempts)
            {
                try
                {
                    var enhancedPrompt = EnhancePromptForJson(prompt);

                    var request = new
                    {
                        model = _model,
                        prompt = enhancedPrompt,
                        stream = stream,
                        format = useJsonFormat ? "json" : null,
                        options = new
                        {
                            temperature = 0.2,  // Even lower for more consistency
                            top_p = 0.9,
                            seed = 42,
                            repeat_penalty = 1.1,
                            num_predict = 2048
                        }
                    };

                    var json = JsonSerializer.Serialize(request);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);
                    response.EnsureSuccessStatusCode();

                    var responseText = await response.Content.ReadAsStringAsync();
                    var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseText);
                    lastResponse = ollamaResponse.Response;

                    if (_debugMode)
                    {
                        Console.WriteLine($"[DEBUG] Raw Response (attempt {(useJsonFormat ? "with" : "without")} JSON format):");
                        Console.WriteLine(lastResponse?.Substring(0, Math.Min(500, lastResponse?.Length ?? 0)));
                    }

                    var cleanedResponse = _jsonParser.ExtractJson(ollamaResponse.Response);

                    if (string.IsNullOrWhiteSpace(cleanedResponse))
                    {
                        throw new JsonException("No valid JSON found in response");
                    }

                    return _jsonParser.DeserializeJson<T>(cleanedResponse);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (_debugMode)
                    {
                        Console.WriteLine($"[DEBUG] Attempt {(useJsonFormat ? "with" : "without")} JSON format failed: {ex.Message}");
                        if (!string.IsNullOrEmpty(lastResponse))
                        {
                            Console.WriteLine($"[DEBUG] Last response: {lastResponse}");
                        }
                    }
                }
            }

            // If all attempts fail, try to create a default response
            Console.WriteLine($"Warning: Could not parse JSON response. Error: {lastException?.Message}");
            return CreateDefaultResponse<T>();
        }

        private string EnhancePromptForJson(string prompt)
        {
            return $@"{prompt}

CRITICAL JSON FORMATTING RULES:
1. Return ONLY valid JSON with no text before or after
2. Start with {{ and end with }}
3. Use double quotes for all strings
4. No comments, no markdown, no explanations outside JSON
5. Escape special characters properly
6. No trailing commas

EXAMPLE valid response:
{{""field"": ""value"", ""field2"": ""value2""}}

DO NOT include any of these:
- Markdown code blocks (```json)
- Comments (// or /* */)
- Text explanations before or after the JSON
- Single quotes instead of double quotes";
        }

        private T CreateDefaultResponse<T>()
        {
            var type = typeof(T);

            if (type == typeof(ClassificationResponse))
                return (T)(object)new ClassificationResponse
                {
                    TypeString = "Coding",
                    Reasoning = "Failed to classify - defaulting to coding"
                };

            if (type == typeof(ContextRetrievalResponse))
                return (T)(object)new ContextRetrievalResponse
                {
                    RelevantFiles = new(),
                    Reasoning = "Could not determine relevant files"
                };

            if (type == typeof(TaskListResponse))
                return (T)(object)new TaskListResponse
                {
                    Tasks = new(),
                    Summary = "Failed to generate tasks"
                };

            if (type == typeof(GeneralAnswerResponse))
                return (T)(object)new GeneralAnswerResponse
                {
                    Answer = "Unable to process the question at this time.",
                    References = new()
                };

            if (type == typeof(SingleTaskResponse))
                return (T)(object)new SingleTaskResponse
                {
                    Code = "",
                    FilePath = "",
                    OperationString = "Update",
                    Explanation = "Failed to generate code"
                };

            throw new NotSupportedException($"No default response for type {type.Name}");
        }

        private class OllamaResponse
        {
            [JsonPropertyName("response")]
            public string Response { get; set; }

            [JsonPropertyName("done")]
            public bool Done { get; set; }
        }
    }
}
