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
            var summaries = service.ListMacros().Select(Summary);
            return Results.Json(summaries);
        });

        app.MapGet("/api/macros/{id}", (string id) =>
        {
            var macro = service.GetMacro(id);
            return macro is null
                ? Results.NotFound(new { error = "매크로를 찾을 수 없습니다." })
                : Json(macro);
        });

        // ---- 매크로 생성/수정 ----
        app.MapPost("/api/macros", (HttpRequest req) => Guard(async () =>
        {
            var macro = await ReadMacro(req);
            if (string.IsNullOrWhiteSpace(macro.Id)) macro.Id = Guid.NewGuid().ToString("N");
            service.SaveMacro(macro);
            return Json(macro);
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

        app.MapPost("/api/gamepad/send", (GamepadSendBody body) => Guard(() =>
        {
            if (!Enum.TryParse<GamepadControl>(body.Control, ignoreCase: true, out var control))
                throw new ArgumentException("알 수 없는 컨트롤: " + body.Control);
            service.SendGamepad(control, body.Value);
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

        // ---- Git 업데이트 동기화(개발 PC: 소스 트리 + dotnet SDK + git 필요) ----
        app.MapGet("/api/app/update/check", () => Guard(async () =>
        {
            var r = await Task.Run(AppUpdater.Check);
            return Results.Json(new { ok = r.Ok, behind = r.Behind, current = r.Current, message = r.Message });
        }));
        app.MapPost("/api/app/update", () => Guard(() =>
        {
            var r = AppUpdater.Start();
            service.Log(r.Ok ? "info" : "error", r.Ok ? "업데이트 시작 — 곧 재빌드/재시작됩니다." : ("업데이트 실패: " + r.Message));
            return Task.FromResult(Results.Json(new { started = r.Ok, message = r.Message }));
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

    private static object Summary(Macro m) => new
    {
        id = m.Id,
        name = m.Name,
        stepCount = m.Steps.Count,
        loopCount = m.LoopCount,
        durationMs = TotalDurationMs(m),
        speedMultiplier = m.SpeedMultiplier,
        trigger = m.Trigger?.ToString() ?? "",
        enabled = m.Enabled,
        modifiedUtc = m.ModifiedUtc,
    };

    /// <summary>한 번 재생 기준 총 소요 시간(ms). 내부 반복(LoopStart/End) 배수를 반영하며
    /// 편집기의 총 시간 계산과 동일하게 지연(Delay) 스텝의 DelayBeforeMs만 합산한다.</summary>
    private static double TotalDurationMs(Macro m)
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
            }
        }
        return total;
    }

    private static async Task<Macro> ReadMacro(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        return MacroStore.Deserialize(json);
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
    private sealed record GamepadSendBody(string Control, int Value);
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
