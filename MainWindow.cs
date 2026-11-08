using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace MarketReceipt;

public sealed class MainWindow : Window
{
    private static readonly string[] Ranges = { "오늘", "최근 7일", "최근 30일", "전체" };
    private static readonly string[] Sorts = { "최신순", "오래된순", "총액 높은순", "아이템 이름순", "종류별", "라벨별" };

    private static readonly Vector4 AccentBg = new(0.26f, 0.47f, 0.85f, 1f);
    private static readonly Vector4 AccentHover = new(0.33f, 0.55f, 0.95f, 1f);
    private static readonly Vector4 AutoOnBg = new(0.20f, 0.45f, 0.25f, 1f);
    private static readonly Vector4 AutoOnHover = new(0.25f, 0.55f, 0.30f, 1f);
    private static readonly Vector4 Muted = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 Danger = new(1.00f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 SelectionBar = new(0.26f, 0.47f, 0.85f, 0.15f);

    private readonly Plugin plugin;

    // 필터
    private int range = 1;
    private string? labelFilter; // null: 모든 라벨, "": 라벨 없음
    private string search = "";
    private int sort; // Sorts 인덱스, 0이 최신순

    // 화면용 캐시. 기록이나 필터가 바뀔 때만 다시 계산한다.
    private List<PurchaseRecord> view = new();
    private long viewSum;
    private (int Version, int Range, string? Label, string Search, int Sort, DateTime Day) viewKey;

    // 선택과 라벨링
    private readonly HashSet<long> selected = new();
    private string? assignLabel; // null: 아직 안 고름, "": 라벨 제거
    private bool assignError;

    // 라벨 관리
    private string newLabel = "";
    private string labelError = "";
    private string? pendingDelete;
    private readonly Dictionary<string, string> renameBuffers = new();

    // 시트 내보내기
    private string sheetUrlBuffer = "";
    private string sheetMessage = "";
    private bool sheetMessageIsError;
    private volatile bool exporting;
    private Vector2 sheetAnchor;

    // 아이템 번호 → 아이콘 번호
    private readonly Dictionary<uint, uint> iconIds = new();

    // 아이템 번호 → 분류 이름(무기, 가구 등)
    private readonly Dictionary<uint, string> categories = new();

    public MainWindow(Plugin plugin) : base("장터 구매 기록###MarketReceiptMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 320) };
        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.InfoCircle,
            IconOffset = new Vector2(2, 1),
            Click = _ => plugin.HelpWindow.Toggle(),
            ShowTooltip = () => ImGui.SetTooltip("사용법"),
        });
    }

    private Configuration Config => this.plugin.Config;

    public override void Draw()
    {
        this.RefreshView();

        this.DrawTopBar();

        // 아래쪽 줄(선택 바 + 하단 바)을 뺀 나머지를 표가 차지해, 하단 바가 항상 바닥에 붙어 있게 한다
        var rows = this.selected.Count > 0 ? 2 : 1;
        var footer = ImGui.GetFrameHeightWithSpacing() * rows + ImGui.GetStyle().ItemSpacing.Y;
        var tableHeight = Math.Max(ImGui.GetContentRegionAvail().Y - footer, ImGui.GetFrameHeight() * 3);
        this.DrawTable(tableHeight);

        if (this.selected.Count > 0) this.DrawSelectionBar();
        this.DrawFooter();
    }

    // ---- 데이터 ----

    private void RefreshView()
    {
        var key = (this.plugin.PurchaseLog.Version, this.range, this.labelFilter, this.search, this.sort, DateTime.Today);
        if (key == this.viewKey) return;
        this.viewKey = key;

        var since = this.range switch
        {
            0 => DateTime.Today,
            1 => DateTime.Now.AddDays(-7),
            2 => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        var rows = this.plugin.PurchaseLog.Snapshot()
            .Where(r => r.Time >= since)
            .Where(r => this.labelFilter == null || r.Label == this.labelFilter)
            .Where(r => this.search.Length == 0 || r.ItemName.Contains(this.search, StringComparison.OrdinalIgnoreCase));

        // 라벨별 정렬은 '라벨 관리'의 순서를 따르고, 라벨 없는 기록은 맨 뒤로 보낸다
        int LabelOrder(PurchaseRecord r)
        {
            var i = this.Config.Labels.IndexOf(r.Label);
            return i < 0 ? int.MaxValue : i;
        }

        this.view = (this.sort switch
        {
            1 => rows.OrderBy(r => r.Time),
            2 => rows.OrderByDescending(r => r.Total).ThenByDescending(r => r.Time),
            3 => rows.OrderBy(r => r.ItemName, StringComparer.CurrentCulture).ThenByDescending(r => r.Time),
            4 => rows.OrderBy(r => this.CategoryOf(r.ItemId), StringComparer.CurrentCulture)
                     .ThenBy(r => r.ItemName, StringComparer.CurrentCulture).ThenByDescending(r => r.Time),
            5 => rows.OrderBy(LabelOrder).ThenByDescending(r => r.Time),
            _ => rows.OrderByDescending(r => r.Time),
        }).ToList();
        this.viewSum = this.view.Sum(r => r.Total);

        this.selected.IntersectWith(this.view.Select(r => r.Id).ToHashSet());
    }

    private string CategoryOf(uint itemId)
    {
        if (this.categories.TryGetValue(itemId, out var name)) return name;
        name = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?
            .ItemUICategory.ValueNullable?.Name.ExtractText() ?? "";
        this.categories[itemId] = name;
        return name;
    }

    private uint IconOf(uint itemId)
    {
        if (this.iconIds.TryGetValue(itemId, out var icon)) return icon;
        icon = (uint)(Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0);
        this.iconIds[itemId] = icon;
        return icon;
    }

    // ---- 위쪽: 보는 조건 ----

    private void DrawTopBar()
    {
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.SetNextItemWidth(100 * scale);
        ImGui.Combo("##range", ref this.range, Ranges);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * scale);
        if (ImGui.BeginCombo("##sort", $"정렬: {Sorts[this.sort]}"))
        {
            for (var i = 0; i < Sorts.Length; i++)
                if (ImGui.Selectable(Sorts[i], this.sort == i)) this.sort = i;
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered() && this.sort == 4)
            ImGui.SetTooltip("무기, 가구, 재료 같은 게임 아이템 분류 순으로 묶어 보여줍니다.");

        var autoOn = this.Config.AutoLabelEnabled && this.Config.Labels.Contains(this.Config.AutoLabel);
        var autoText = autoOn ? $"자동 라벨링: {this.Config.AutoLabel}" : "자동 라벨링: OFF";
        var autoWidth = ImGui.CalcTextSize(autoText).X + ImGui.GetStyle().FramePadding.X * 2;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(80 * scale, ImGui.GetContentRegionAvail().X - autoWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##search", "아이템 검색", ref this.search, 64);

        ImGui.SameLine();
        if (autoOn)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, AutoOnBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AutoOnHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AutoOnHover);
        }

        if (ImGui.Button(autoText)) ImGui.OpenPopup("autoMenu");
        if (autoOn) ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("이후 구매부터 선택한 라벨로 자동 분류됩니다.");

        if (ImGui.BeginPopup("autoMenu"))
        {
            if (ImGui.Selectable("OFF", !autoOn))
            {
                this.Config.AutoLabelEnabled = false;
                this.Config.Save();
            }

            ImGui.Separator();
            if (this.Config.Labels.Count == 0)
                this.MutedText("라벨이 없습니다. '라벨 관리'에서 먼저 만드세요.");

            foreach (var l in this.Config.Labels)
            {
                if (!this.TagSelectable($"##auto_{l}", l, autoOn && this.Config.AutoLabel == l)) continue;
                this.Config.AutoLabel = l;
                this.Config.AutoLabelEnabled = true;
                this.Config.Save();
            }

            ImGui.EndPopup();
        }
    }

    // ---- 가운데: 표 ----

    private void DrawTable(float height)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                      ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##purchases", 7, flags, new Vector2(0, height))) return;

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("아이템", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("일시", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00-00 00:00").X);
        ImGui.TableSetupColumn("라벨", ImGuiTableColumnFlags.WidthFixed, 120 * scale);
        ImGui.TableSetupColumn("수량", ImGuiTableColumnFlags.WidthFixed, 40 * scale);
        ImGui.TableSetupColumn("단가", ImGuiTableColumnFlags.WidthFixed, 72 * scale);
        ImGui.TableSetupColumn("총액", ImGuiTableColumnFlags.WidthFixed, 80 * scale);

        // 머리줄: 첫 칸은 '모두 선택' 체크박스
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        var allSelected = this.view.Count > 0 && this.view.All(r => this.selected.Contains(r.Id));
        if (ImGui.Checkbox("##all", ref allSelected))
        {
            if (allSelected) foreach (var r in this.view) this.selected.Add(r.Id);
            else this.selected.Clear();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("지금 보이는 기록 모두 선택");
        string[] headers = { "아이템", "일시", "라벨", "수량", "단가", "총액" };
        for (var i = 0; i < headers.Length; i++)
        {
            ImGui.TableSetColumnIndex(i + 1);
            if (i == 2) this.DrawLabelHeader();
            else ImGui.TableHeader(headers[i]);
        }

        if (this.view.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);
            this.MutedText("조건에 맞는 기록이 없습니다.");
        }

        var rowHeight = ImGui.GetFrameHeight();
        var iconSize = new Vector2(rowHeight - 2 * scale);

        // 보이는 줄만 그려서 기록이 많아도 가볍게 유지한다
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(this.view.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var r = this.view[i];
                var isSelected = this.selected.Contains(r.Id);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

                // 줄 전체를 덮는 투명 선택 영역. 그 위에 체크박스를 다시 그려 둘 다 눌리게 한다.
                ImGui.TableNextColumn();
                var start = ImGui.GetCursorPos();
                if (ImGui.Selectable($"##row{r.Id}", isSelected,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                        new Vector2(0, rowHeight)))
                {
                    isSelected = !isSelected;
                    this.Toggle(r.Id, isSelected);
                }

                ImGui.SetCursorPos(start);
                if (ImGui.Checkbox($"##sel{r.Id}", ref isSelected)) this.Toggle(r.Id, isSelected);

                ImGui.TableNextColumn();
                var icon = this.IconOf(r.ItemId);
                if (icon != 0)
                {
                    var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon, r.IsHq)).GetWrapOrEmpty();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + scale);
                    ImGui.Image(tex.Handle, iconSize);
                    ImGui.SameLine();
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(r.IsHq ? r.ItemName + " (HQ)" : r.ItemName);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{r.ItemName}{(r.IsHq ? " (HQ)" : "")}\n분류: {this.CategoryOf(r.ItemId)}\n서버: {r.World}\n세금: {r.Tax:N0}길");

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(r.Time.ToString("MM-dd HH:mm"));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (r.Label.Length > 0) this.DrawTag(r.Label);
                else this.MutedText("없음");

                ImGui.TableNextColumn(); this.RightText(r.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); this.RightText(r.UnitPrice.ToString("N0"));
                ImGui.TableNextColumn(); this.RightText(r.Total.ToString("N0"));
            }
        }

        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    // 라벨 칸 머리: 누르면 라벨로 거르는 목록이 열리고, 걸러진 상태면 색과 이름으로 표시한다
    private void DrawLabelHeader()
    {
        var active = this.labelFilter != null;
        var title = this.labelFilter switch { null => "라벨", "" => "라벨: 없음", _ => $"라벨: {this.labelFilter}" };

        if (active) ImGui.PushStyleColor(ImGuiCol.Text, AccentHover);
        ImGui.TableHeader($"{title}##labelHeader");
        if (active) ImGui.PopStyleColor();

        var clicked = ImGui.IsItemClicked();

        // 칸 오른쪽 끝에 필터 아이콘
        var max = ImGui.GetItemRectMax();
        var min = ImGui.GetItemRectMin();
        ImGui.PushFont(UiBuilder.IconFont);
        var icon = FontAwesomeIcon.Filter.ToIconString();
        var iconSize = ImGui.CalcTextSize(icon);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(max.X - iconSize.X - 4 * ImGuiHelpers.GlobalScale, min.Y + ((max.Y - min.Y - iconSize.Y) / 2)),
            ImGui.GetColorU32(active ? AccentHover : Muted), icon);
        ImGui.PopFont();

        if (clicked) ImGui.OpenPopup("labelFilterPopup");
        if (ImGui.BeginPopup("labelFilterPopup"))
        {
            if (ImGui.Selectable("모든 라벨", this.labelFilter == null)) this.labelFilter = null;
            if (ImGui.Selectable("없음", this.labelFilter == "")) this.labelFilter = "";
            if (this.Config.Labels.Count > 0) ImGui.Separator();
            foreach (var l in this.Config.Labels)
                if (this.TagSelectable($"##lf_{l}", l, this.labelFilter == l)) this.labelFilter = l;
            ImGui.EndPopup();
        }
    }

    private void Toggle(long id, bool on)
    {
        if (on) this.selected.Add(id);
        else this.selected.Remove(id);
    }

    // ---- 아래쪽: 선택 바 (선택했을 때만) ----

    private void DrawSelectionBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + ImGui.GetContentRegionAvail().X, min.Y + ImGui.GetFrameHeight());
        ImGui.GetWindowDrawList().AddRectFilled(min, max, ImGui.GetColorU32(SelectionBar), 4 * scale);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($" {this.selected.Count:N0}건 선택됨");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * scale);
        var preview = this.assignLabel switch { null => "라벨 선택", "" => "라벨 제거", _ => this.assignLabel };
        if (ImGui.BeginCombo("##assign", preview))
        {
            foreach (var l in this.Config.Labels)
            {
                if (this.TagSelectable($"##as_{l}", l, this.assignLabel == l))
                {
                    this.assignLabel = l;
                    this.assignError = false;
                }
            }

            if (this.Config.Labels.Count > 0) ImGui.Separator();
            if (ImGui.Selectable("라벨 제거", this.assignLabel == ""))
            {
                this.assignLabel = "";
                this.assignError = false;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, AccentBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentHover);
        var apply = ImGui.Button("변경");
        ImGui.PopStyleColor(3);
        if (apply)
        {
            if (this.assignLabel == null)
            {
                this.assignError = true;
            }
            else
            {
                this.plugin.PurchaseLog.SetLabel(this.selected.ToList(), this.assignLabel);
                this.selected.Clear();
                this.assignLabel = null;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("선택 해제"))
        {
            this.selected.Clear();
            this.assignError = false;
        }

        ImGui.SameLine();
        var exportText = this.exporting ? "내보내는 중…" : "시트로 내보내기";
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileExport, exportText) && !this.exporting)
        {
            if (SheetExporter.Validate(this.Config.SheetUrl) != null)
            {
                this.OpenSheetConnect("먼저 스프레드시트를 연결하세요.", true);
            }
            else
            {
                this.StartExport();
            }
        }

        if (this.assignError)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Danger);
            ImGui.TextUnformatted("라벨을 먼저 고르세요.");
            ImGui.PopStyleColor();
        }
    }

    private void StartExport()
    {
        var rows = this.view
            .Where(r => this.selected.Contains(r.Id))
            .OrderBy(r => r.Time)
            .Select(r => SheetExporter.ToRow(r, this.CategoryOf(r.ItemId)))
            .ToList();
        if (rows.Count == 0) return;

        var url = this.Config.SheetUrl;
        this.exporting = true;
        Task.Run(async () =>
        {
            var res = await SheetExporter.SendAsync(url, rows);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                this.exporting = false;
                if (res.Ok)
                {
                    var skipped = res.Skipped > 0 ? $" 이미 있던 {res.Skipped:N0}건은 건너뛰었습니다." : "";
                    Plugin.Chat.Print($"[구매 기록] 스프레드시트로 {res.Added:N0}건 내보냈습니다.{skipped}");
                }
                else
                {
                    Plugin.Chat.PrintError($"[구매 기록] 스프레드시트 내보내기 실패: {res.Error}");
                }
            });
        });
    }

    private void OpenSheetConnect(string message, bool isError)
    {
        this.sheetUrlBuffer = this.Config.SheetUrl;
        this.sheetMessage = message;
        this.sheetMessageIsError = isError;
        ImGui.OpenPopup("sheetConnect");
    }

    // ---- 맨 아래: 라벨 관리, CSV, 시트 연결, 합계 (항상 고정) ----

    private void DrawFooter()
    {
        var openManager = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cog, "라벨 관리");
        var managerAnchor = ImGui.GetItemRectMin();
        if (openManager)
        {
            this.labelError = "";
            this.pendingDelete = null;
            ImGui.OpenPopup("labelManager");
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, "CSV 폴더 열기"))
        {
            var dir = System.IO.Path.GetDirectoryName(this.plugin.PurchaseLog.FilePath)!;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        ImGui.SameLine();
        var connected = SheetExporter.Validate(this.Config.SheetUrl) == null;
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Link, connected ? "시트 연결됨" : "시트 연결"))
            this.OpenSheetConnect("", false);
        this.sheetAnchor = ImGui.GetItemRectMin();

        // 선택한 게 있으면 선택한 기록의 합계, 없으면 지금 걸러진 전체 합계
        var hasSelection = this.selected.Count > 0;
        var sum = hasSelection
            ? $"선택 {this.selected.Count:N0}건 · 합계 {this.view.Where(r => this.selected.Contains(r.Id)).Sum(r => r.Total):N0}길"
            : $"{this.view.Count:N0}건 · 합계 {this.viewSum:N0}길";
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(sum).X));
        ImGui.AlignTextToFramePadding();
        if (hasSelection)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, AccentHover);
            ImGui.TextUnformatted(sum);
            ImGui.PopStyleColor();
        }
        else
        {
            this.MutedText(sum);
        }

        // 라벨 관리는 버튼 위쪽으로 열린다
        if (ImGui.IsPopupOpen("labelManager"))
            ImGui.SetNextWindowPos(managerAnchor - new Vector2(0, ImGui.GetStyle().ItemSpacing.Y), ImGuiCond.Always, new Vector2(0, 1));
        if (ImGui.BeginPopup("labelManager"))
        {
            this.DrawLabelManager();
            ImGui.EndPopup();
        }

        if (ImGui.IsPopupOpen("sheetConnect"))
            ImGui.SetNextWindowPos(this.sheetAnchor - new Vector2(0, ImGui.GetStyle().ItemSpacing.Y), ImGuiCond.Always, new Vector2(0, 1));
        if (ImGui.BeginPopup("sheetConnect"))
        {
            this.DrawSheetConnect();
            ImGui.EndPopup();
        }
    }

    private void DrawSheetConnect()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted("스프레드시트 연결");
        ImGui.Separator();

        ImGui.PushTextWrapPos(400 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.80f, 0.80f, 0.80f, 1f));
        ImGui.TextWrapped("구글 시트에 붙인 Apps Script의 웹 앱 주소를 넣으면, 선택한 기록을 그 시트로 내보낼 수 있습니다. 이미 보낸 기록은 다시 보내도 중복으로 쌓이지 않습니다.");
        ImGui.Spacing();
        ImGui.TextWrapped("1. 기록을 받을 구글 시트를 열고 메뉴의 확장 프로그램 → Apps Script를 엽니다.");
        ImGui.TextWrapped("2. 아래 '스크립트 복사'를 눌러 편집기 내용을 지우고 붙여넣은 뒤 저장합니다.");
        ImGui.TextWrapped("3. 배포 → 새 배포 → 유형 '웹 앱'을 고르고, 실행 사용자 '나', 액세스 권한 '모든 사용자'로 배포합니다. 처음 한 번은 구글 계정 권한 허용이 필요합니다.");
        ImGui.TextWrapped("4. 배포가 끝나면 나오는 웹 앱 URL(…/exec)을 아래 칸에 붙여넣고 저장합니다.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "스크립트 복사"))
        {
            ImGui.SetClipboardText(SheetExporter.AppsScript);
            this.sheetMessage = "스크립트를 복사했습니다. Apps Script 편집기에 붙여넣으세요.";
            this.sheetMessageIsError = false;
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(400 * scale);
        var enter = ImGui.InputTextWithHint("##sheetUrl", "https://script.google.com/macros/s/…/exec", ref this.sheetUrlBuffer, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemEdited()) this.sheetMessage = "";

        var save = ImGui.Button("저장");
        if (enter || save)
        {
            var problem = SheetExporter.Validate(this.sheetUrlBuffer);
            if (problem != null)
            {
                this.sheetMessage = problem;
                this.sheetMessageIsError = true;
            }
            else
            {
                this.Config.SheetUrl = this.sheetUrlBuffer.Trim();
                this.Config.Save();
                this.sheetMessage = "저장했습니다. 기록을 선택하고 '시트로 내보내기'를 누르면 됩니다.";
                this.sheetMessageIsError = false;
            }
        }

        if (this.Config.SheetUrl.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("연결 해제"))
            {
                this.Config.SheetUrl = "";
                this.Config.Save();
                this.sheetUrlBuffer = "";
                this.sheetMessage = "연결을 해제했습니다.";
                this.sheetMessageIsError = false;
            }
        }

        if (this.sheetMessage.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.sheetMessageIsError ? Danger : AccentHover);
            ImGui.TextWrapped(this.sheetMessage);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        this.MutedText("이 주소를 아는 사람은 누구나 시트에 줄을 추가할 수 있으니 공유하지 마세요.");
        ImGui.PopTextWrapPos();
    }

    private void DrawLabelManager()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted("라벨 관리");
        ImGui.Separator();

        string? toDelete = null;
        (string From, string To)? toRename = null;
        string? toRecolor = null;

        for (var i = 0; i < this.Config.Labels.Count; i++)
        {
            var l = this.Config.Labels[i];
            ImGui.PushID(i);

            // 색 견본. 누르면 다음 색으로 바뀐다.
            var c = this.plugin.ColorOf(l);
            ImGui.PushStyleColor(ImGuiCol.Button, LabelColors.Text(c));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, LabelColors.Text(c));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, LabelColors.Text(c));
            if (ImGui.Button("##color", new Vector2(ImGui.GetFrameHeight()))) toRecolor = l;
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("눌러서 색 바꾸기");

            ImGui.SameLine();
            if (!this.renameBuffers.TryGetValue(l, out var buf)) buf = l;
            ImGui.SetNextItemWidth(180 * scale);
            var enter = ImGui.InputText("##name", ref buf, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            this.renameBuffers[l] = buf;
            if ((enter || ImGui.IsItemDeactivatedAfterEdit()) && buf.Trim() != l) toRename = (l, buf);

            ImGui.SameLine();
            if (this.pendingDelete == l)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
                if (ImGui.Button("삭제 확인")) toDelete = l;
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("이 라벨이 붙은 기록은 '라벨 없음'이 됩니다.");
            }
            else if (ImGuiComponents.IconButton(i, FontAwesomeIcon.Trash))
            {
                this.pendingDelete = l;
            }

            ImGui.PopID();
        }

        // 목록을 순회하는 동안 바꾸지 않도록 루프 밖에서 반영한다
        if (toRecolor != null) this.plugin.CycleLabelColor(toRecolor);

        if (toRename is { } rn)
        {
            this.renameBuffers.Remove(rn.From);
            if (this.plugin.RenameLabel(rn.From, rn.To))
            {
                var to = rn.To.Trim();
                if (this.labelFilter == rn.From) this.labelFilter = to;
                if (this.assignLabel == rn.From) this.assignLabel = to;
                this.labelError = "";
            }
            else
            {
                this.labelError = "이름이 비었거나 이미 있는 라벨입니다.";
            }
        }

        if (toDelete != null)
        {
            this.renameBuffers.Remove(toDelete);
            this.plugin.DeleteLabel(toDelete);
            if (this.labelFilter == toDelete) this.labelFilter = null;
            if (this.assignLabel == toDelete) this.assignLabel = null;
            this.pendingDelete = null;
        }

        if (this.Config.Labels.Count > 0) ImGui.Spacing();

        ImGui.SetNextItemWidth((180 * scale) + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        var add = ImGui.InputTextWithHint("##newLabel", "부대 재료비", ref this.newLabel, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemEdited()) this.labelError = "";
        ImGui.SameLine();
        var addClicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "추가");
        if (add || addClicked)
        {
            if (this.newLabel.Trim().Length == 0) this.labelError = "라벨 이름을 입력하세요.";
            else if (this.plugin.AddLabel(this.newLabel)) { this.newLabel = ""; this.labelError = ""; }
            else this.labelError = "이미 있는 라벨입니다.";
        }

        if (this.labelError.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Danger);
            ImGui.TextUnformatted(this.labelError);
            ImGui.PopStyleColor();
        }
    }

    // ---- 그리기 도우미 ----

    // 목록용: 줄 전체가 눌리는 선택 영역 위에 라벨 태그를 겹쳐 그린다
    private bool TagSelectable(string id, string label, bool selected)
    {
        var start = ImGui.GetCursorPos();
        var height = ImGui.GetTextLineHeight() + 2 * ImGuiHelpers.GlobalScale;
        var clicked = ImGui.Selectable(id, selected, ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, height));
        var after = ImGui.GetCursorPos();
        ImGui.SetCursorPos(start);
        this.DrawTag(label);
        ImGui.SetCursorPos(after);
        return clicked;
    }

    private void DrawTag(string label)
    {
        var c = this.plugin.ColorOf(label);
        var pad = new Vector2(6, 1) * ImGuiHelpers.GlobalScale;
        var size = ImGui.CalcTextSize(label) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(LabelColors.Background(c)), 4 * ImGuiHelpers.GlobalScale);
        dl.AddText(pos + pad, ImGui.GetColorU32(LabelColors.Text(c)), label);
        ImGui.Dummy(size);
    }

    private void MutedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private void RightText(string text)
    {
        ImGui.AlignTextToFramePadding();
        var w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - w));
        ImGui.TextUnformatted(text);
    }
}
