using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketReceipt;

/// <summary>
/// 사용자가 구글 시트에 배포한 Apps Script 웹 앱으로 기록을 보낸다.
/// 시트 쪽 스크립트가 첫 열(키)을 보고 이미 있는 기록은 건너뛰므로 같은 기록을 여러 번 보내도 중복되지 않는다.
/// </summary>
internal static class SheetExporter
{
    public static readonly string[] Header =
        { "키", "일시", "라벨", "서버", "분류", "아이템", "HQ", "수량", "단가", "세금", "총액" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>시트에 붙여넣을 Apps Script. 플러그인의 '스크립트 복사' 버튼이 이 내용을 복사한다.</summary>
    public const string AppsScript = """
        /** @OnlyCurrentDoc */
        // Market Receipt: 장터 구매 기록을 받아 이 시트에 적는 스크립트
        // 위 주석 덕분에 구글 권한 요청이 '이 스크립트를 붙인 시트 하나'로 좁혀집니다.
        const SHEET_NAME = '구매 기록';
        const HEADER = ['키', '일시', '라벨', '서버', '분류', '아이템', 'HQ', '수량', '단가', '세금', '총액'];

        function doPost(e) {
          const lock = LockService.getScriptLock();
          lock.waitLock(10000);
          try {
            const data = JSON.parse(e.postData.contents);
            const ss = SpreadsheetApp.getActiveSpreadsheet();
            let sheet = ss.getSheetByName(SHEET_NAME);
            if (!sheet) {
              sheet = ss.insertSheet(SHEET_NAME);
              sheet.appendRow(HEADER);
              sheet.setFrozenRows(1);
            }
            const last = sheet.getLastRow();
            const existing = new Set(last > 1
              ? sheet.getRange(2, 1, last - 1, 1).getValues().map(r => String(r[0]))
              : []);
            const rows = data.rows.filter(r => !existing.has(String(r[0])));
            if (rows.length > 0) {
              sheet.getRange(sheet.getLastRow() + 1, 1, rows.length, HEADER.length).setValues(rows);
            }
            return reply({ ok: true, added: rows.length, skipped: data.rows.length - rows.length });
          } catch (err) {
            return reply({ ok: false, error: String(err) });
          } finally {
            lock.releaseLock();
          }
        }

        function doGet() {
          return reply({ ok: true, message: 'Market Receipt 연결 확인 완료' });
        }

        function reply(obj) {
          return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
        }
        """;

    /// <summary>
    /// 브라우저에 구글 계정이 여러 개 로그인되어 있으면 주소가 …/macros/u/1/s/…/exec 처럼 복사된다.
    /// /u/숫자 부분은 '그 계정으로 로그인해서 열라'는 뜻이라 로그인 없는 플러그인은 401을 받으므로 떼어낸다.
    /// </summary>
    public static string Normalize(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url.Trim(), @"/macros/u/\d+/s/", "/macros/s/");

    /// <returns>주소가 쓸 수 있는 형태면 null, 아니면 사용자에게 보여줄 안내 문구.</returns>
    public static string? Validate(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "주소를 입력하세요.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "https:// 로 시작하는 주소를 넣어야 합니다.";
        if (uri.Host == "docs.google.com")
            return "시트 주소가 아니라 Apps Script 배포 후 나오는 웹 앱 주소(…/exec)를 넣어야 합니다.";
        if (uri.Host != "script.google.com" || !uri.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal))
            return "Apps Script 웹 앱 주소는 script.google.com/macros/s/…/exec 형태입니다.";
        return null;
    }

    /// <summary>시트 한 줄. 키는 일시+아이템+수량+단가로 만들어, 플러그인을 다시 설치해도 같은 구매는 같은 키가 된다.</summary>
    public static object[] ToRow(PurchaseRecord r, string category) => new object[]
    {
        $"{r.Time:yyyyMMddHHmmss}-{r.ItemId}-{r.Quantity}-{r.UnitPrice}",
        r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
        r.Label,
        r.World,
        category,
        r.ItemName,
        r.IsHq ? "Y" : "N",
        r.Quantity,
        r.UnitPrice,
        r.Tax,
        r.Total,
    };

    public sealed record Result(bool Ok, int Added, int Skipped, string? Error);

    public static async Task<Result> SendAsync(string url, IReadOnlyList<object[]> rows)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { rows });
            using var content = new StringContent(body, Encoding.UTF8, "text/plain");
            // Apps Script는 처리 후 결과 주소로 리다이렉트한다. HttpClient가 따라가서 결과 JSON을 받는다.
            using var res = await Http.PostAsync(Normalize(url), content).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if ((int)res.StatusCode is 401 or 403)
                return new Result(false, 0, 0,
                    "구글이 접근을 막았습니다. Apps Script 배포 관리에서 액세스 권한을 '모든 사용자'로 바꿔 새 버전으로 배포했는지, 회사·학교 계정이 아닌지 확인하세요.");
            if ((int)res.StatusCode == 404)
                return new Result(false, 0, 0, "웹 앱을 찾지 못했습니다. 배포가 삭제되었거나 주소가 잘못되었습니다.");
            if (!res.IsSuccessStatusCode)
                return new Result(false, 0, 0, $"시트 응답 오류 ({(int)res.StatusCode})");

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    return new Result(true, root.GetProperty("added").GetInt32(), root.GetProperty("skipped").GetInt32(), null);
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                return new Result(false, 0, 0, err ?? "시트가 오류를 돌려보냈습니다.");
            }
            catch (JsonException)
            {
                // 로그인 페이지 같은 HTML이 오면 배포 권한 설정 문제다
                return new Result(false, 0, 0, "응답을 읽지 못했습니다. 웹 앱 액세스 권한이 '모든 사용자'인지 확인하세요.");
            }
        }
        catch (TaskCanceledException)
        {
            return new Result(false, 0, 0, "시간이 초과되었습니다. 잠시 후 다시 시도하세요.");
        }
        catch (HttpRequestException e)
        {
            return new Result(false, 0, 0, $"연결하지 못했습니다: {e.Message}");
        }
    }
}
