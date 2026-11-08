using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MarketReceipt;

public sealed class MainWindow : Window
{
    private static readonly string[] Ranges = { "오늘", "최근 7일", "최근 30일", "전체" };
    private const string NoLabel = "(라벨 없음)";

    private readonly Plugin plugin;

    // 필터 상태
    private int range = 1;          // Ranges 인덱스
    private string? labelFilter;    // null: 전체, "": 라벨 없음, 그 외: 해당 라벨
    private string search = "";

    // 화면용 캐시. 기록이나 필터가 바뀔 때만 다시 계산한다.
    private List<PurchaseRecord> view = new();
    private long viewSum;
    private (int Version, int Range, string? Label, string Search, DateTime Day) viewKey;

    // 선택과 일괄 지정
    private readonly HashSet<long> selected = new();
    private string assignLabel = "";

    // 라벨 관리
    private string newLabel = "";
    private readonly Dictionary<string, string> renameBuffers = new();

    public MainWindow(Plugin plugin) : base("장터 구매 기록###MarketReceiptMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 360) };
    }

    private Configuration Config => this.plugin.Config;

    public override void Draw()
    {
        this.RefreshView();

        this.DrawFilters();
        this.DrawAutoLabel();
        this.DrawLabelManager();
        ImGui.Separator();
        this.DrawSelectionBar();
        this.DrawTable();
    }

