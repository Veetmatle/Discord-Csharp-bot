using System.Net;
using System.Text;
using Discord_Bot_AI.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Json;

namespace Discord_Bot_AI.Services;

public class RiotService
{
    private readonly HttpClient _httpClient;
    private readonly string _riotApiKey;
    private const string BaseUrl = "https://europe.api.riotgames.com/riot/account/v1/accounts";

    public RiotService(string apiKey)
    {
        _riotApiKey = apiKey;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-Riot-Token", _riotApiKey);
    }
    
    /// <summary>
    /// Method is used to provide Riot Account information based on in-game nickname and tag.
    /// </summary>
    public async Task<RiotAccount?> GetAccountAsync(string gameNickName, string tag)
    {
        var url = $"{BaseUrl}/by-riot-id/{gameNickName}/{tag}";
        var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
            return null;
        
        return await response.Content.ReadFromJsonAsync<RiotAccount>();
    }

    public async Task<string?> GetLatestMatchIdAsync(string puuid)
    {
        var url = $"{BaseUrl}/by-puuid/{puuid}/ids?start=0&count=1";
    
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var matchIds = await response.Content.ReadFromJsonAsync<List<string>>();
        return matchIds?.FirstOrDefault();
    }
    
    public async Task<MatchData?> GetMatchDetailsAsync(string matchId)
    {
        var url = $"{BaseUrl}/{matchId}";
    
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<MatchData>();
    }
}

