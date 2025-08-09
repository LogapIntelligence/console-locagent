using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OllamaStructuredOutput
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private const string OLLAMA_API_URL = "http://localhost:11434/api/chat";
        private const string MODEL_NAME = "phi4-mini";

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Ollama Structured Output Console App ===");
            Console.WriteLine($"Using model: {MODEL_NAME}");
            Console.WriteLine("Type 'exit' to quit\n");

            while (true)
            {
                Console.Write("Enter your prompt: ");
                string prompt = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(prompt) || prompt.ToLower() == "exit")
                {
                    Console.WriteLine("Goodbye!");
                    break;
                }

                try
                {
                    string response = await GetStructuredResponse(prompt);
                    Console.WriteLine("\nResponse (JSON):");
                    Console.WriteLine(response);
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nError: {ex.Message}\n");
                }
            }
        }

        static async Task<string> GetStructuredResponse(string prompt)
        {
            // Define the request payload with structured output format
            var requestPayload = new
            {
                model = MODEL_NAME,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                },
                stream = false,
                format = new
                {
                    type = "object",
                    properties = new
                    {
                        response = new
                        {
                            type = "string",
                            description = "The model's response to the prompt"
                        }
                    },
                    required = new[] { "response" }
                }
            };

            // Serialize the request to JSON
            string jsonRequest = JsonSerializer.Serialize(requestPayload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            // Create the HTTP request
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            // Send the request
            HttpResponseMessage httpResponse = await httpClient.PostAsync(OLLAMA_API_URL, content);

            if (!httpResponse.IsSuccessStatusCode)
            {
                string errorContent = await httpResponse.Content.ReadAsStringAsync();
                throw new Exception($"API request failed with status {httpResponse.StatusCode}: {errorContent}");
            }

            // Read and parse the response
            string responseContent = await httpResponse.Content.ReadAsStringAsync();
            using (JsonDocument doc = JsonDocument.Parse(responseContent))
            {
                if (doc.RootElement.TryGetProperty("message", out JsonElement messageElement) &&
                    messageElement.TryGetProperty("content", out JsonElement contentElement))
                {
                    string structuredOutput = contentElement.GetString();

                    // Pretty print the JSON
                    using (JsonDocument outputDoc = JsonDocument.Parse(structuredOutput))
                    {
                        return JsonSerializer.Serialize(outputDoc, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                    }
                }
                else
                {
                    throw new Exception("Unexpected response format from Ollama API");
                }
            }
        }
    }
}

// === Program.csproj file content (save this as a separate file) ===
/*
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
*/

// === Usage Instructions ===
/*
1. Prerequisites:
   - Install .NET SDK 8.0 or later
   - Install and run Ollama locally
   - Pull the phi4-mini model: ollama pull phi4-mini

2. Create a new console application:
   mkdir OllamaStructuredOutput
   cd OllamaStructuredOutput
   dotnet new console

3. Replace the Program.cs content with the code above

4. Run the application:
   dotnet run

5. Example prompts to try:
   - "What is the capital of France?"
   - "Explain quantum computing in simple terms"
   - "Write a haiku about programming"

The application will return responses in the JSON format:
{
  "response": "The model's answer here..."
}
*/