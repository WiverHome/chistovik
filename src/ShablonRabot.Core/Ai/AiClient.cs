using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShablonRabot.Core.Ai;

/// <summary>anthropic — API Claude; openai — OpenAI и совместимые сервисы (DeepSeek, OpenRouter, локальные модели, прокси).</summary>
public enum AiApi { Anthropic, OpenAi }

/// <summary>Сервис ИИ с адресом и моделью по умолчанию.</summary>
public sealed record AiProviderInfo(string Id, string Title, AiApi Api, string BaseUrl, string Model, string? KeysUrl, string Hint)
{
    public bool IsCustom => Id == "custom";
    public override string ToString() => Title;
}

public static class AiProviders
{
    public static readonly AiProviderInfo[] All =
    [
        new("anthropic", "Claude (Anthropic)", AiApi.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5",
            "https://console.anthropic.com/settings/keys", "Ключ начинается с «sk-ant-». Создаётся в консоли Anthropic."),
        new("openai", "ChatGPT (OpenAI)", AiApi.OpenAi, "https://api.openai.com/v1", "gpt-5-mini",
            "https://platform.openai.com/api-keys", "Ключ начинается с «sk-». Создаётся на platform.openai.com."),
        new("deepseek", "DeepSeek", AiApi.OpenAi, "https://api.deepseek.com", "deepseek-chat",
            "https://platform.deepseek.com/api_keys", "Ключ создаётся на platform.deepseek.com."),
        new("openrouter", "OpenRouter", AiApi.OpenAi, "https://openrouter.ai/api/v1", "openrouter/auto",
            "https://openrouter.ai/keys", "Один ключ — много моделей. Ключ начинается с «sk-or-»."),
        new("custom", "Другой сервис", AiApi.OpenAi, "", "",
            null, "Любой сервис с API, совместимым с OpenAI: прокси, локальная модель (Ollama, LM Studio) и другие."),
    ];

    public static AiProviderInfo Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

/// <summary>Подключение: какой API, адрес, модель и ключ.</summary>
public sealed record AiConnection(AiApi Api, string BaseUrl, string Model, string ApiKey);

/// <summary>Ошибка запроса к ИИ с понятным заголовком и текстом для пользователя.</summary>
public sealed class AiException(string title, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Title { get; } = title;
}

/// <summary>Запросы к API ИИ: потоковый ответ и список моделей. Без сторонних библиотек — HttpClient и JSON.</summary>
public static class AiClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    const string AnthropicVersion = "2023-06-01";
    const int MaxTokens = 8000;
    /// <summary>Кириллица в запросе — как есть, без экранирования: короче и читается в журналах сервиса.</summary>
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Отправляет запрос и передаёт ответ частями по мере генерации; возвращает весь текст.</summary>
    public static async Task<string> StreamAsync(AiConnection c, string system, string user, Action<string> onText, CancellationToken ct)
    {
        using var request = c.Api == AiApi.Anthropic ? AnthropicRequest(c, system, user) : OpenAiRequest(c, system, user);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw NetworkError(e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await HttpError(response, c, ct);

            var text = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (!line.StartsWith("data:")) continue;
                    var data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(data); }
                    catch (JsonException) { continue; }
                    if (node?["error"] is { } error)
                        throw new AiException("Сервис вернул ошибку", ErrorText(error) ?? "Ответ прерван. Попробуйте ещё раз.");
                    var piece = c.Api == AiApi.Anthropic
                        ? (node?["type"]?.GetValue<string>() == "content_block_delta" ? node["delta"]?["text"]?.GetValue<string>() : null)
                        : node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(piece)) continue;
                    text.Append(piece);
                    onText(piece);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                throw NetworkError(e);
            }
            if (text.Length == 0)
                throw new AiException("Пустой ответ", "Сервис ничего не ответил. Попробуйте ещё раз или выберите другую модель.");
            return text.ToString();
        }
    }

    /// <summary>Список моделей — заодно проверяет ключ и адрес.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(AiConnection c, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, c.Api == AiApi.Anthropic ? Url(c.BaseUrl, "v1/models?limit=100") : Url(c.BaseUrl, "models"));
        Authorize(request, c);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw NetworkError(e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await HttpError(response, c, ct);
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            return (node?["data"] as JsonArray ?? [])
                .Select(m => m?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
        }
    }

    static HttpRequestMessage AnthropicRequest(AiConnection c, string system, string user)
    {
        var body = new JsonObject
        {
            ["model"] = c.Model,
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Url(c.BaseUrl, "v1/messages"))
        {
            Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json"),
        };
        Authorize(request, c);
        return request;
    }

    static HttpRequestMessage OpenAiRequest(AiConnection c, string system, string user)
    {
        // Без max_tokens и temperature: новые модели OpenAI их не принимают, а совместимые сервисы обходятся значениями по умолчанию
        var body = new JsonObject
        {
            ["model"] = c.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Url(c.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json"),
        };
        Authorize(request, c);
        return request;
    }

    static void Authorize(HttpRequestMessage request, AiConnection c)
    {
        if (c.Api == AiApi.Anthropic)
        {
            request.Headers.Add("x-api-key", c.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
        }
        else if (c.ApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
    }

    static Uri Url(string baseUrl, string path)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(b + "/" + path, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new AiException("Неверный адрес сервиса", $"Адрес «{baseUrl}» не похож на адрес API. Пример: https://api.openai.com/v1");
        return uri;
    }

    static AiException NetworkError(Exception e) => e is TaskCanceledException
        ? new AiException("Сервис не ответил вовремя", "Ответа нет слишком долго. Проверьте интернет и попробуйте ещё раз.", e)
        : new AiException("Нет связи с сервисом", "Не удалось подключиться. Проверьте интернет и адрес сервиса. Если сервис недоступен в вашей стране, укажите адрес прокси в варианте «Другой сервис».", e);

    static async Task<AiException> HttpError(HttpResponseMessage response, AiConnection c, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            detail = JsonNode.Parse(body)?["error"] is { } error ? ErrorText(error) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException) { }
        var tail = detail != null ? $" Ответ сервиса: «{Trim(detail)}»." : "";
        return (int)response.StatusCode switch
        {
            401 => new AiException("Ключ не подошёл", "Сервис не принял API-ключ. Проверьте, что ключ скопирован полностью и выбран тот же сервис, где он создан." + tail),
            402 => new AiException("Недостаточно средств", "На балансе сервиса закончились деньги. Пополните счёт в личном кабинете сервиса." + tail),
            403 => new AiException("Доступ запрещён", "Сервис отклонил запрос. Возможно, он недоступен в вашей стране или у ключа нет доступа к этой модели." + tail),
            404 => new AiException("Не найдено", $"Сервис не знает модель «{c.Model}» или адрес указан неверно. Выберите модель из списка." + tail),
            429 => new AiException("Слишком много запросов", "Превышен лимит запросов или закончилась квота. Подождите минуту или проверьте баланс." + tail),
            400 => new AiException("Сервис не принял запрос", "Запрос отклонён." + (tail.Length > 0 ? tail : " Попробуйте другую модель.")),
            >= 500 => new AiException("Сервис перегружен", "У сервиса временные неполадки. Попробуйте ещё раз через минуту." + tail),
            var code => new AiException("Ошибка сервиса", $"Код ответа {code} ({response.ReasonPhrase})." + tail),
        };
    }

    static string? ErrorText(JsonNode error) => error is JsonValue v && v.TryGetValue<string>(out var s) ? s : error["message"]?.ToString();

    static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
