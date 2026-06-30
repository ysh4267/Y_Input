using System.Net.WebSockets;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Core.Persistence;
using YInput.Engine;
using YInput.Host.Services;
using YInput.Input;

namespace YInput.Host.Web;

/// <summary>REST API + WebSocket 엔드포인트 매핑.</summary>
public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app, MacroService service, SocketHub hub)
    {
        // ---- 상태 ----
        app.MapGet("/api/status", () => Results.Json(service.GetStatusData()));
        app.MapGet("/api/log/recent", () => Results.Json(service.RecentLogs())); // 진단용 최근 로그

        // ---- 드라이버 설치(관리자 필요, 수 분 소요 가능) ----
        app.MapPost("/api/drivers/install", async () => await Guard(async () =>
        {
            var result = await Task.Run(DriverProvisioner.EnsureInstalled);
            foreach (var m in result.Messages) service.Log("info", m);
            service.BroadcastStatus();
            return Results.Json(new
            {
                interception = result.InterceptionInstalled,
                vigem = result.ViGEmInstalled,
                rebootRequired = result.RebootRequired,
                messages = result.Messages,
            });
        }));

        // ---- 매크로 목록/조회 ----
        app.MapGet("/api/macros", () =>
        {
            var all = service.ListMacros();
            var byId = new Dictionary<string, Macro>();
            foreach (var m in all) byId[m.Id] = m;
            var summaries = all.Select(m => Summary(m, byId, BuildShape(service.Expanded(m.Id) ?? m)));
            return Results.Json(summaries);
        });

        app.MapGet("/api/macros/{id}", (string id) =>
        {
            var macro = service.GetMacro(id);
            return macro is null
                ? Results.NotFound(new { error = "매크로를 찾을 수 없습니다." })
                : Json(macro);
        });

        // 참조(MacroRef) 인라인 전개본 — 재생되는 실제 스텝 시퀀스(진행 stepIndex 기준)
        app.MapGet("/api/macros/{id}/expanded", (string id) =>
        {
            var macro = service.Expanded(id);
            return macro is null
                ? Results.NotFound(new { error = "매크로를 찾을 수 없습니다." })
                : Json(macro);
        });

        // 참조를 펼치되 계층(트리)을 유지 — 우측 현황 패널이 '매크로 실행' 아래에 내용을 들여쓰기로 표시
        app.MapGet("/api/macros/{id}/tree", (string id) =>
        {
            var tree = service.StepTree(id);
            return tree is null
                ? Results.NotFound(new { error = "매크로를 찾을 수 없습니다." })
                : Results.Json(tree, MacroStore.Options);
        });

        // ---- 이 매크로를 참조(매크로 실행 블록)하는 다른 매크로들(삭제 경고용) ----
        app.MapGet("/api/macros/{id}/usage", (string id) =>
            Results.Json(new { usedBy = service.FindReferencing(id) }));

        // ---- 매크로 생성/수정 ----
        app.MapPost("/api/macros", (HttpRequest req) => Guard(async () =>
        {
            var macro = await ReadMacro(req);
            if (string.IsNullOrWhiteSpace(macro.Id)) { macro.Id = Guid.NewGuid().ToString("N"); macro.Order = service.NextOrder(); }
            service.SaveMacro(macro);
            return Json(macro);
        }));

        // ---- 매크로 목록 순서 변경(드래그) — body: 정렬된 id 배열 ----
        app.MapPost("/api/macros/reorder", (HttpRequest req) => Guard(async () =>
        {
            using var reader = new StreamReader(req.Body);
            var ids = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(), MacroStore.Options) ?? Array.Empty<string>();
            service.Reorder(ids);
            return Results.Ok(new { ok = true });
        }));

        app.MapPut("/api/macros/{id}", (string id, HttpRequest req) => Guard(async () =>
        {
            var macro = await ReadMacro(req);
            macro.Id = id; // 경로 id를 신뢰
            service.SaveMacro(macro);
            return Json(macro);
        }));

        app.MapDelete("/api/macros/{id}", (string id) => Guard(() =>
        {
            service.DeleteMacro(id);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 매크로 전체 초기화(모두 휴지통으로) ----
        app.MapPost("/api/macros/reset", () => Guard(() =>
        {
            var deleted = service.DeleteAllMacros();
            return Task.FromResult(Results.Ok(new { ok = true, deleted }));
        }));

        // ---- 매크로 묶음 가져오기 — 내부 매크로 참조(macroRef)를 새 Id로 자동 재연결 ----
        app.MapPost("/api/macros/import", (HttpRequest req) => Guard(async () =>
        {
            var macros = await ReadMacros(req);
            var added = service.ImportMacros(macros);
            return Results.Json(new { ok = true, added });
        }));

        // ---- 매크로 적용(활성) 토글 ----
        app.MapPost("/api/macros/{id}/enabled", (string id, EnabledBody? body) => Guard(() =>
        {
            service.SetEnabled(id, body?.Enabled ?? true);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 매크로 트리거 핫키 설정/해제(실행 페이지에서 직접) ----
        app.MapPost("/api/macros/{id}/trigger", (string id, HttpRequest req) => Guard(async () =>
        {
            var hk = await ReadHotkey(req);
            service.SetTrigger(id, hk);
            return Results.Ok(new { ok = true });
        }));

        // ---- 매크로 반복/속도 설정(실행 페이지 항목에서 직접) ----
        app.MapPost("/api/macros/{id}/playback", (string id, PlaybackBody? body) => Guard(() =>
        {
            service.SetPlayback(id, body?.LoopCount ?? 1);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 녹화 ----
        app.MapPost("/api/record/start", (HttpRequest req) => Guard(async () =>
        {
            var options = await ReadRecordOptions(req);
            service.StartRecording(options);
            return Results.Ok(new { ok = true });
        }));

        app.MapPost("/api/record/stop", (StopRecordingBody? body) => Guard(() =>
        {
            var macro = service.StopRecording(body?.Name, body?.Persist ?? true);
            return Task.FromResult(Json(macro));
        }));

        // ---- 재생 ----
        app.MapPost("/api/play/{id}", (string id) => Guard(() =>
        {
            service.Play(id);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        app.MapPost("/api/stop", () => Guard(() =>
        {
            service.StopPlayback();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 게임패드 ----
        app.MapPost("/api/gamepad/connect", () => Guard(() =>
        {
            service.ConnectGamepad();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        app.MapPost("/api/gamepad/disconnect", () => Guard(() =>
        {
            service.DisconnectGamepad();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 핫키 재등록 ----
        app.MapPost("/api/hotkeys/reload", () => Guard(() =>
        {
            service.ReloadHotkeys();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 아무 입력 잡기(게임패드 버튼) ----
        app.MapPost("/api/listen/start", () => Guard(() =>
        {
            service.StartListen();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));
        app.MapPost("/api/listen/stop", () => Guard(() =>
        {
            service.StopListen();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 입력 모니터(모든 입력 인식 스트림) ----
        app.MapPost("/api/monitor/on", () => Guard(() =>
        {
            service.SetMonitor(true);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));
        app.MapPost("/api/monitor/off", () => Guard(() =>
        {
            service.SetMonitor(false);
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        // ---- 앱 종료(빌드 후 재배포 자동화용) ----
        app.MapPost("/api/app/quit", () =>
        {
            service.RequestQuit();
            return Results.Ok(new { quitting = true });
        });

        // ---- GitHub Releases 업데이트(소스/SDK/git 불필요 — 어느 PC에서나 동작) ----
        app.MapGet("/api/app/version", () => Guard(async () =>
        {
            var v = await Task.Run(AppUpdater.Version);
            return Results.Json(new { current = v.Current, currentDate = v.CurrentDate, release = v.Release, releaseDate = v.ReleaseDate });
        }));
        app.MapGet("/api/app/update/check", () => Guard(async () =>
        {
            var r = await Task.Run(AppUpdater.Check);
            return Results.Json(new { ok = r.Ok, updateAvailable = r.UpdateAvailable, current = r.Current, latest = r.Latest, message = r.Message, downloadUrl = r.DownloadUrl, pageUrl = r.PageUrl });
        }));
        // ---- WebSocket ----
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // 연결 직후 현재 상태 1회 푸시
            service.BroadcastStatus();
            await hub.HandleAsync(socket, ctx.RequestAborted);
        });
    }

    // ---- 헬퍼 ----
    private static IResult Json(Macro macro) =>
        Results.Text(MacroStore.Serialize(macro), "application/json", System.Text.Encoding.UTF8);

    private static object Summary(Macro m, IReadOnlyDictionary<string, Macro> byId, List<object[]> shape) => new
    {
        id = m.Id,
        name = m.Name,
        stepCount = m.Steps.Count,
        loopCount = m.LoopCount,
        order = m.Order,
        durationMs = TotalDurationMs(m, byId, new HashSet<string>()),
        speedMultiplier = m.SpeedMultiplier,
        trigger = m.Trigger?.ToString() ?? "",
        enabled = m.Enabled,
        modifiedUtc = m.ModifiedUtc,
        shape, // 좌측 인디케이터용 — 펼친 시퀀스의 compact 토큰(인덱스 = 재생 stepIndex)
    };

    private const int MaxShape = 240; // 인디케이터 토큰 상한(초과 매크로는 잘림)

    /// <summary>펼친(전개된) 매크로 스텝을 좌측 인디케이터용 compact 토큰 배열로 만든다.
    /// "a"=행위 / "d",ms=지연 / "s",n=반복시작 / "e"=반복끝. 인덱스는 재생 stepIndex와 1:1.</summary>
    private static List<object[]> BuildShape(Macro expanded)
    {
        var shape = new List<object[]>(Math.Min(expanded.Steps.Count, MaxShape));
        foreach (var s in expanded.Steps)
        {
            if (shape.Count >= MaxShape) break;
            switch (s.Event)
            {
                case DelayEvent: shape.Add(new object[] { "d", Math.Round(s.DelayBeforeMs) }); break;
                case LoopStartEvent ls: shape.Add(new object[] { "s", Math.Max(1, ls.Count) }); break;
                case LoopEndEvent: shape.Add(new object[] { "e" }); break;
                default: shape.Add(new object[] { "a" }); break; // 키/마우스/패드/텍스트 = 행위
            }
        }
        return shape;
    }

    /// <summary>한 번 재생 기준 총 소요 시간(ms). 내부 반복(LoopStart/End) 배수를 반영하고
    /// 지연(Delay) 스텝의 DelayBeforeMs를 합산하며, 매크로 참조(MacroRef)는 대상 매크로의
    /// 1사이클 시간을 더한다(순환은 0으로 차단).</summary>
    private static double TotalDurationMs(Macro m, IReadOnlyDictionary<string, Macro> byId, HashSet<string> path)
    {
        if (!path.Add(m.Id)) return 0; // 순환 차단
        try
        {
            var stack = new Stack<int>();
            double mult = 1, total = 0;
            foreach (var s in m.Steps)
            {
                switch (s.Event)
                {
                    case LoopStartEvent ls:
                        var c = Math.Max(1, ls.Count);
                        stack.Push(c); mult *= c;
                        break;
                    case LoopEndEvent:
                        if (stack.Count > 0) mult /= stack.Pop();
                        break;
                    case DelayEvent:
                        total += s.DelayBeforeMs * mult;
                        break;
                    case MacroRefEvent r:
                        total += s.DelayBeforeMs * mult;
                        if (!string.IsNullOrEmpty(r.MacroId) && byId.TryGetValue(r.MacroId, out var sub))
                            total += TotalDurationMs(sub, byId, path) * mult;
                        break;
                }
            }
            return total;
        }
        finally { path.Remove(m.Id); }
    }

    private static async Task<Macro> ReadMacro(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        return MacroStore.Deserialize(json);
    }

    private static async Task<List<Macro>> ReadMacros(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return new List<Macro>();
        return JsonSerializer.Deserialize<List<Macro>>(json, MacroStore.Options) ?? new List<Macro>();
    }

    private static async Task<Hotkey?> ReadHotkey(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
        return JsonSerializer.Deserialize<Hotkey>(json, MacroStore.Options);
    }

    private static async Task<RecordOptions> ReadRecordOptions(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return RecordOptions.Default;
        var body = JsonSerializer.Deserialize<RecordStartBody>(json, MacroStore.Options) ?? new RecordStartBody();
        return body.ToOptions();
    }

    /// <summary>핸들러 예외를 적절한 HTTP 응답으로 변환.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InputNotReadyException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (FileNotFoundException ex) { return Results.Json(new { error = ex.Message }, statusCode: 404); }
        catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
    }

    private sealed record EnabledBody(bool Enabled = true);
    private sealed record PlaybackBody(int LoopCount = 1);
    private sealed record StopRecordingBody(string? Name, bool Persist = true);
    private sealed record RecordStartBody(
        bool Keyboard = true,
        bool MouseButtons = true,
        bool MouseMove = false,
        bool MouseWheel = true,
        bool Gamepad = false,
        double? FixedDelayMs = null)
    {
        public RecordOptions ToOptions() => new(Keyboard, MouseButtons, MouseMove, MouseWheel, Gamepad, FixedDelayMs);
    }
}
