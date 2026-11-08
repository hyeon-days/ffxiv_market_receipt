using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace MarketReceipt;

public sealed class HelpWindow : Window
{
    private static readonly (string Title, string Body)[] Sections =
    {
        ("플러그인 소개",
            "장터(마켓보드)에서 구매한 아이템을 자동으로 기록합니다. NPC 상점 구매, 하우징 가구 상점, 재화 교환 등은 기록되지 않습니다."),
        ("라벨링",
            "기록에 '부대 공금' 같은 라벨을 붙여 용도별로 묶는 기능입니다. 위쪽 라벨 필터로 걸러 보거나, CSV에서 라벨별로 따로 모아볼 수 있습니다."),
        ("라벨링 하는 법",
            "표에서 기록을 눌러 선택한 뒤, 아래쪽에서 라벨을 고르고 '변경'을 누릅니다. 표 맨 위 체크박스로 지금 보이는 기록을 한 번에 선택할 수 있습니다."),
        ("자동 라벨링",
            "켜두면 이후 장터에서 사는 아이템에 고른 라벨이 자동으로 붙습니다. 장터에서 가구를 대량으로 살 때처럼 같은 목적의 구매가 이어질 때 켜고, 끝나면 꺼두세요."),
        ("라벨 관리",
            "라벨 추가, 이름 변경, 색 변경, 삭제는 왼쪽 아래 '라벨 관리'에서 할 수 있습니다."),
    };

    public HelpWindow() : base("사용법###MarketReceiptHelp", ImGuiWindowFlags.NoCollapse)
    {
        this.Size = new Vector2(380, 0);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.Flags |= ImGuiWindowFlags.AlwaysAutoResize;
    }

    public override void Draw()
    {
        ImGui.PushTextWrapPos(360 * ImGuiHelpers.GlobalScale);
        var body = new Vector4(0.80f, 0.80f, 0.80f, 1f);

        for (var i = 0; i < Sections.Length; i++)
        {
            if (i > 0) ImGui.Spacing();
            ImGui.TextUnformatted(Sections[i].Title);
            ImGui.PushStyleColor(ImGuiCol.Text, body);
            ImGui.TextWrapped(Sections[i].Body);
            ImGui.PopStyleColor();
        }

        ImGui.PopTextWrapPos();
    }
}
