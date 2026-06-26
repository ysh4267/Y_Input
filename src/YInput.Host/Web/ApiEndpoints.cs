using System.Net.WebSockets;
using YInput.Core.Models;
using YInput.Core.Persistence;
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

        // ---- 녹화 ----
        app.MapPost("/api/record/start", () => Guard(() =>
        {
            service.StartRecording();
            return Task.FromResult(Results.Ok(new { ok = true }));
        }));

        app.MapPost("/api/record/stop", (StopRecordingBody? body) => Guard(() =>
        {
            var macro = service.StopRecording(body?.Name);
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
        speedMultiplier = m.SpeedMultiplier,
        trigger = m.Trigger?.ToString() ?? "",
        modifiedUtc = m.ModifiedUtc,
    };

    private static async Task<Macro> ReadMacro(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var json = await reader.ReadToEndAsync();
        return MacroStore.Deserialize(json);
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

    private sealed record StopRecordingBody(string? Name);
    private sealed record GamepadSendBody(string Control, int Value);
}
