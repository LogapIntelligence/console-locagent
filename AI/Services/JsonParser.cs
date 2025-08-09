using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI.Services
{
    public class JsonParser
    {
        private readonly JsonSerializerOptions _jsonOptions;

        public JsonParser()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public string ExtractJson(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return null;

            return response;
        }

        private string FixMalformedEscapeSequences(string json)
        {
            // Fix pattern like: "value\\",\" which should be "value","
            // This happens when backslashes are incorrectly added before closing quotes
            json = Regex.Replace(json, @"\\+"",\\\""", "\",\"");

            // Fix pattern at the end of values: "value\\"
            json = Regex.Replace(json, @"\\+""(\s*[,\}\]])""", "\"$1");

            // Fix double-escaped quotes that shouldn't be escaped
            json = Regex.Replace(json, @"\\\\""", "\\\"");

            // Fix orphaned backslashes before commas and quotes
            json = Regex.Replace(json, @"\\+(\s*,\s*\\?"")", "$1");

            // Special case: "Coding\\" should be "Coding"
            json = Regex.Replace(json, @"""([^""]+)\\+""", "\"$1\"");

            // Now handle normal escape sequences more carefully
            // Only unescape if it's clearly a double-escaped sequence
            if (json.Contains("\\\\\""))
            {
                json = json.Replace("\\\\\"", "\\\"");
            }

            // Remove escape before quotes that shouldn't be escaped
            // but preserve properly escaped quotes within strings
            var fixedJson = new StringBuilder();
            var inString = false;
            var prevChar = '\0';
            var prevPrevChar = '\0';

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];

                // Skip if we're looking at an escape sequence we've already processed
                if (prevChar == '\\' && c == '"')
                {
                    // Check if this is a wrongly placed escape
                    if (i + 1 < json.Length && (json[i + 1] == ',' || json[i + 1] == '}' || json[i + 1] == ']'))
                    {
                        // This is likely a wrongly escaped closing quote
                        // Remove the backslash
                        fixedJson.Length--; // Remove the backslash we just added
                        fixedJson.Append(c);
                    }
                    else if (!inString && prevPrevChar != '\\')
                    {
                        // Not in a string and not a double escape, likely wrong
                        fixedJson.Length--; // Remove the backslash
                        fixedJson.Append(c);
                    }
                    else
                    {
                        fixedJson.Append(c);
                    }

                    if (prevPrevChar != '\\')
                    {
                        inString = !inString;
                    }
                }
                else if (c == '"' && prevChar != '\\')
                {
                    inString = !inString;
                    fixedJson.Append(c);
                }
                else
                {
                    fixedJson.Append(c);
                }

                prevPrevChar = prevChar;
                prevChar = c;
            }

            return fixedJson.ToString();
        }

        private string FixUnescapedCharactersInJson(string json)
        {
            // First, try to fix the most common issue: unescaped quotes in string values
            json = FixUnescapedQuotesInStringValues(json);

            // Then fix unescaped newlines, tabs, etc.
            json = FixUnescapedSpecialCharacters(json);

            return json;
        }

        private string FixUnescapedQuotesInStringValues(string json)
        {
            // This pattern matches JSON string values and fixes unescaped quotes within them
            // It looks for patterns like: "key": "value with "unescaped" quotes"
            var pattern = @"(""[^""]+"")\s*:\s*""([^""]*(?:""[^,}\]]*)*[^""]*)""";

            var result = json;
            var maxIterations = 10; // Prevent infinite loops
            var iteration = 0;

            while (iteration < maxIterations)
            {
                var newResult = Regex.Replace(result, pattern, m =>
                {
                    var key = m.Groups[1].Value;
                    var value = m.Groups[2].Value;

                    // If the value contains quotes, escape them
                    if (value.Contains("\""))
                    {
                        // Don't escape quotes that are already escaped
                        value = Regex.Replace(value, @"(?<!\\)""", "\\\"");
                    }

                    return $"{key}: \"{value}\"";
                });

                if (newResult == result)
                    break;

                result = newResult;
                iteration++;
            }

            // Additional fix for specific patterns like: "prompt":"text with "quoted" words"
            result = Regex.Replace(result, @"(""[^""]+"":\s*"")([^""]*)(""[^""]+"")+([^""]*"")", m =>
            {
                var prefix = m.Groups[1].Value;
                var beforeQuote = m.Groups[2].Value;
                var quotedPart = m.Groups[3].Value;
                var afterQuote = m.Groups[4].Value;

                // Escape the inner quotes
                quotedPart = quotedPart.Replace("\"", "\\\"");

                return prefix + beforeQuote + quotedPart + afterQuote;
            });

            return result;
        }

        private string FixUnescapedSpecialCharacters(string json)
        {
            var result = new StringBuilder();
            var inString = false;
            var escapeNext = false;
            var quoteCount = 0;

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];

                if (escapeNext)
                {
                    result.Append(c);
                    escapeNext = false;
                    continue;
                }

                if (c == '\\')
                {
                    result.Append(c);
                    escapeNext = true;
                    continue;
                }

                if (c == '"')
                {
                    quoteCount++;

                    // Check if this quote is likely the start/end of a string value
                    if (i > 0 && i < json.Length - 1)
                    {
                        var prevChar = json[i - 1];
                        var nextChar = json[i + 1];

                        // If previous char is : or , or { or [ then it's likely start of string
                        // If next char is : or , or } or ] then it's likely end of string
                        var isLikelyBoundary = (prevChar == ':' || prevChar == ',' || prevChar == '{' || prevChar == '[' || prevChar == ' ') ||
                                               (nextChar == ':' || nextChar == ',' || nextChar == '}' || nextChar == ']' || nextChar == ' ');

                        if (isLikelyBoundary)
                        {
                            inString = !inString;
                        }
                        else if (inString)
                        {
                            // This is likely an unescaped quote within a string
                            result.Append('\\');
                        }
                    }
                    else
                    {
                        inString = !inString;
                    }

                    result.Append(c);
                    continue;
                }

                if (inString)
                {
                    if (c == '\n')
                    {
                        result.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        result.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        result.Append("\\t");
                    }
                    else
                    {
                        result.Append(c);
                    }
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        private string RemoveNonJsonContent(string input)
        {
            // Find the first { or [ and last } or ]
            int start = -1;
            int end = -1;

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '{' || input[i] == '[')
                {
                    start = i;
                    break;
                }
            }

            for (int i = input.Length - 1; i >= 0; i--)
            {
                if (input[i] == '}' || input[i] == ']')
                {
                    end = i;
                    break;
                }
            }

            if (start >= 0 && end > start)
            {
                return input.Substring(start, end - start + 1);
            }

            return input;
        }

        private string CleanJsonString(string json)
        {
            json = json.Trim();

            // Remove trailing commas before } or ]
            json = Regex.Replace(json, @",\s*}", "}", RegexOptions.Multiline);
            json = Regex.Replace(json, @",\s*]", "]", RegexOptions.Multiline);

            // Fix common JSON issues
            json = Regex.Replace(json, @":\s*'([^']*)'", ": \"$1\"", RegexOptions.Multiline); // Single quotes to double
            json = Regex.Replace(json, @"'([^']+)':", "\"$1\":", RegexOptions.Multiline); // Single quote keys

            // Remove comments
            json = Regex.Replace(json, @"//.*$", "", RegexOptions.Multiline);
            json = Regex.Replace(json, @"/\*.*?\*/", "", RegexOptions.Singleline);

            return json;
        }

        private bool IsValidJsonStructure(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            json = json.Trim();
            return (json.StartsWith("{") && json.EndsWith("}")) ||
                   (json.StartsWith("[") && json.EndsWith("]"));
        }

        public T DeserializeJson<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                // Try to fix the JSON one more time before giving up
                var fixedJson = ExtractJson(json);
                if (fixedJson != null && fixedJson != json)
                {
                    try
                    {
                        return JsonSerializer.Deserialize<T>(fixedJson, _jsonOptions);
                    }
                    catch
                    {
                        // Fall through to original error
                    }
                }

                // Log the problematic JSON for debugging
                Console.WriteLine($"[DEBUG] Failed to deserialize JSON: {ex.Message}");
                Console.WriteLine($"[DEBUG] Problematic JSON (first 500 chars): {json?.Substring(0, Math.Min(500, json?.Length ?? 0))}");

                // Try to provide more helpful debugging info
                if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
                {
                    Console.WriteLine($"[DEBUG] Error at Line: {ex.LineNumber}, Position: {ex.BytePositionInLine}");

                    // Try to show the problematic section
                    if (json != null && ex.BytePositionInLine.Value < json.Length)
                    {
                        var startPos = Math.Max(0, (int)ex.BytePositionInLine.Value - 50);
                        var endPos = Math.Min(json.Length, (int)ex.BytePositionInLine.Value + 50);
                        var problemArea = json.Substring(startPos, endPos - startPos);
                        Console.WriteLine($"[DEBUG] Problem area: ...{problemArea}...");
                    }
                }

                throw;
            }
        }

        // Additional helper method to validate and attempt to repair JSON
        public string ValidateAndRepairJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            // First, try to parse it as-is
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    // If it parses successfully, return it
                    return json;
                }
            }
            catch (JsonException)
            {
                // If it fails, try to repair it
                // First fix malformed escapes
                json = FixMalformedEscapeSequences(json);

                // Try parsing again after first fix
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        return json;
                    }
                }
                catch (JsonException)
                {
                    // Continue with full repair
                }

                // Full repair attempt
                var repaired = ExtractJson(json);

                // Validate the repaired JSON
                if (repaired != null)
                {
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(repaired))
                        {
                            return repaired;
                        }
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"[DEBUG] Still invalid after repair attempt: {ex.Message}");
                        Console.WriteLine($"[DEBUG] Attempted repair result: {repaired?.Substring(0, Math.Min(500, repaired?.Length ?? 0))}");
                    }
                }
            }

            return null;
        }
    }
}