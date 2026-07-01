using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Core.Persistence;

namespace YInput.Host.Services;

/// <summary>
/// GitHub 비공개 저장소로 매크로를 동기화한다(Contents API — git 설치 불필요, 어느 PC에서나 동작).
/// 저장소의 단일 파일(기본 macros.json)에 전체 매크로를 담고, <b>3-way 병합</b>(공통 조상 = 마지막 동기화 스냅샷
/// <c>sync-base.json</c>)으로 PC별 편집을 합친다. 충돌은 <see cref="Macro.ModifiedUtc"/> 기준 최신 우선,
/// 삭제는 조상 대비 '부재'로 감지해 전파한다(조상 파일이 삭제 톰스톤 역할).
/// 트리거: 시작 시 1회, 로컬 편집 후 디바운스(3초), 주기(45초).
/// </summary>
public sealed class GitHubSync : IDisposable
{
    public sealed class Config
    {
        public bool Enabled { get; set; }
        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Branch { get; set; } = "main";
        public string Path { get; set; } = "macros.json";
        public string Token { get; set; } = ""; // 로컬(%APPDATA%)에만 저장, API로 노출하지 않음
        public bool IsReady => Enabled && Owner.Length > 0 && Repo.Length > 0 && Token.Length > 0;
    }

    private sealed class Bundle
    {
        public int Version { get; set; } = 1;
        public List<Macro> Macros { get; set; } = new();
    }

    private sealed class ConflictException : Exception { }

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
        owner = _config.Owner,
        repo = _config.Repo,
        branch = _config.Branch,
        path = _config.Path,
        hasToken = _config.Token.Length > 0,
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
        if (string.IsNullOrWhiteSpace(_config.Branch)) _config.Branch = "main";
        if (string.IsNullOrWhiteSpace(_config.Path)) _config.Path = "macros.json";
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, ConfigJson)); }
        catch (Exception ex) { _service.Log("error", "동기화 설정 저장 실패: " + ex.Message); }
    }

    /// <summary>UI에서 설정 갱신. <paramref name="token"/>이 null이면 기존 토큰 유지(빈 문자열이면 지움).</summary>
    public void UpdateConfig(bool enabled, string? owner, string? repo, string? branch, string? path, string? token)
    {
        _config.Enabled = enabled;
        _config.Owner = (owner ?? "").Trim();
        _config.Repo = (repo ?? "").Trim();
        _config.Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch!.Trim();
        _config.Path = string.IsNullOrWhiteSpace(path) ? "macros.json" : path!.Trim();
        if (token is not null) _config.Token = token.Trim();
        SaveConfig();
        RestartPeriodic();
        if (_config.IsReady) _ = SyncAsync("설정 변경");
        else { _service.BroadcastStatus(); }
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
        if (!_config.IsReady) { LastResult = "설정 필요(저장소·토큰)"; return LastResult; }
        if (!await _lock.WaitAsync(0)) { _pendingResync = true; return "대기(진행 중)"; } // 겹치면 현재 완료 후 1회 재실행
        _running = true;
        _service.BroadcastSyncStatus(StatusData());
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var (remoteJson, sha) = await GetRemoteAsync();
                var local = _library.LoadAll().ToDictionary(m => m.Id, m => m);

                Dictionary<string, Macro> merged;
                bool changedLocal, changedRemote;
                if (remoteJson is null)
                {
                    // 원격 파일 없음(최초 업로드 또는 파일이 사라짐) — 로컬을 그대로 올린다. 로컬 삭제는 절대 안 함(데이터 보호).
                    merged = local; changedLocal = false; changedRemote = true;
                }
                else if (string.IsNullOrWhiteSpace(remoteJson))
                {
                    throw new Exception("원격 파일이 비어 있어 동기화를 중단했습니다(데이터 보호).");
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
                if (changedRemote || remoteJson is null)
                {
                    try { sha = await PutRemoteAsync(mergedJson, sha, reason); pushed = true; }
                    catch (ConflictException) { await Task.Delay(400); continue; } // sha 낡음 → 다시 GET/병합
                }

                WriteBase(mergedJson);
                if (changedLocal) _service.OnExternalMacrosChanged(); // 핫키 재등록 + UI 새로고침 알림

                LastSync = DateTimeOffset.UtcNow;
                LastResult = $"완료 · 매크로 {merged.Count}개"
                    + (pushed ? " · 올림" : "") + (changedLocal ? " · 내려받음" : "");
                _service.Log("info", $"[동기화] {LastResult} ({reason})");
                return LastResult;
            }
            LastResult = "충돌이 반복돼 중단(잠시 후 자동 재시도)";
            _service.Log("warn", "[동기화] " + LastResult);
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

    // ---------- GitHub Contents API ----------
    private HttpRequestMessage Req(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        r.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return r;
    }

    private async Task<(string? json, string? sha)> GetRemoteAsync()
    {
        var url = $"https://api.github.com/repos/{_config.Owner}/{_config.Repo}/contents/{_config.Path}?ref={Uri.EscapeDataString(_config.Branch)}";
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, url));
        if (resp.StatusCode == HttpStatusCode.NotFound) return (null, null); // 파일(또는 저장소/브랜치) 없음 → 최초 업로드로 처리
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception(FriendlyError(resp.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sha = root.TryGetProperty("sha", out var s) ? s.GetString() : null;
        var encoding = root.TryGetProperty("encoding", out var enc) ? enc.GetString() : "base64";
        if (encoding != "base64") throw new Exception("원격 파일이 너무 큽니다(1MB 초과) — 매크로 수를 줄이거나 나눠 주세요.");
        var contentB64 = root.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
        return (DecodeBase64(contentB64), sha);
    }

    private async Task<string?> PutRemoteAsync(string json, string? sha, string reason)
    {
        var url = $"https://api.github.com/repos/{_config.Owner}/{_config.Repo}/contents/{_config.Path}";
        var payload = new Dictionary<string, object?>
        {
            ["message"] = $"YInput 매크로 동기화: {reason} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm})",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
            ["branch"] = _config.Branch,
        };
        if (!string.IsNullOrEmpty(sha)) payload["sha"] = sha;
        using var req = Req(HttpMethod.Put, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode == HttpStatusCode.Conflict || (int)resp.StatusCode == 422) throw new ConflictException(); // sha 낡음
        if (!resp.IsSuccessStatusCode) throw new Exception(FriendlyError(resp.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("content", out var content) && content.TryGetProperty("sha", out var ns)
            ? ns.GetString() : sha;
    }

    private static string DecodeBase64(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        var clean = b64.Replace("\n", "").Replace("\r", "");
        return Encoding.UTF8.GetString(Convert.FromBase64String(clean));
    }

    private static string FriendlyError(HttpStatusCode code, string body)
    {
        var detail = "";
        try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("message", out var m)) detail = m.GetString() ?? ""; }
        catch { /* 무시 */ }
        return code switch
        {
            HttpStatusCode.Unauthorized => "토큰이 유효하지 않습니다(401) — 토큰을 다시 확인하세요.",
            HttpStatusCode.Forbidden => "권한 없음 또는 요청 한도 초과(403). " + detail,
            HttpStatusCode.NotFound => "저장소·브랜치·경로를 찾을 수 없습니다(404) — 소유자/이름/브랜치를 확인하세요.",
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
