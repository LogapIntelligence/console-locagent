using AI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // Increased timeout
            _model = model;
            _baseUrl = baseUrl;
            _debugMode = debugMode;
            _jsonParser = new JsonParser();
        }

        public async Task<T> SendPromptAsync<T>(string prompt, bool stream = false)
        {
            var attempts = new[] { false, true }; // Try without JSON format first
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
                        stream = false, // Always use non-streaming for complete responses
                        format = useJsonFormat ? "json" : null,
                        options = new
                        {
                            temperature = 0.1,  // Very low for consistency
                            top_p = 0.9,
                            seed = 42,
                            repeat_penalty = 1.0, // Reduced to avoid truncation
                            num_predict = 4096, // Increased for larger responses
                            stop = new[] { "```", "\n\n\n" } // Stop sequences to prevent markdown
                        }
                    };

                    var json = JsonSerializer.Serialize(request);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);
                    response.EnsureSuccessStatusCode();

                    var responseText = await response.Content.ReadAsStringAsync();

                    // Handle streaming responses if accidentally enabled
                    var fullResponse = new StringBuilder();
                    if (responseText.Contains("\n"))
                    {
                        var lines = responseText.Split('\n');
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                try
                                {
                                    var chunk = JsonSerializer.Deserialize<OllamaResponse>(line);
                                    if (chunk?.Response != null)
                                    {
                                        fullResponse.Append(chunk.Response);
                                    }
                                }
                                catch
                                {
                                    // Skip invalid chunks
                                }
                            }
                        }
                        lastResponse = fullResponse.ToString();
                    }
                    else
                    {
                        var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseText);
                        lastResponse = ollamaResponse?.Response;
                    }

                    if (_debugMode)
                    {
                        Console.WriteLine($"[DEBUG] Raw Response (attempt {(useJsonFormat ? "with" : "without")} JSON format):");
                        Console.WriteLine(lastResponse?.Substring(0, Math.Min(500, lastResponse?.Length ?? 0)));
                    }

                    // Try to extract and clean JSON
                    var cleanedResponse = ExtractAndCleanJson(lastResponse);

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

            // If all attempts fail, try to create a partial response from what we have
            if (!string.IsNullOrEmpty(lastResponse))
            {
                var partialResponse = TryCreatePartialResponse<T>(lastResponse);
                if (partialResponse != null)
                {
                    Console.WriteLine("Warning: Using partial response due to parsing errors");
                    return partialResponse;
                }
            }

            Console.WriteLine($"Warning: Could not parse JSON response. Error: {lastException?.Message}");
            return CreateDefaultResponse<T>();
        }

        private string ExtractAndCleanJson(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return null;

            // Remove any markdown code blocks
            response = System.Text.RegularExpressions.Regex.Replace(
                response, @"```json?\s*\n?|\s*```", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Find JSON boundaries
            int startIndex = response.IndexOf('{');
            int endIndex = response.LastIndexOf('}');

            if (startIndex >= 0 && endIndex > startIndex)
            {
                var jsonCandidate = response.Substring(startIndex, endIndex - startIndex + 1);

                // Escape unescaped newlines and other special characters
                jsonCandidate = EscapeJsonString(jsonCandidate);

                return _jsonParser.ExtractJson(jsonCandidate);
            }

            return null;
        }

        private string EscapeJsonString(string json)
        {
            // This is a more careful approach to escaping
            var result = new StringBuilder();
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    result.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    result.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    result.Append(c);
                    continue;
                }

                if (inString)
                {
                    switch (c)
                    {
                        case '\n':
                            result.Append("\\n");
                            break;
                        case '\r':
                            result.Append("\\r");
                            break;
                        case '\t':
                            result.Append("\\t");
                            break;
                        default:
                            result.Append(c);
                            break;
                    }
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        private T TryCreatePartialResponse<T>(string partialResponse)
        {
            var type = typeof(T);

            if (type == typeof(SingleTaskResponse))
            {
                // Try to extract code from partial response
                var codeMatch = System.Text.RegularExpressions.Regex.Match(
                    partialResponse,
                    @"""code""\s*:\s*""([^""]*(?:\\.[^""]*)*)""",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var filePathMatch = System.Text.RegularExpressions.Regex.Match(
                    partialResponse,
                    @"""file_path""\s*:\s*""([^""]+)""");

                if (codeMatch.Success || filePathMatch.Success)
                {
                    var response = new SingleTaskResponse
                    {
                        Code = codeMatch.Success ?
                            System.Text.RegularExpressions.Regex.Unescape(codeMatch.Groups[1].Value) : "",
                        FilePath = filePathMatch.Success ? filePathMatch.Groups[1].Value : "",
                        OperationString = "Update",
                        Explanation = "Partial response recovered"
                    };
                    return (T)(object)response;
                }
            }

            return default(T);
        }

        private string EnhancePromptForJson(string prompt)
        {
            return $@"{prompt}

CRITICAL: Return ONLY a valid JSON object. No markdown, no code blocks, no explanations outside the JSON.

The JSON must:
1. Start with {{ and end with }}
2. Use double quotes for ALL strings
3. Properly escape special characters (\n for newlines, \"" for quotes)
                4.Have no trailing commas
5.Be complete and valid

DO NOT include ```json or ``` or any other markdown.
DO NOT truncate or abbreviate the response.
ENSURE all string values are properly escaped.";
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