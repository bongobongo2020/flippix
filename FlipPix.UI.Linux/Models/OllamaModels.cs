using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Linux.Models
{
    public class OllamaModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("modified_at")]
        public string ModifiedAt { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string Digest { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public OllamaModelDetails Details { get; set; } = new OllamaModelDetails();

        public override string ToString()
        {
            return Name;
        }
    }

    public class OllamaModelDetails
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        [JsonPropertyName("family")]
        public string Family { get; set; } = string.Empty;

        [JsonPropertyName("families")]
        public List<string> Families { get; set; } = new List<string>();

        [JsonPropertyName("parameter_size")]
        public string ParameterSize { get; set; } = string.Empty;

        [JsonPropertyName("quantization_level")]
        public string QuantizationLevel { get; set; } = string.Empty;
    }

    public class OllamaModelsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel> Models { get; set; } = new List<OllamaModel>();
    }

    public class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("system")]
        public string? System { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; set; }
    }

    public class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("total_duration")]
        public long TotalDuration { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }

    public class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }
}