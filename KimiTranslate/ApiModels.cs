using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KimiTranslate
{
    // 在 ApiModels.cs 或你的模型文件中
    public class ApiRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("messages")]
        public Message[] Messages { get; set; }

        // --- 新增这个属性 ---
        [JsonProperty("stream")]
        public bool Stream { get; set; }
    }

    public class ApiResponse
    {
        [JsonProperty("choices")]
        public Choice[] Choices { get; set; }
    }
    public class Choice
    {
        [JsonProperty("message")]
        public Message Message { get; set; }
    }
    public class Message
    {
        [JsonProperty("role")]
        public string Role { get; set; }
        [JsonProperty("content")]
        public string Content { get; set; }
    }

    // --- 用于解析流式API响应的新模型 ---

    public class ApiStreamResponse
    {
        [JsonProperty("choices")]
        public StreamChoice[] Choices { get; set; }
    }

    public class StreamChoice
    {
        [JsonProperty("delta")]
        public MessageDelta Delta { get; set; }

        [JsonProperty("finish_reason")]
        public string FinishReason { get; set; }
    }

    public class MessageDelta
    {
        [JsonProperty("content")]
        public string Content { get; set; }
    }

}
