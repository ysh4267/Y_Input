using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Core.Persistence;

namespace YInput.Host.Services;

/// <summary>
/// GitHub <b>secret gist</b>로 매크로를 동기화한다(git 설치·저장소 생성 불필요 — 토큰만 있으면 앱이 gist를 자동 생성/발견).
/// gist 안의 단일 파일(<c>yinput-macros.json</c>)에 전체 매크로를 담고, <b>3-way 병합</b>(공통 조상 = 마지막 동기화
/// 스냅샷 <c>sync-base.json</c>)으로 PC별 편집을 합친다. 충돌은 <see cref="Macro.ModifiedUtc"/> 기준 최신 우선,
/// 삭제는 조상 대비 '부재'로 감지해 전파한다. 같은 토큰(계정)의 여러 PC는 파일명으로 같은 gist를 자동 공유한다.
/// 트리거: 시작 시 1회, 로컬 편집 후 디바운스(3초), 주기(45초).
/// </summary>
public sealed class GitHubSync : IDisposable
{
    public sealed class Config
    {
        public bool Enabled { get; set; }
        public string Token { get; set; } = "";       // 로컬(%APPDATA%)에만 저장, API로 노출 안 함. classic PAT + gist 스코프.
        public string GistId { get; set; } = "";       // 자동 생성/발견되어 저장(사용자 입력 아님)
        public bool IsReady => Enabled && Token.Length > 0;
    }

    private sealed class Bundle
    {
        public int Version { get; set; } = 1;
        public List<Macro> Macros { get; set; } = new();
    }

    private const string GistFile = "yinput-macros.json";
    private const string GistDesc = "YInput 매크로 동기화 — 자동 생성됨(삭제하지 마세요)";

    private readonly MacroLibrary _library;
    private readonly MacroService _service;
    private readonly string _configPath;
    private readonly string _basePath;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Config _config = new();
    private System.Threading.Timer? _periodic;
    private CancellationTokenSource? _debounce;
    private volatile bool _running;
    private volatile bool _pendingResync;

