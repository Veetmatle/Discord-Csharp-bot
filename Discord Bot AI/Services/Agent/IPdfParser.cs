namespace Discord_Bot_AI.Services.Agent;

public interface IPdfParser
{
    Task<string> ExtractTextAsync(Stream pdfStream, CancellationToken cancellationToken = default);
}
