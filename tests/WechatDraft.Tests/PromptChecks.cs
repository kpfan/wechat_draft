using System.Net;
using System.Text.Json;
using WechatDraft.Core;

internal static class PromptChecks
{
    public static async Task RunAsync()
    {
        foreach (var useFunction in new[] { false, true })
        foreach (var address in new string?[] { null, "", "陈老师" })
        {
            Contact? contact = address is null ? null : new() { Confirmed = true, AddressTerm = address };
            var greeting = string.IsNullOrEmpty(address) ? "端午安康，愿你平安顺心！" : address + "，端午安康，愿您平安顺心！";
            var calls = 0;
            using var client = new CompatibleModelClient(new StubHandler(request =>
            {
                calls++;
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var messages = body.RootElement.GetProperty("messages");
                Require(messages.GetArrayLength() == 2, "生成应沿用一组系统提示和用户输入。");
                Require(messages[0].GetProperty("role").GetString() == "system", "提示词没有进入系统消息。");
                var content = messages[1].GetProperty("content");
                var images = content.EnumerateArray().Where(x => x.GetProperty("type").GetString() == "image_url").ToArray();
                Require(images.Length == 4, "原有多截图输入丢失。");
                for (var i = 0; i < images.Length; i++)
                    Require(images[i].GetProperty("image_url").GetProperty("url").GetString() ==
                        "data:image/png;base64," + Convert.ToBase64String([(byte)i]), "截图内容或顺序发生变化。");
                using var input = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
                Require(input.RootElement.GetProperty("scene").GetString() == "端午", "场景丢失。");
                Require(input.RootElement.GetProperty("extra_requirement").GetString() == "不要使用表情", "当次要求被提示词覆盖。");
                Require(input.RootElement.GetProperty("hard_max").GetInt32() == 120, "原有字数约束发生变化。");
                var salutation = input.RootElement.GetProperty("salutation");
                Require(salutation.GetProperty("required_address_term").GetString() ==
                    (string.IsNullOrEmpty(address) ? null : address), "档案称呼优先级发生变化。");
                Require(salutation.GetProperty("allow_inferred").GetBoolean() == (contact is null), "已确认空称呼不能被补猜。");
                var data = JsonSerializer.Serialize(new ModelResponse { AddressTerm = string.IsNullOrEmpty(address) ? null : address,
                    GreetingText = greeting }, JsonDefaults.Options);
                object message = useFunction
                    ? new { tool_calls = new[] { new { function = new { name = "submit_draft", arguments = data } } } }
                    : new { content = data };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message } } })) };
            }));
            var settings = new AppSettings
            {
                BaseUrl = useFunction ? "https://open.bigmodel.cn/api/paas/v4" : "https://test.example/v1",
                Model = useFunction ? "glm-4.6v-flash" : "vision", Scene = "端午", ExtraInstruction = "不要使用表情"
            };
            var captures = Enumerable.Range(0, 4).Select(i => new Capture("capture_" + i, [(byte)i])).ToArray();
            var result = await client.GenerateAsync(settings, "", captures, contact, [], true, default);
            Require(result.GreetingText == greeting, "现有响应协议无法返回正文。");
            Require(calls == 1, "提示词接入不应增加一次生成的请求次数。");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