    public DateTimeOffset LastSync { get; private set; }
    public string LastResult { get; private set; } = "";

    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };

    public GitHubSync(MacroLibrary library, MacroService service, string dataRoot)
    {
        _library = library;
        _service = service;
        _configPath = System.IO.Path.Combine(dataRoot, "sync-config.json");
        _basePath = System.IO.Path.Combine(dataRoot, "sync-base.json");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YInput-Sync", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        LoadConfig();
    }

    public bool Syncing => _running;

    /// <summary>UI용 상태(토큰 자체는 빼고 존재 여부만).</summary>
    public object StatusData() => new
    {
        enabled = _config.Enabled,
        hasToken = _config.Token.Length > 0,
        hasGist = _config.GistId.Length > 0,
        ready = _config.IsReady,
        lastSync = LastSync == default ? null : (DateTimeOffset?)LastSync,
        lastResult = LastResult,
        syncing = _running,
    };

    // ---------- 설정 ----------
    private void LoadConfig()
    {
        try { if (File.Exists(_configPath)) _config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath)) ?? new(); }
        catch { _config = new(); }
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, ConfigJson)); }
        catch (Exception ex) { _service.Log("error", "동기화 설정 저장 실패: " + ex.Message); }
    }

    /// <summary>UI에서 설정 갱신. <paramref name="token"/>이 null이면 기존 토큰 유지(빈 문자열이면 지움).</summary>
    public void UpdateConfig(bool enabled, string? token)
    {
        _config.Enabled = enabled;
        if (token is not null)
        {
            var t = token.Trim();
            if (t != _config.Token) _config.GistId = ""; // 토큰(계정)이 바뀌면 gist를 다시 발견/생성
            _config.Token = t;
        }
        SaveConfig();
        RestartPeriodic();
        if (_config.IsReady) _ = SyncAsync("설정 변경");
        else _service.BroadcastSyncStatus(StatusData());
    }

    // ---------- 스케줄 ----------
    public void Start()
    {
        RestartPeriodic();
        if (_config.IsReady) _ = SyncAsync("시작");
    }

    private void RestartPeriodic()
    {
        _periodic?.Dispose();
        _periodic = null;
        if (_config.IsReady)
            _periodic = new System.Threading.Timer(_ => { _ = SyncAsync("주기"); }, null,
                TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
    }

    /// <summary>로컬 변경 후 호출 — 3초 디바운스 후 동기화(빠른 편집을 하나로 묶음).</summary>
    public void SchedulePush()
    {
        if (!_config.IsReady) return;
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(3000, cts.Token); } catch { return; }
            if (!cts.IsCancellationRequested) await SyncAsync("편집");
        });
    }

    // ---------- 핵심: 3-way 병합 동기화(양방향) ----------
    public async Task<string> SyncAsync(string reason)
    {
        if (!_config.IsReady) { LastResult = "설정 필요(토큰)"; return LastResult; }
        if (!await _lock.WaitAsync(0)) { _pendingResync = true; return "대기(진행 중)"; } // 겹치면 현재 완료 후 1회 재실행
        _running = true;
        _service.BroadcastSyncStatus(StatusData());
        try
        {
            var local = _library.LoadAll().ToDictionary(m => m.Id, m => m);
            var (gistId, remoteJson, created) = await EnsureGistAsync(local);

            Dictionary<string, Macro> merged;
            bool changedLocal, changedRemote;
            if (created)
            {
                // 방금 로컬 내용으로 gist 생성 — 원격 = 로컬. 추가 변경 없음.
                merged = local; changedLocal = false; changedRemote = false;
            }
            else if (remoteJson is null)
            {
                // gist는 있는데 우리 파일이 비어/없음 — 로컬을 올린다(로컬 삭제 절대 안 함 = 데이터 보호).
                merged = local; changedLocal = false; changedRemote = true;
            }
            else if (string.IsNullOrWhiteSpace(remoteJson))
            {
                throw new Exception("원격 내용이 비어 있어 동기화를 중단했습니다(데이터 보호).");
            }
            else
            {
                var remote = ParseBundle(remoteJson); // 손상 JSON이면 예외 → 중단(로컬 대량 삭제 방지)
                Dictionary<string, Macro> ancestor;
                try { ancestor = ParseBundle(TryReadBase()); } catch { ancestor = new(); } // 조상 손상은 안전(삭제 판정은 조상-존재 필요)
                (merged, changedLocal, changedRemote) = Merge(ancestor, local, remote);
            }

            if (changedLocal) ApplyLocal(merged, local);

            var mergedJson = BuildBundle(merged);
            bool pushed = false;
            if (changedRemote) { await PatchGistAsync(gistId, mergedJson); pushed = true; }

            WriteBase(mergedJson);
            if (changedLocal) _service.OnExternalMacrosChanged(); // 핫키 재등록 + UI 새로고침 알림

            LastSync = DateTimeOffset.UtcNow;
            LastResult = $"완료 · 매크로 {merged.Count}개"
                + (created ? " · gist 생성" : "") + (pushed ? " · 올림" : "") + (changedLocal ? " · 내려받음" : "");
            _service.Log("info", $"[동기화] {LastResult} ({reason})");
            return LastResult;
        }
        catch (Exception ex)
        {
            LastResult = "실패: " + ex.Message;
            _service.Log("error", "[동기화] " + ex.Message);
            return LastResult;
        }
        finally
        {
            _running = false;
            _lock.Release();
            _service.BroadcastSyncStatus(StatusData());
            if (_pendingResync) { _pendingResync = false; _ = Task.Run(() => SyncAsync("후속")); }
        }
    }

    // ---------- 병합 ----------
    // 조상(base) 대비 로컬/원격 변경을 판정해 3-way 병합. 충돌은 최신 수정(ModifiedUtc) 우선.
    private static (Dictionary<string, Macro> merged, bool changedLocal, bool changedRemote)
        Merge(Dictionary<string, Macro> ancestor, Dictionary<string, Macro> local, Dictionary<string, Macro> remote)
    {
        var ids = new HashSet<string>(ancestor.Keys);
        ids.UnionWith(local.Keys);
        ids.UnionWith(remote.Keys);
        var merged = new Dictionary<string, Macro>();
        foreach (var id in ids)
        {
            ancestor.TryGetValue(id, out var b);
            local.TryGetValue(id, out var l);
            remote.TryGetValue(id, out var r);
            bool lChanged = !Same(l, b), rChanged = !Same(r, b);
            Macro? pick;
            if (!lChanged && !rChanged) pick = l;         // 둘 다 조상 그대로
            else if (lChanged && !rChanged) pick = l;     // 로컬만 변경(삭제=null 포함)
            else if (!lChanged && rChanged) pick = r;     // 원격만 변경
            else pick = ConflictPick(l, r);               // 양쪽 변경 → 최신 우선
            if (pick is not null) merged[id] = pick;
        }
        return (merged, !SameSet(merged, local), !SameSet(merged, remote));
    }

    private static Macro? ConflictPick(Macro? l, Macro? r)
    {
        if (l is null) return r; // 로컬 삭제 vs 원격 편집 → 데이터 보존(원격 유지)
        if (r is null) return l; // 원격 삭제 vs 로컬 편집 → 로컬 유지
        return l.ModifiedUtc >= r.ModifiedUtc ? l : r; // 최신 수정 우선
    }

    private static bool Same(Macro? a, Macro? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.ModifiedUtc == b.ModifiedUtc;
    }

    private static bool SameSet(Dictionary<string, Macro> a, Dictionary<string, Macro> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.ModifiedUtc == kv.Value.ModifiedUtc);

    // 병합 결과를 로컬 파일에 반영(ModifiedUtc 보존 — 라이브러리 Save는 지금 시각으로 덮어써 재동기화 루프를 만들므로 직접 저장).
    private void ApplyLocal(Dictionary<string, Macro> merged, Dictionary<string, Macro> local)
    {
        foreach (var (id, macro) in merged)
            if (!local.TryGetValue(id, out var cur) || cur.ModifiedUtc != macro.ModifiedUtc)
                MacroStore.Save(macro, _library.PathFor(id));
        foreach (var id in local.Keys)
            if (!merged.ContainsKey(id))
                RecycleBin.Delete(_library.PathFor(id));
    }

    // 손상 JSON이면 예외를 던진다(호출측에서 원격=중단 / 조상=빈 집합으로 안전 처리).
    private static Dictionary<string, Macro> ParseBundle(string? json)
    {
        var map = new Dictionary<string, Macro>();
        if (string.IsNullOrWhiteSpace(json)) return map;
        var bundle = JsonSerializer.Deserialize<Bundle>(json, MacroStore.Options);
        if (bundle?.Macros is { } list)
            foreach (var m in list)
                if (!string.IsNullOrEmpty(m.Id)) map[m.Id] = m;
        return map;
    }

    private static string BuildBundle(Dictionary<string, Macro> macros)
    {
        var ordered = macros.Values.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return JsonSerializer.Serialize(new Bundle { Version = 1, Macros = ordered }, MacroStore.Options);
    }

    private string? TryReadBase() { try { return File.Exists(_basePath) ? File.ReadAllText(_basePath) : null; } catch { return null; } }
    private void WriteBase(string json) { try { File.WriteAllText(_basePath, json); } catch { /* 무시 */ } }

    // ---------- GitHub Gist API ----------
    private HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        r.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return r;
    }

    // 저장된 gist → 발견 → 생성 순으로 확보. 반환: (gistId, 우리 파일 내용 or null, 방금 생성했는지)
    private async Task<(string gistId, string? remoteJson, bool created)> EnsureGistAsync(Dictionary<string, Macro> local)
    {
        if (!string.IsNullOrEmpty(_config.GistId))
        {
            var (ok, json) = await TryGetGistFileAsync(_config.GistId);
            if (ok) return (_config.GistId, json, false);
            _config.GistId = ""; SaveConfig(); // 404 등 → 무효화하고 재발견/생성
        }
        var found = await DiscoverGistAsync();
        if (found is not null)
        {
            _config.GistId = found; SaveConfig();
            var (_, json) = await TryGetGistFileAsync(found);
            return (found, json, false);
        }
        var id = await CreateGistAsync(BuildBundle(local)); // 최초 — 로컬 내용으로 생성
        _config.GistId = id; SaveConfig();
        _service.Log("info", "[동기화] 새 secret gist를 생성했습니다.");
        return (id, null, true);
    }

    private async Task<(bool ok, string? json)> TryGetGistFileAsync(string id)
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/gists/{id}"));
        if (resp.StatusCode == HttpStatusCode.NotFound) return (false, null);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(FriendlyError(resp.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return (true, null);
        if (!files.TryGetProperty(GistFile, out var file)) return (true, null); // gist는 있는데 우리 파일 없음
        // 큰 파일은 content가 truncated → raw_url로 원문 조회
        if (file.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True
            && file.TryGetProperty("raw_url", out var raw) && raw.GetString() is { } rawUrl)
        {
            using var r2 = await _http.SendAsync(Req(HttpMethod.Get, rawUrl));
            if (!r2.IsSuccessStatusCode) throw new Exception("gist 원문 조회 실패");
            return (true, await r2.Content.ReadAsStringAsync());
        }
        return (true, file.TryGetProperty("content", out var c) ? c.GetString() : null);
    }

    // 같은 계정의 기존 YInput gist를 파일명으로 찾는다(여러 PC가 같은 gist 자동 공유).
    private async Task<string?> DiscoverGistAsync()
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, "https://api.github.com/gists?per_page=100"));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(FriendlyError(resp.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var g in doc.RootElement.EnumerateArray())
            if (g.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object
                && files.TryGetProperty(GistFile, out _) && g.TryGetProperty("id", out var id))
                return id.GetString();
        return null;
    }

    private async Task<string> CreateGistAsync(string content)
    {
        var payload = new
        {
            description = GistDesc,
            @public = false, // secret gist
            files = new Dictionary<string, object> { [GistFile] = new { content } },
        };
        using var req = Req(HttpMethod.Post, "https://api.github.com/gists");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(FriendlyError(resp.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } s && s.Length > 0
            ? s : throw new Exception("gist 생성 응답에 id가 없습니다.");
    }

    private async Task PatchGistAsync(string id, string content)
    {
        var payload = new { files = new Dictionary<string, object> { [GistFile] = new { content } } };
        using var req = Req(HttpMethod.Patch, $"https://api.github.com/gists/{id}");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new Exception(FriendlyError(resp.StatusCode, body));
    }

    private static string FriendlyError(HttpStatusCode code, string body)
    {
        var detail = "";
        try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("message", out var m)) detail = m.GetString() ?? ""; }
        catch { /* 무시 */ }
        return code switch
        {
            HttpStatusCode.Unauthorized => "토큰이 유효하지 않습니다(401) — classic 토큰에 gist 권한이 있는지 확인하세요.",
            HttpStatusCode.Forbidden => "권한 없음(gist 스코프 필요) 또는 요청 한도 초과(403). " + detail,
            HttpStatusCode.NotFound => "gist를 찾을 수 없습니다(404).",
            _ => $"GitHub 오류 {(int)code}" + (detail.Length > 0 ? ": " + detail : ""),
        };
    }

    public void Dispose()
    {
        _periodic?.Dispose();
        _debounce?.Cancel();
        _http.Dispose();
        _lock.Dispose();
    }
}
