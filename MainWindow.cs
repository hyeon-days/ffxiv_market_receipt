using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MarketReceipt;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private string filter = "";
    private int range = 1; // 0: 오늘, 1: 7일, 2: 30일, 3: 전체

    private static readonly string[] Ranges = { "오늘", "최근 7일", "최근 30일", "전체" };

    public MainWindow(Plugin plugin) : base("장터 구매 기록###MarketReceiptMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 300) };
    }

    public override void Draw()
    {
        var since = this.range switch
        {
            0 => DateTime.Today,
            1 => DateTime.Now.AddDays(-7),
            2 => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        var rows = this.plugin.PurchaseLog.Snapshot()
            .Where(r => r.Time >= since)
            .Where(r => this.filter.Length == 0 || r.ItemName.Contains(this.filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Time)
            .ToList();

        ImGui.SetNextItemWidth(120);
        ImGui.Combo("##range", ref this.range, Ranges);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##filter", "아이템 이름 검색", ref this.filter, 64);
        ImGui.SameLine();
        if (ImGui.Button("CSV 폴더 열기"))
        {
            var dir = System.IO.Path.GetDirectoryName(this.plugin.PurchaseLog.FilePath)!;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        ImGui.TextUnformatted($"{rows.Count}건, 합계 {rows.Sum(r => r.Total):N0}길");
        ImGui.Separator();

        if (!ImGui.BeginTable("##purchases", 7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("일시");
        ImGui.TableSetupColumn("서버");
        ImGui.TableSetupColumn("아이템", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("수량");
        ImGui.TableSetupColumn("단가");
        ImGui.TableSetupColumn("세금");
        ImGui.TableSetupColumn("총액");
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Time.ToString("MM-dd HH:mm"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.World);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.IsHq ? r.ItemName + " (HQ)" : r.ItemName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Quantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.UnitPrice.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Tax.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Total.ToString("N0"));
        }

        ImGui.EndTable();
    }
}
