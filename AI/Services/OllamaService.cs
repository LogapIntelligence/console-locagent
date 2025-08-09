using AI.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
        private readonly ILogger _logger;

        public OllamaService(string model = "gpt-oss:20b", string baseUrl = "http://localhost:11434", bool debugMode = false)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _model = model;
            _baseUrl = baseUrl;
            _debugMode = debugMode;
            _jsonParser = new JsonParser();
            _logger = Log.ForContext<OllamaService>();

            _logger.Information("OllamaService initialized with model: {Model}, baseUrl: {BaseUrl}, debugMode: {DebugMode}",
                _model, _baseUrl, _debugMode);
        }

        public async Task<T> SendPromptAsync<T>(string prompt, bool stream = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);

            _logger.Information("[{RequestId}] Starting SendPromptAsync for type {TypeName}", requestId, typeof(T).Name);
            _logger.Debug("[{RequestId}] Original prompt: {Prompt}", requestId, prompt);

            var attempts = new[] { false, true }; // Try without JSON format first
            Exception lastException = null;
            string lastResponse = null;

            foreach (var useJsonFormat in attempts)
            {
                var attemptNumber = useJsonFormat ? 2 : 1;
                _logger.Information("[{RequestId}] Attempt {AttemptNumber}/2 - JSON format: {UseJsonFormat}",
                    requestId, attemptNumber, useJsonFormat);

                try
                {
                    var enhancedPrompt = EnhancePromptForJson(prompt);
                    _logger.Debug("[{RequestId}] Enhanced prompt (first 500 chars): {Prompt}",
                        requestId, enhancedPrompt.Substring(0, Math.Min(500, enhancedPrompt.Length)));

                    var request = new
                    {
                        model = _model,
                        prompt = enhancedPrompt,
                        stream = false,
                        format = useJsonFormat ? "json" : null,
                        options = new
                        {
                            temperature = 0.5,
                            top_p = 0.9,
                            seed = 42,
                            repeat_penalty = 1.0,
                            num_predict = 128000,
                        }
                    };

                    var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true });
                    _logger.Debug("[{RequestId}] Request payload: {RequestJson}", requestId, json);

                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    _logger.Information("[{RequestId}] Sending HTTP POST to {Url}", requestId, $"{_baseUrl}/api/generate");
                    var httpStopwatch = Stopwatch.StartNew();

                    var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content);

                    httpStopwatch.Stop();
                    _logger.Information("[{RequestId}] HTTP response received in {ElapsedMs}ms - Status: {StatusCode}",
                        requestId, httpStopwatch.ElapsedMilliseconds, response.StatusCode);

                    response.EnsureSuccessStatusCode();

                    var responseText = await response.Content.ReadAsStringAsync();
                    _logger.Debug("[{RequestId}] Raw HTTP response (first 1000 chars): {Response}",
                        requestId, responseText.Substring(0, Math.Min(1000, responseText.Length)));

                    // Handle streaming responses if accidentally enabled
                    var fullResponse = new StringBuilder();
                    if (responseText.Contains("\n"))
                    {
                        _logger.Debug("[{RequestId}] Detected multi-line response, processing as stream", requestId);
                        var lines = responseText.Split('\n');
                        var lineCount = 0;

                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                lineCount++;
                                try
                                {
                                    var chunk = JsonSerializer.Deserialize<OllamaResponse>(line);
                                    if (chunk?.Response != null)
                                    {
                                        fullResponse.Append(chunk.Response);
                                        _logger.Verbose("[{RequestId}] Chunk {LineCount}: {ChunkLength} chars",
                                            requestId, lineCount, chunk.Response.Length);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warning("[{RequestId}] Failed to parse chunk {LineCount}: {Error}",
                                        requestId, lineCount, ex.Message);
                                }
                            }
                        }
                        lastResponse = fullResponse.ToString();
                        _logger.Debug("[{RequestId}] Assembled {LineCount} chunks into response of {TotalLength} chars",
                            requestId, lineCount, lastResponse.Length);
                    }
                    else
                    {
                        var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseText);
                        lastResponse = ollamaResponse?.Response;
                        _logger.Debug("[{RequestId}] Single response parsed, length: {Length} chars",
                            requestId, lastResponse?.Length ?? 0);
                    }

                    _logger.Information("[{RequestId}] Complete Ollama response received, length: {Length} chars",
                        requestId, lastResponse?.Length ?? 0);

                    if (_debugMode || _logger.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    {
                        _logger.Debug("[{RequestId}] Full Ollama response: {Response}", requestId, lastResponse);
                    }

                    // Try to extract and clean JSON
                    _logger.Debug("[{RequestId}] Attempting to extract JSON from response", requestId);
                    var cleanedResponse = ExtractAndCleanJson(lastResponse);

                    if (string.IsNullOrWhiteSpace(cleanedResponse))
                    {
                        _logger.Warning("[{RequestId}] No valid JSON found in response", requestId);
                        throw new JsonException("No valid JSON found in response");
                    }

                    _logger.Debug("[{RequestId}] Cleaned JSON (first 500 chars): {Json}",
                        requestId, cleanedResponse.Substring(0, Math.Min(500, cleanedResponse.Length)));

                    _logger.Debug("[{RequestId}] Attempting to deserialize to type {TypeName}", requestId, typeof(T).Name);
                    var result = _jsonParser.DeserializeJson<T>(cleanedResponse);

                    stopwatch.Stop();
                    _logger.Information("[{RequestId}] Successfully completed request in {ElapsedMs}ms",
                        requestId, stopwatch.ElapsedMilliseconds);

                    LogDeserializedResult(requestId, result);

                    return result;
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.Error(httpEx, "[{RequestId}] HTTP request failed on attempt {AttemptNumber}",
                        requestId, attemptNumber);
                    lastException = httpEx;
                }
                catch (TaskCanceledException tcEx)
                {
                    _logger.Error(tcEx, "[{RequestId}] Request timeout on attempt {AttemptNumber}",
                        requestId, attemptNumber);
                    lastException = tcEx;
                }
                catch (JsonException jsonEx)
                {
                    _logger.Error(jsonEx, "[{RequestId}] JSON parsing failed on attempt {AttemptNumber}. Response: {Response}",
                        requestId, attemptNumber, lastResponse);
                    lastException = jsonEx;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[{RequestId}] Unexpected error on attempt {AttemptNumber}",
                        requestId, attemptNumber);
                    lastException = ex;

                    if (!string.IsNullOrEmpty(lastResponse))
                    {
                        _logger.Debug("[{RequestId}] Last response for failed attempt: {Response}",
                            requestId, lastResponse);
                    }
                }
            }

            // If all attempts fail, try to create a partial response from what we have
            _logger.Warning("[{RequestId}] All attempts failed, trying to create partial response", requestId);

            if (!string.IsNullOrEmpty(lastResponse))
            {
                var partialResponse = TryCreatePartialResponse<T>(lastResponse);
                if (partialResponse != null)
                {
                    _logger.Warning("[{RequestId}] Using partial response due to parsing errors", requestId);
                    LogDeserializedResult(requestId, partialResponse);
                    stopwatch.Stop();
                    _logger.Information("[{RequestId}] Completed with partial response in {ElapsedMs}ms",
                        requestId, stopwatch.ElapsedMilliseconds);
                    return partialResponse;
                }
            }

            _logger.Error("[{RequestId}] Could not parse JSON response, returning default. Last error: {Error}",
                requestId, lastException?.Message);

            var defaultResponse = CreateDefaultResponse<T>();
            stopwatch.Stop();
            _logger.Information("[{RequestId}] Returning default response after {ElapsedMs}ms",
                requestId, stopwatch.ElapsedMilliseconds);

            return defaultResponse;
        }

        private void LogDeserializedResult<T>(string requestId, T result)
        {
            try
            {
                var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger.Debug("[{RequestId}] Deserialized result: {Result}", requestId, resultJson);
            }
            catch (Exception ex)
            {
                _logger.Warning("[{RequestId}] Could not serialize result for logging: {Error}",
                    requestId, ex.Message);
            }
        }

        private string ExtractAndCleanJson(string response)
        {
            _logger.Verbose("ExtractAndCleanJson called with response length: {Length}", response?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(response))
                return null;

            // Remove any markdown code blocks
            var originalLength = response.Length;
            response = System.Text.RegularExpressions.Regex.Replace(
                response, @"```json?\s*\n?|\s*```", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (originalLength != response.Length)
            {
                _logger.Debug("Removed {CharCount} chars of markdown formatting", originalLength - response.Length);
            }

            // Find JSON boundaries
            int startIndex = response.IndexOf('{');
            int endIndex = response.LastIndexOf('}');

            _logger.Verbose("JSON boundaries - Start: {Start}, End: {End}", startIndex, endIndex);

            if (startIndex >= 0 && endIndex > startIndex)
            {
                var jsonCandidate = response.Substring(startIndex, endIndex - startIndex + 1);
                _logger.Verbose("Extracted JSON candidate of length {Length}", jsonCandidate.Length);

                // Escape unescaped newlines and other special characters
                var beforeEscape = jsonCandidate.Length;
                jsonCandidate = EscapeJsonString(jsonCandidate);

                if (beforeEscape != jsonCandidate.Length)
                {
                    _logger.Debug("Escaped special characters, size changed by {Diff} chars",
                        jsonCandidate.Length - beforeEscape);
                }

                return _jsonParser.ExtractJson(jsonCandidate);
            }

            _logger.Warning("Could not find valid JSON boundaries in response");
            return null;
        }

        private string EscapeJsonString(string json)
        {
            var result = new StringBuilder();
            bool inString = false;
            bool escaped = false;
            int changedChars = 0;

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
                            changedChars++;
                            break;
                        case '\r':
                            result.Append("\\r");
                            changedChars++;
                            break;
                        case '\t':
                            result.Append("\\t");
                            changedChars++;
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

            if (changedChars > 0)
            {
                _logger.Verbose("Escaped {Count} special characters in JSON string", changedChars);
            }

            return result.ToString();
        }

        private T TryCreatePartialResponse<T>(string partialResponse)
        {
            _logger.Debug("Attempting to create partial response for type {TypeName}", typeof(T).Name);

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

                _logger.Debug("Partial response extraction - Code found: {CodeFound}, FilePath found: {FilePathFound}",
                    codeMatch.Success, filePathMatch.Success);

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

                    _logger.Information("Successfully created partial {TypeName} response", type.Name);
                    return (T)(object)response;
                }
            }

            _logger.Warning("Could not create partial response for type {TypeName}", type.Name);
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
4. Have no trailing commas
5. Be complete and valid

DO NOT include ```json or ``` or any other markdown.
DO NOT truncate or abbreviate the response.
ENSURE all string values are properly escaped.";
        }

        private T CreateDefaultResponse<T>()
        {
            var type = typeof(T);
            _logger.Information("Creating default response for type {TypeName}", type.Name);

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

            _logger.Error("No default response available for type {TypeName}", type.Name);
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