    private void RefreshView()
    {
        var key = (this.plugin.PurchaseLog.Version, this.range, this.labelFilter, this.search, DateTime.Today);
        if (key == this.viewKey) return;
        this.viewKey = key;

        var since = this.range switch
        {
            0 => DateTime.Today,
            1 => DateTime.Now.AddDays(-7),
            2 => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        this.view = this.plugin.PurchaseLog.Snapshot()
            .Where(r => r.Time >= since)
            .Where(r => this.labelFilter == null || r.Label == this.labelFilter)
            .Where(r => this.search.Length == 0 || r.ItemName.Contains(this.search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Time)
            .ToList();
        this.viewSum = this.view.Sum(r => r.Total);

        // 화면에서 사라진 항목은 선택에서도 뺀다
        var visible = this.view.Select(r => r.Id).ToHashSet();
        this.selected.IntersectWith(visible);
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##range", ref this.range, Ranges);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        var preview = this.labelFilter switch
        {
            null => "모든 라벨",
            "" => NoLabel,
            _ => this.labelFilter,
        };
        if (ImGui.BeginCombo("##labelFilter", preview))
        {
            if (ImGui.Selectable("모든 라벨", this.labelFilter == null)) this.labelFilter = null;
            if (ImGui.Selectable(NoLabel, this.labelFilter == "")) this.labelFilter = "";
            foreach (var l in this.Config.Labels)
                if (ImGui.Selectable(l, this.labelFilter == l)) this.labelFilter = l;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##search", "아이템 이름 검색", ref this.search, 64);

        ImGui.SameLine();
        if (ImGui.Button("CSV 폴더 열기"))
        {
            var dir = System.IO.Path.GetDirectoryName(this.plugin.PurchaseLog.FilePath)!;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    private void DrawAutoLabel()
    {
        var enabled = this.Config.AutoLabelEnabled;
        if (ImGui.Checkbox("새 구매에 라벨 자동 적용", ref enabled))
        {
            this.Config.AutoLabelEnabled = enabled;
            this.Config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        var current = this.Config.Labels.Contains(this.Config.AutoLabel) ? this.Config.AutoLabel : "라벨 선택";
        if (ImGui.BeginCombo("##autoLabel", current))
        {
            foreach (var l in this.Config.Labels)
            {
                if (!ImGui.Selectable(l, this.Config.AutoLabel == l)) continue;
                this.Config.AutoLabel = l;
                this.Config.Save();
            }

            ImGui.EndCombo();
        }

        if (this.Config.AutoLabelEnabled && !this.Config.Labels.Contains(this.Config.AutoLabel))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("라벨을 골라야 적용됩니다.");
        }
    }

    private void DrawLabelManager()
    {
        if (!ImGui.CollapsingHeader("라벨 관리")) return;

        string? toDelete = null;
        (string From, string To)? toRename = null;

        foreach (var l in this.Config.Labels)
        {
            ImGui.PushID("label_" + l);
            if (!this.renameBuffers.TryGetValue(l, out var buf)) buf = l;

            ImGui.SetNextItemWidth(200);
            var enter = ImGui.InputText("##name", ref buf, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            this.renameBuffers[l] = buf;

            ImGui.SameLine();
            var renameClicked = ImGui.Button("이름 변경");
            if ((enter || renameClicked) && buf.Trim() != l) toRename = (l, buf);

            ImGui.SameLine();
            if (ImGui.Button("삭제") && ImGui.GetIO().KeyCtrl) toDelete = l;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl을 누른 채 클릭하면 삭제됩니다. 이 라벨이 붙은 기록은 '라벨 없음'이 됩니다.");

            ImGui.PopID();
        }

        // 목록을 순회하는 동안 바꾸지 않도록 루프 밖에서 반영한다
        if (toRename is { } rn)
        {
            this.renameBuffers.Remove(rn.From);
            if (this.plugin.RenameLabel(rn.From, rn.To))
            {
                if (this.labelFilter == rn.From) this.labelFilter = rn.To.Trim();
                if (this.assignLabel == rn.From) this.assignLabel = rn.To.Trim();
            }
            else
            {
                Plugin.Chat.PrintError("이름이 비었거나 이미 있는 라벨입니다.");
            }
        }

        if (toDelete != null)
        {
            this.renameBuffers.Remove(toDelete);
            this.plugin.DeleteLabel(toDelete);
            if (this.labelFilter == toDelete) this.labelFilter = null;
            if (this.assignLabel == toDelete) this.assignLabel = "";
        }

        ImGui.SetNextItemWidth(200);
        var add = ImGui.InputTextWithHint("##newLabel", "새 라벨 이름", ref this.newLabel, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var addClicked = ImGui.Button("추가");
        if ((add || addClicked) && this.newLabel.Trim().Length > 0)
        {
            if (this.plugin.AddLabel(this.newLabel)) this.newLabel = "";
            else Plugin.Chat.PrintError("이미 있는 라벨입니다.");
        }
    }

    private void DrawSelectionBar()
    {
        ImGui.TextUnformatted($"{this.view.Count:N0}건, 합계 {this.viewSum:N0}길");

        ImGui.SameLine();
        ImGui.TextUnformatted($"   선택 {this.selected.Count:N0}건");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("##assign", this.assignLabel.Length == 0 ? NoLabel : this.assignLabel))
        {
            if (ImGui.Selectable(NoLabel, this.assignLabel == "")) this.assignLabel = "";
            foreach (var l in this.Config.Labels)
                if (ImGui.Selectable(l, this.assignLabel == l)) this.assignLabel = l;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("선택 항목에 지정") && this.selected.Count > 0)
        {
            this.plugin.PurchaseLog.SetLabel(this.selected.ToList(), this.assignLabel);
            this.selected.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("모두 선택"))
            foreach (var r in this.view) this.selected.Add(r.Id);

        ImGui.SameLine();
        if (ImGui.Button("선택 해제")) this.selected.Clear();
    }

    private void DrawTable()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##purchases", 9, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("일시");
        ImGui.TableSetupColumn("라벨");
        ImGui.TableSetupColumn("서버");
        ImGui.TableSetupColumn("아이템", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("수량");
        ImGui.TableSetupColumn("단가");
        ImGui.TableSetupColumn("세금");
        ImGui.TableSetupColumn("총액");
        ImGui.TableHeadersRow();

        // 보이는 줄만 그려서 기록이 많아도 가볍게 유지한다
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(this.view.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var r = this.view[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var isSelected = this.selected.Contains(r.Id);
                if (ImGui.Checkbox("##sel" + r.Id, ref isSelected))
                {
                    if (isSelected) this.selected.Add(r.Id);
                    else this.selected.Remove(r.Id);
                }

                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Time.ToString("MM-dd HH:mm"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Label);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.World);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.IsHq ? r.ItemName + " (HQ)" : r.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.UnitPrice.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Tax.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Total.ToString("N0"));
            }
        }

        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }
}
