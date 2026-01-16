using System.Text;
using Discord_Bot_AI.Models.Gemini;
using Newtonsoft.Json;

namespace Discord_Bot_AI.Services;

public class GeminiService(string apiKey)
{
    private readonly HttpClient _httpClient = new();
    private const string ApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

    public async Task<string> GetAnswerAsync(string prompt)
    {
        try
        {
            var requestBody = new GeminiRequest
            {
                contents = new[]
                {
                    new Content { parts = new[] { new Part { text = prompt } } }
                }
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{ApiUrl}?key={apiKey}", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"Error: {response.StatusCode}";
            }
            
            var result = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(responseJson);
            string? answer = result?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();

            if (string.IsNullOrEmpty(answer))
                return "No answer found";
            
            return answer;
        }
        catch (Exception ex)
        {
            return $"Error connecting to Gemini: {ex.Message}";
        }
    }
}

