using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pult.Services;

public sealed record ChatMessage(string Role, string Content,
    List<ToolCall>? ToolCalls = null, string? ToolCallId = null, string? Name = null);
public sealed record ToolCall(string Id, string Name, Dictionary<string, JsonElement> Args, string ArgsRaw = "{}");

// OpenAI-совместимый чат (Ollama / OpenAI-совместимые серверы). Только BCL.
public sealed class LlmClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _model;
    private readonly string _apiKey;

    public LlmClient(AppSettings settings)
    {
        var llm = settings?.Llm ?? new LlmSettings();
        _url = (llm.BaseUrl ?? "").TrimEnd('/') + "/chat/completions";
        _model = string.IsNullOrWhiteSpace(llm.Model) ? "qwen2.5:7b" : llm.Model;
        _apiKey = llm.ApiKey ?? "";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public void Dispose() => _http.Dispose();

    public static string ToolsJson() => JsonSerializer.Serialize(BuildTools());

    public static int ToolCount()
    {
        try { return BuildTools().Count; }
        catch { return 0; }
    }

    private static List<Dictionary<string, object>> BuildTools()
    {
        static Dictionary<string, object> StrProp(string desc) =>
            new() { ["type"] = "string", ["description"] = desc };
        static Dictionary<string, object> BoolProp(string desc) =>
            new() { ["type"] = "boolean", ["description"] = desc };
        static Dictionary<string, object> IntProp(string desc) =>
            new() { ["type"] = "integer", ["description"] = desc };
        static Dictionary<string, object> Fn(string name, string desc, Dictionary<string, object>? props) =>
            new()
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = (object)(props ?? new Dictionary<string, object>()),
                        ["additionalProperties"] = false,
                    },
                },
            };

        var tools = new List<Dictionary<string, object>>
        {
            Fn("disk_space", "Свободное место на всех дисках", null),
            Fn("organize_downloads", "Разложить файлы в Загрузках по папкам-категориям", null),
            Fn("find_duplicates", "Найти дубликаты файлов (SHA-256)", new()
            {
                ["directory"] = StrProp("Папка для поиска (по умолчанию Загрузки)"),
                ["delete"] = BoolProp("true — удалить дубликаты"),
            }),
            Fn("clean_temp", "Предпросмотр или очистка временных файлов", new()
            {
                ["dry_run"] = BoolProp("true — только показать, false — удалить"),
                ["min_age_hours"] = IntProp("Удалять файлы старше N часов"),
            }),
            Fn("large_files", "Крупные файлы в папке", new()
            {
                ["directory"] = StrProp("Папка для поиска"),
                ["min_mb"] = IntProp("Минимальный размер в МБ"),
            }),
            Fn("get_clipboard", "Прочитать текст из буфера обмена", null),
            Fn("startup_list", "Что запускается вместе с Windows (автозагрузка)", null),
            Fn("installed_programs", "Самые крупные установленные программы", new()
            {
                ["limit"] = IntProp("Сколько показать (по умолчанию 10)"),
            }),
            Fn("battery_status", "Состояние батареи ноутбука", null),
            Fn("empty_recycle_bin", "Очистить корзину", null),
            Fn("list_games", "Список установленных игр", null),
            Fn("launch_game", "Запустить игру по названию", new()
            {
                ["name"] = StrProp("Название игры (можно частично)"),
            }),
            Fn("system_info", "CPU, память и диски одной строкой-сводкой", null),
            Fn("list_processes", "Топ процессов по памяти", new()
            {
                ["limit"] = IntProp("Сколько процессов показать"),
            }),
            Fn("kill_process", "Завершить процесс по имени или PID", new()
            {
                ["name_or_pid"] = StrProp("Имя процесса или PID"),
            }),
            Fn("web_search", "Открыть поиск в браузере по запросу", new()
            {
                ["query"] = StrProp("Поисковый запрос"),
            }),
            Fn("web_search_read", "Найти в интернете и вернуть заголовки/ссылки/описания для пересказа", new()
            {
                ["query"] = StrProp("Запрос"),
                ["max_results"] = IntProp("Сколько результатов (по умолчанию 5)"),
            }),
            Fn("open_url", "Открыть URL в браузере", new()
            {
                ["url"] = StrProp("Адрес"),
            }),
        };
        // Строгий режим: модель даже не узнает про буфер обмена.
        try
        {
            if (AppSettings.Load().StrictPrivacyMode)
                tools.RemoveAll(t => t.TryGetValue("function", out var f)
                    && f is Dictionary<string, object> fd
                    && fd.TryGetValue("name", out var n)
                    && (n as string) == "get_clipboard");
        }
        catch { }
        return tools;
    }

    private static object SerializeMessage(ChatMessage m)
    {
        var d = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            d["tool_calls"] = m.ToolCalls.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["arguments"] = c.ArgsRaw,
                },
            }).ToArray();
        if (!string.IsNullOrEmpty(m.ToolCallId))
        {
            d["tool_call_id"] = m.ToolCallId;
            if (!string.IsNullOrEmpty(m.Name)) d["name"] = m.Name;
        }
        return d;
    }

    // Лёгкая проверка связи: обычный чат БЕЗ инструментов.
    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            var (content, _) = await ChatAsync(
                new List<ChatMessage> { new("user", "Ответь одним словом: ОК") }, "", ct);
            if (string.IsNullOrWhiteSpace(content))
                return (false, "Пустой ответ.");
            string one = content.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (one.Length > 200) one = one[..200] + "…";
            return (true, one);
        }
        catch (OperationCanceledException)
        {
            return (false, "Таймаут: сервер не ответил.");
        }
        catch (Exception ex) when (ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is InvalidOperationException
            || ex is JsonException)
        {
            return (false, "Не могу подключиться. Проверь Base URL, ключ и модель."
                + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $" ({ex.Message})"));
        }
    }

    public async Task<(string? Content, List<ToolCall> Calls)> ChatAsync(
        List<ChatMessage> messages, string toolsJson, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>();
        payload["model"] = _model;
        payload["messages"] = messages.Select(m => SerializeMessage(m)).ToArray();
        if (!string.IsNullOrWhiteSpace(toolsJson))
        {
            try { payload["tools"] = JsonSerializer.Deserialize<JsonElement>(toolsJson); }
            catch { /* без tools — обычный чат */ }
        }
        payload["tool_choice"] = "auto";
        payload["temperature"] = 0.1;

        string json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);

        string? content = null;
        var calls = new List<ToolCall>();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                content = c.GetString();
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tc.EnumerateArray())
                {
                    string id = t.TryGetProperty("id", out var ide) && ide.ValueKind == JsonValueKind.String
                        ? ide.GetString() ?? "" : "";
                    if (!t.TryGetProperty("function", out var fn)) continue;
                    string name = fn.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String
                        ? ne.GetString() ?? "" : "";
                    string argsRaw = "{}";
                    if (fn.TryGetProperty("arguments", out var ae))
                        argsRaw = ae.ValueKind == JsonValueKind.String
                            ? ae.GetString() ?? "{}"
                            : ae.GetRawText();
                    var dict = new Dictionary<string, JsonElement>();
                    try
                    {
                        using var ad = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsRaw) ? "{}" : argsRaw);
                        if (ad.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var p in ad.RootElement.EnumerateObject())
                                dict[p.Name] = p.Value.Clone();
                    }
                    catch { /* кривые аргументы — пустой словарь */ }
                    if (!string.IsNullOrWhiteSpace(name))
                        calls.Add(new ToolCall(id, name, dict, argsRaw));
                }
            }
        }
        return (content, calls);
    }

    public async Task<string> RunConversationAsync(
        string userText,
        Action<string, string> onEvent,
        Func<string, Dictionary<string, string>, bool> confirm,
        CancellationToken ct,
        List<ChatMessage>? history = null)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string system =
            "Ты ассистент Windows-Пульта. Отвечай кратко по-русски. " +
            "Действуй ТОЛЬКО через tools, не пиши вызовы текстом. " +
            "Интернет: web_search_read(query) — найти и пересказать, web_search — открыть браузер, open_url — открыть ссылку. " +
            "Не повторяй упавший вызов. Удаление файлов и завершение процессов — только по явному запросу пользователя.\n" +
            "Домашняя папка: " + home;

        var messages = new List<ChatMessage> { new("system", system) };
        // Контекст: последние реплики (только user/assistant, без tool).
        if (history != null)
        {
            foreach (var h in history.TakeLast(20))
            {
                if (h.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(h.Content))
                    messages.Add(new ChatMessage(h.Role, h.Content));
            }
        }
        messages.Add(new ChatMessage("user", userText ?? ""));
        string tools = ToolsJson();
        string? lastContent = null;
        // Сигнатура name+args: один и тот же упавший вызов не повторяем,
        // но другие вызовы того же инструмента разрешены.
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Sig(string name, Dictionary<string, string> a) =>
            name + ":" + string.Join(";", a.OrderBy(kv => kv.Key)
                .Select(kv => kv.Key + "=" + kv.Value));

        try
        {
            for (int round = 0; round < 6; round++)
            {
                ct.ThrowIfCancellationRequested();
                try { onEvent?.Invoke("stage", "Думаю…"); } catch { }

                string? content;
                List<ToolCall> calls;
                try
                {
                    (content, calls) = await ChatAsync(messages, tools, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return "Остановлено пользователем.";
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || ex is TaskCanceledException
                    || ex is InvalidOperationException
                    || ex is JsonException)
                {
                    return "Не могу подключиться к LLM. Проверь Настройки → LLM."
                        + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $" ({ex.Message})");
                }

                lastContent = content;
                messages.Add(new ChatMessage("assistant", content ?? "", calls));

                var pending = calls.Where(c => !failed.Contains(Sig(c.Name, ToStringArgs(c.Args)))).ToList();
                if (pending.Count == 0)
                {
                    // Промпт-инъекция через "псевдовызовы" текстом отключена:
                    // текст модели никогда не исполняется как инструмент.
                    return string.IsNullOrWhiteSpace(content) ? "Готово." : content!;
                }

                foreach (var call in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    var sargs = ToStringArgs(call.Args);
                    string sig = Sig(call.Name, sargs);
                    bool ok = AskConfirm(confirm, call.Name, sargs);
                    string res;
                    if (!ok)
                    {
                        res = "Отменено пользователем.";
                        failed.Add(sig);
                    }
                    else
                    {
                        res = SafeExecute(call.Name, sargs);
                        if (IsFailure(res)) failed.Add(sig);
                    }
                    try { onEvent?.Invoke("tool", call.Name + ": " + Short(res)); } catch { }
                    messages.Add(new ChatMessage("tool", $"[{call.Name}] {res}",
                        null, string.IsNullOrEmpty(call.Id) ? null : call.Id, call.Name));
                }
            }
            return string.IsNullOrWhiteSpace(lastContent) ? "Готово." : lastContent!;
        }
        catch (OperationCanceledException)
        {
            return "Остановлено пользователем.";
        }
    }

    // ---- Выполнение инструментов ----

    public static string ExecuteTool(string name, Dictionary<string, string> args)
    {
        args ??= new Dictionary<string, string>();
        static string Get(Dictionary<string, string> a, string key, string def = "")
        {
            if (a.TryGetValue(key, out var v) && v != null) return v.Trim();
            return def;
        }
        static bool GetBool(Dictionary<string, string> a, string key, bool def)
        {
            string v = Get(a, key, "");
            if (v.Length == 0) return def;
            v = v.ToLowerInvariant();
            if (v is "true" or "1" or "да" or "yes" or "y") return true;
            if (v is "false" or "0" or "нет" or "no" or "n") return false;
            return def;
        }
        static int GetInt(Dictionary<string, string> a, string key, int def)
        {
            string v = Get(a, key, "");
            return int.TryParse(v, out int n) ? n : def;
        }
        static long GetLong(Dictionary<string, string> a, string key, long def)
        {
            string v = Get(a, key, "");
            return long.TryParse(v, out long n) ? n : def;
        }
        static bool ExpOn()
        {
            try { return AppSettings.ExpOn(); }
            catch { return false; }
        }

        try
        {
            switch (name)
            {
                case "disk_space":
                    return SystemMonitor.DiskReport();

                case "organize_downloads":
                    return FileService.OrganizeDirectory(FileService.Downloads());

                case "find_duplicates":
                {
                    string dir = Get(args, "directory", "");
                    if (dir.Length == 0) dir = FileService.Downloads();
                    if (GetBool(args, "delete", false))
                    {
                        if (!ExpOn()) return "Удаление выключено: включи Настройки → Экспериментальное.";
                        return DiskCleanupService.DeleteDuplicates(dir);
                    }
                    return DiskCleanupService.FindDuplicates(dir);
                }

                case "clean_temp":
                {
                    bool dry = GetBool(args, "dry_run", true);
                    int age = GetInt(args, "min_age_hours", 24);
                    if (age < 0) age = 0;
                    if (dry) return DiskCleanupService.PreviewTemp(age);
                    if (!ExpOn()) return "Удаление выключено: включи Настройки → Экспериментальное.";
                    return DiskCleanupService.CleanTemp(age);
                }

                case "large_files":
                {
                    string dir = Get(args, "directory", "");
                    if (dir.Length == 0) dir = FileService.Downloads();
                    long minMb = GetLong(args, "min_mb", 100);
                    if (minMb < 1) minMb = 1;
                    return DiskCleanupService.LargeFiles(dir, minMb);
                }

                case "get_clipboard":
                {
                    // Вторая стена: вдруг модель вызовет инструмент без объявления.
                    try { if (AppSettings.Load().StrictPrivacyMode) return "Запрещено: включён строгий режим приватности."; }
                    catch { }
                    try
                    {
                        string text = "";
                        var app = System.Windows.Application.Current;
                        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                        {
                            text = app.Dispatcher.Invoke(() =>
                            {
                                try { return System.Windows.Clipboard.GetText(); }
                                catch (Exception ex) { return $"Ошибка чтения буфера: {ex.Message}"; }
                            });
                        }
                        else
                        {
                            try { text = System.Windows.Clipboard.GetText(); }
                            catch (Exception ex) { return $"Ошибка чтения буфера: {ex.Message}"; }
                        }
                        if (string.IsNullOrEmpty(text)) return "Буфер обмена пуст.";
                        if (text.Length > 2000) text = text[..2000] + "…";
                        return text;
                    }
                    catch (Exception ex) { return $"Ошибка чтения буфера: {ex.Message}"; }
                }

                case "startup_list":
                {
                    var list = SystemMonitor.StartupEntries();
                    if (list.Count == 0) return "Автозагрузка пуста.";
                    return "Автозагрузка:\n" + string.Join("\n",
                        list.Take(20).Select(e => $"• {e.Name} ({e.Location})"));
                }

                case "installed_programs":
                {
                    long lim = GetLong(args, "limit", 10);
                    if (lim < 1) lim = 1;
                    if (lim > 30) lim = 30;
                    var (total, top) = SystemMonitor.InstalledPrograms(30);
                    if (top.Count == 0) return "Не прочиталось.";
                    return $"Программ всего: {total}. Крупнейшие:\n" + string.Join("\n",
                        top.Take((int)lim).Select(p =>
                            $"• {p.Name} {p.Version} — {(p.SizeKb > 0 ? $"{p.SizeKb / 1024} МБ" : "—")}"));
                }

                case "battery_status":
                {
                    string b = SystemMonitor.BatteryDetail();
                    return string.IsNullOrWhiteSpace(b) || b.Trim() == "—"
                        ? "Батареи не нашёл (похоже, десктоп)."
                        : b;
                }

                case "empty_recycle_bin":
                {
                    return DiskCleanupService.EmptyRecycleBin();
                }

                case "list_games":
                {
                    var games = GameService.GetGames(false, null);
                    if (games.Count == 0) return "Игры не найдены.";
                    return "Установленные игры:\n" + string.Join("\n",
                        games.Take(30).Select((g, i) => $"{i + 1}. {g.Name}"));
                }

                case "launch_game":
                {
                    string q = Get(args, "name", "");
                    if (q.Length == 0) return "Ошибка: укажи название игры.";
                    var games = GameService.GetGames(false, null);
                    var found = games.FirstOrDefault(g =>
                        g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                    if (found == null) return $"Игра не найдена: {q}.";
                    return GameService.Launch(found);
                }

                case "system_info":
                {
                    var mon = new SystemMonitor();
                    double cpu;
                    try { cpu = mon.CpuPercent(); } catch { cpu = 0; }
                    double pct = 0;
                    long used = 0, total = 0;
                    try { (pct, used, total) = mon.Memory(); } catch { }
                    string ram = total > 0
                        ? $"{pct:F0}% ({DiskInfo.Gb(used)} из {DiskInfo.Gb(total)})"
                        : $"{pct:F0}%";
                    return $"CPU: {cpu:F0}%\nRAM: {ram}\n{SystemMonitor.DiskReport()}";
                }

                case "list_processes":
                {
                    int limit = GetInt(args, "limit", 10);
                    limit = Math.Clamp(limit, 1, 50);
                    var top = Process.GetProcesses()
                        .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
                        .Take(limit)
                        .Select(p =>
                        {
                            string mem;
                            try { mem = $"{p.WorkingSet64 / 1024.0 / 1024:F0} МБ"; }
                            catch { mem = "—"; }
                            return $"{p.ProcessName}  PID {p.Id}  {mem}";
                        });
                    return string.Join("\n", top);
                }

                case "kill_process":
                {
                    if (!ExpOn()) return "Завершение процессов выключено: включи Настройки → Экспериментальное.";
                    string target = Get(args, "name_or_pid", "");
                    if (target.Length == 0) return "Ошибка: укажи имя или PID процесса.";
                    string want0 = target.Trim();
                    if (want0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        want0 = want0[..^4];
                    if (SystemMonitor.IsProtectedProc(want0)) return "Системный процесс завершать нельзя.";
                    try
                    {
                        if (int.TryParse(target, out int pid))
                        {
                            using var p = Process.GetProcessById(pid);
                            if (SystemMonitor.IsProtectedProc(p.ProcessName))
                                return "Системный процесс завершать нельзя: " + p.ProcessName + ".";
                            p.Kill();
                            return $"Процесс {p.ProcessName} (PID {pid}) завершён.";
                        }
                        var found = Process.GetProcesses().FirstOrDefault(p =>
                        {
                            try { return string.Equals(p.ProcessName, want0, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        });
                        if (found == null) return $"Процесс не найден: {target}.";
                        if (SystemMonitor.IsProtectedProc(found.ProcessName))
                        {
                            found.Dispose();
                            return "Системный процесс завершать нельзя.";
                        }
                        string nm2 = found.ProcessName;
                        using (found)
                        {
                            found.Kill();
                        }
                        return $"Процесс {nm2} завершён.";
                    }
                    catch (Exception ex) { return $"Ошибка завершения процесса: {ex.Message}"; }

                }
                case "web_search":
                {
                    string q = Get(args, "query", "");
                    if (q.Length == 0) q = Get(args, "q", "");
                    if (q.Length == 0) q = Get(args, "text", "");
                    return WebService.OpenSearch(q);
                }

                case "web_search_read":
                {
                    string q = Get(args, "query", "");
                    if (q.Length == 0) q = Get(args, "q", "");
                    if (q.Length == 0) q = Get(args, "text", "");
                    int n = GetInt(args, "max_results", 5);
                    return WebService.SearchRead(q, n);
                }

                case "open_url":
                {
                    string u = Get(args, "url", "");
                    if (u.Length == 0) u = Get(args, "link", "");
                    return WebService.OpenUrl(u);
                }

                default:
                    return $"Неизвестный инструмент: {name}.";
            }
        }
        catch (Exception ex)
        {
            return $"Ошибка выполнения {name}: {ex.Message}";
        }
    }

    // ---- Вспомогательное ----

    private static Dictionary<string, string> ToStringArgs(Dictionary<string, JsonElement> args)
    {
        var res = new Dictionary<string, string>();
        if (args == null) return res;
        foreach (var kv in args)
        {
            try
            {
                res[kv.Key] = kv.Value.ValueKind switch
                {
                    JsonValueKind.String => kv.Value.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => kv.Value.GetRawText(),
                };
            }
            catch { res[kv.Key] = ""; }
        }
        return res;
    }

    private static bool AskConfirm(
        Func<string, Dictionary<string, string>, bool> confirm,
        string name, Dictionary<string, string> sargs)
    {
        // Сбой подтверждения = запрет, а не разрешение.
        try { return confirm?.Invoke(name, sargs) ?? false; }
        catch { return false; }
    }

    // Только мутирующие инструменты требуют вопроса. Остальное — чтение.
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "disk_space", "list_games", "system_info", "list_processes",
        "recall_facts", "web_search_read", "get_clipboard",
        "startup_list", "installed_programs", "battery_status",
    };

    public static bool NeedsConfirm(string name) => !ReadOnlyTools.Contains(name);

    private static string SafeExecute(string name, Dictionary<string, string> sargs)
    {
        try { return ExecuteTool(name, sargs); }
        catch (Exception ex) { return $"Ошибка выполнения {name}: {ex.Message}"; }
    }

    private static bool IsFailure(string res)
    {
        if (string.IsNullOrEmpty(res)) return true;
        return res.StartsWith("Ошибка", StringComparison.Ordinal)
            || res.StartsWith("Нужны права", StringComparison.Ordinal)
            || res.StartsWith("Не удалось", StringComparison.Ordinal)
            || res.StartsWith("Не вышло", StringComparison.Ordinal)
            || res.Contains("ошибка 5", StringComparison.OrdinalIgnoreCase)
            || res.Contains("[SC]", StringComparison.Ordinal);
    }

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string one = s.Replace('\r', ' ').Replace('\n', ' ');
        while (one.Contains("  ")) one = one.Replace("  ", " ");
        one = one.Trim();
        return one.Length > 220 ? one[..220] + "…" : one;
    }
}
