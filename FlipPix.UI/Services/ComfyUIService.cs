using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    public class ComfyUIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;

        public ComfyUIService(string serverUrl = "http://127.0.0.1:8188")
        {
            _httpClient = new HttpClient();
            _serverUrl = serverUrl;
        }

        public async Task<string> SubmitWorkflowAsync(Dictionary<string, object> workflow)
        {
            var prompt = new { prompt = workflow };
            var json = JsonSerializer.Serialize(prompt);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_serverUrl}/prompt", content);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<JsonElement>(responseText);
            return responseObj.GetProperty("prompt_id").GetString() ?? string.Empty;
        }

        public async Task<bool> IsRunningAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_serverUrl}/system_stats");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<JsonElement> GetQueueInfoAsync()
        {
            var response = await _httpClient.GetAsync($"{_serverUrl}/queue");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
    }
}