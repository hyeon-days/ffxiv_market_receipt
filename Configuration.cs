using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MarketReceipt;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>사용자가 만든 라벨 이름 목록. 표시 순서도 이 순서를 따른다.</summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>라벨 이름별 색 번호(LabelColors.Palette 인덱스). 0.2.0 설정과 호환되도록 따로 둔다.</summary>
    public Dictionary<string, int> LabelColorIndex { get; set; } = new();

    /// <summary>켜져 있으면 새 구매에 AutoLabel을 자동으로 붙인다.</summary>
    public bool AutoLabelEnabled { get; set; }

    public string AutoLabel { get; set; } = "";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
