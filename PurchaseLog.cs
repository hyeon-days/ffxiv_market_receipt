using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MarketReceipt;

public sealed record PurchaseRecord(
    DateTime Time,
    string World,
    uint ItemId,
    string ItemName,
    bool IsHq,
    uint Quantity,
    uint UnitPrice,
    uint Tax)
{
    public long Total => (long)UnitPrice * Quantity + Tax;
}

/// <summary>구매 기록을 CSV 파일에 누적 저장하고 메모리에 들고 있는다.</summary>
public sealed class PurchaseLog
{
    private const string Header = "일시,서버,아이템ID,아이템,HQ,수량,단가,세금,총액";

    private readonly object gate = new();
    private readonly List<PurchaseRecord> records = new();

    public string FilePath { get; }

    public PurchaseLog(string directory)
    {
        Directory.CreateDirectory(directory);
        this.FilePath = Path.Combine(directory, "purchases.csv");
        this.Load();
    }

    public IReadOnlyList<PurchaseRecord> Snapshot()
    {
        lock (this.gate) return this.records.ToList();
    }

    public void Add(PurchaseRecord r)
    {
        lock (this.gate)
        {
            this.records.Add(r);
            var isNew = !File.Exists(this.FilePath);
            // 엑셀에서 한글이 깨지지 않도록 UTF-8 BOM으로 저장
            using var w = new StreamWriter(this.FilePath, append: true, new UTF8Encoding(true));
            if (isNew) w.WriteLine(Header);
            w.WriteLine(string.Join(",",
                r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Escape(r.World),
                r.ItemId,
                Escape(r.ItemName),
                r.IsHq ? "Y" : "N",
                r.Quantity,
                r.UnitPrice,
                r.Tax,
                r.Total));
        }
    }

    private void Load()
    {
        if (!File.Exists(this.FilePath)) return;
        foreach (var line in File.ReadLines(this.FilePath, Encoding.UTF8).Skip(1))
        {
            var c = Split(line);
            if (c.Count < 8) continue;
            try
            {
                this.records.Add(new PurchaseRecord(
                    DateTime.ParseExact(c[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    c[1], uint.Parse(c[2]), c[3], c[4] == "Y",
                    uint.Parse(c[5]), uint.Parse(c[6]), uint.Parse(c[7])));
            }
            catch (FormatException)
            {
                // 손으로 편집하다 깨진 줄은 건너뛴다
            }
        }
    }

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static List<string> Split(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
