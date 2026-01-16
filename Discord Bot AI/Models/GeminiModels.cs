namespace Discord_Bot_AI.Models.Gemini;

public class GeminiRequest
{
    public required Content[] contents { get; set; }
}

public class Content
{
    public required Part[] parts { get; set; }
}

public class Part
{
    public required string text { get; set; }
}

