using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MarketReceipt;

public sealed record PurchaseRecord(
    long Id,
    DateTime Time,
    string Label,
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

/// <summary>
/// 구매 기록을 CSV 파일 하나에 저장한다.
/// 새 구매는 끝에 한 줄 덧붙이고, 라벨 변경처럼 과거 기록을 고칠 때는 파일 전체를 다시 쓴다.
/// </summary>
public sealed class PurchaseLog
{
    private const string Header = "ID,일시,라벨,서버,아이템ID,아이템,HQ,수량,단가,세금,총액";
    private const string LegacyHeader = "일시,서버,아이템ID,아이템,HQ,수량,단가,세금,총액";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    private readonly object gate = new();
    private readonly List<PurchaseRecord> records = new();
    private long nextId = 1;

    public string FilePath { get; }

    /// <summary>기록이 바뀔 때마다 증가한다. 화면 쪽 캐시 무효화에 쓴다.</summary>
    public int Version { get; private set; }

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

    public IReadOnlyCollection<string> UsedLabels()
    {
        lock (this.gate)
            return this.records.Select(r => r.Label).Where(l => l.Length > 0).Distinct().ToList();
    }

    public PurchaseRecord Add(DateTime time, string label, string world, uint itemId, string itemName,
        bool isHq, uint quantity, uint unitPrice, uint tax)
    {
        lock (this.gate)
        {
            var r = new PurchaseRecord(this.nextId++, time, label, world, itemId, itemName, isHq, quantity, unitPrice, tax);
            this.records.Add(r);
            var isNew = !File.Exists(this.FilePath);
            using (var w = new StreamWriter(this.FilePath, append: true, Utf8Bom))
            {
                if (isNew) w.WriteLine(Header);
                w.WriteLine(ToLine(r));
            }

            this.Version++;
            return r;
        }
    }

    public void SetLabel(IReadOnlyCollection<long> ids, string label)
    {
        if (ids.Count == 0) return;
        var set = ids.ToHashSet();
        this.Mutate(r => set.Contains(r.Id) ? r with { Label = label } : r);
    }

    public void RenameLabel(string from, string to) =>
        this.Mutate(r => r.Label == from ? r with { Label = to } : r);

    public void ClearLabel(string label) =>
        this.Mutate(r => r.Label == label ? r with { Label = "" } : r);

    private void Mutate(Func<PurchaseRecord, PurchaseRecord> change)
    {
        lock (this.gate)
        {
            var changed = false;
            for (var i = 0; i < this.records.Count; i++)
            {
                var updated = change(this.records[i]);
                if (!ReferenceEquals(updated, this.records[i])) changed = true;
                this.records[i] = updated;
            }

            if (!changed) return;
            this.Rewrite();
            this.Version++;
        }
    }

    // 임시 파일에 다 쓴 뒤 바꿔치기해서, 쓰는 도중 꺼져도 기존 파일이 깨지지 않게 한다.
    private void Rewrite()
    {
        var tmp = this.FilePath + ".tmp";
        using (var w = new StreamWriter(tmp, append: false, Utf8Bom))
        {
            w.WriteLine(Header);
            foreach (var r in this.records) w.WriteLine(ToLine(r));
        }

        File.Move(tmp, this.FilePath, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(this.FilePath)) return;

        var lines = File.ReadAllLines(this.FilePath, Encoding.UTF8);
        if (lines.Length == 0) return;
        var legacy = lines[0].TrimStart('﻿') == LegacyHeader;

        foreach (var line in lines.Skip(1))
        {
            var c = Split(line);
            try
            {
                if (legacy && c.Count >= 8)
                {
                    // 0.1.0 형식: ID와 라벨이 없다
                    this.records.Add(new PurchaseRecord(this.nextId++,
                        ParseTime(c[0]), "", c[1], uint.Parse(c[2]), c[3], c[4] == "Y",
                        uint.Parse(c[5]), uint.Parse(c[6]), uint.Parse(c[7])));
                }
                else if (!legacy && c.Count >= 10)
                {
                    var id = long.Parse(c[0]);
                    this.records.Add(new PurchaseRecord(id,
                        ParseTime(c[1]), c[2], c[3], uint.Parse(c[4]), c[5], c[6] == "Y",
                        uint.Parse(c[7]), uint.Parse(c[8]), uint.Parse(c[9])));
                    this.nextId = Math.Max(this.nextId, id + 1);
                }
            }
            catch (FormatException)
            {
                // 손으로 편집하다 깨진 줄은 건너뛴다
            }
        }

        // 예전 형식이면 새 형식으로 한 번 변환해 저장
        if (legacy) this.Rewrite();
    }

    private static DateTime ParseTime(string s) =>
        DateTime.ParseExact(s, TimeFormat, CultureInfo.InvariantCulture);

    private static string ToLine(PurchaseRecord r) => string.Join(",",
        r.Id,
        r.Time.ToString(TimeFormat, CultureInfo.InvariantCulture),
        Escape(r.Label),
        Escape(r.World),
        r.ItemId,
        Escape(r.ItemName),
        r.IsHq ? "Y" : "N",
        r.Quantity,
        r.UnitPrice,
        r.Tax,
        r.Total);

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

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
