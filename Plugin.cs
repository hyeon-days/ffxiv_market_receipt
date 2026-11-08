using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketReceipt;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mreceipt";

    // 구매 요청과 구매 완료 사이의 최대 허용 간격
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(30);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private sealed record Pending(DateTime At, string World, IMarketBoardPurchaseHandler Req);

    private readonly object gate = new();
    private readonly List<Pending> pending = new();

    private readonly WindowSystem windows = new("MarketReceipt");
    private readonly MainWindow mainWindow;
    internal HelpWindow HelpWindow { get; }

    internal PurchaseLog PurchaseLog { get; }
    internal Configuration Config { get; }

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.PurchaseLog = new PurchaseLog(PluginInterface.ConfigDirectory.FullName);

        // CSV를 직접 고쳐 새 라벨을 넣었을 경우에도 목록에 나타나게 맞춘다
        var missing = this.PurchaseLog.UsedLabels().Except(this.Config.Labels).ToList();
        this.Config.Labels.AddRange(missing);
        foreach (var l in this.Config.Labels)
            this.Config.LabelColorIndex.TryAdd(l, this.NextColorIndex());
        this.Config.Save();

        this.HelpWindow = new HelpWindow();
        this.mainWindow = new MainWindow(this);
        this.windows.AddWindow(this.mainWindow);
        this.windows.AddWindow(this.HelpWindow);

        MarketBoard.PurchaseRequested += this.OnPurchaseRequested;
        MarketBoard.ItemPurchased += this.OnItemPurchased;

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => this.mainWindow.Toggle())
        {
            HelpMessage = "장터 구매 기록 창을 엽니다.",
        });

        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.mainWindow.Toggle;
    }

    public void Dispose()
    {
        MarketBoard.PurchaseRequested -= this.OnPurchaseRequested;
        MarketBoard.ItemPurchased -= this.OnItemPurchased;
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.mainWindow.Toggle;
        this.windows.RemoveAllWindows();
    }

    // ---- 라벨 관리 ----

    /// <returns>추가에 성공하면 true. 빈 이름이나 중복이면 false.</returns>
    internal bool AddLabel(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || this.Config.Labels.Contains(name)) return false;
        this.Config.Labels.Add(name);
        this.Config.LabelColorIndex[name] = this.NextColorIndex();
        this.Config.Save();
        return true;
    }

    internal int ColorOf(string label) =>
        this.Config.LabelColorIndex.TryGetValue(label, out var c) ? c : 0;

    internal void CycleLabelColor(string label)
    {
        this.Config.LabelColorIndex[label] = LabelColors.Wrap(this.ColorOf(label) + 1);
        this.Config.Save();
    }

    // 지금 가장 적게 쓰인 색을 고른다
    private int NextColorIndex() =>
        Enumerable.Range(0, LabelColors.Palette.Length)
            .OrderBy(i => this.Config.LabelColorIndex.Values.Count(v => v == i))
            .First();

    internal bool RenameLabel(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || to == from || this.Config.Labels.Contains(to)) return false;
        var i = this.Config.Labels.IndexOf(from);
        if (i < 0) return false;

        this.Config.Labels[i] = to;
        this.Config.LabelColorIndex[to] = this.ColorOf(from);
        this.Config.LabelColorIndex.Remove(from);
        if (this.Config.AutoLabel == from) this.Config.AutoLabel = to;
        this.Config.Save();
        this.PurchaseLog.RenameLabel(from, to);
        return true;
    }

    internal void DeleteLabel(string name)
    {
        if (!this.Config.Labels.Remove(name)) return;
        this.Config.LabelColorIndex.Remove(name);
        if (this.Config.AutoLabel == name)
        {
            this.Config.AutoLabel = "";
            this.Config.AutoLabelEnabled = false;
        }

        this.Config.Save();
        this.PurchaseLog.ClearLabel(name);
    }

    private string CurrentAutoLabel =>
        this.Config.AutoLabelEnabled && this.Config.Labels.Contains(this.Config.AutoLabel)
            ? this.Config.AutoLabel
            : "";

    // ---- 구매 감지 ----

    // 1단계: 구매 버튼을 누른 시점. 가격 정보는 여기에만 있다.
    private void OnPurchaseRequested(IMarketBoardPurchaseHandler req)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            var world = ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
            lock (this.gate)
            {
                this.pending.RemoveAll(p => DateTime.Now - p.At > MatchWindow);
                this.pending.Add(new Pending(DateTime.Now, world, req));
            }
        });
    }

    // 2단계: 서버가 구매 성공을 알려온 시점. 요청과 짝지어 기록한다.
    private void OnItemPurchased(IMarketBoardPurchase done)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            Pending? match;
            lock (this.gate)
            {
                match = this.pending.Find(p =>
                    p.Req.CatalogId == done.CatalogId &&
                    p.Req.ItemQuantity == done.ItemQuantity &&
                    DateTime.Now - p.At <= MatchWindow);
                if (match != null) this.pending.Remove(match);
            }

            if (match == null)
            {
                Log.Warning($"짝이 맞는 구매 요청 없음: item={done.CatalogId} qty={done.ItemQuantity}");
                return;
            }

            var req = match.Req;
            var name = DataManager.GetExcelSheet<Item>().GetRowOrDefault(req.CatalogId)?.Name.ExtractText()
                       ?? $"#{req.CatalogId}";
            var label = this.CurrentAutoLabel;

            try
            {
                var r = this.PurchaseLog.Add(DateTime.Now, label, match.World, req.CatalogId, name, req.IsHq,
                    req.ItemQuantity, req.PricePerUnit, req.TotalTax);
                var tag = label.Length > 0 ? $" [{label}]" : "";
                Chat.Print($"[구매 기록]{tag} {name}{(req.IsHq ? " HQ" : "")} x{req.ItemQuantity}, {r.Total:N0}길");
            }
            catch (Exception e)
            {
                Log.Error(e, "구매 기록 저장 실패");
            }
        });
    }
}
