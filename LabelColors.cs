using System.Numerics;

namespace MarketReceipt;

/// <summary>라벨 태그에 쓰는 색. 게임의 어두운 창 위에서 읽히도록 밝은 글자 + 옅은 배경 조합.</summary>
internal static class LabelColors
{
    public static readonly Vector4[] Palette =
    {
        new(0.66f, 0.58f, 1.00f, 1f), // 보라
        new(0.40f, 0.85f, 0.85f, 1f), // 청록
        new(0.95f, 0.50f, 0.75f, 1f), // 분홍
        new(1.00f, 0.66f, 0.38f, 1f), // 주황
        new(0.55f, 0.85f, 0.45f, 1f), // 초록
        new(0.50f, 0.70f, 1.00f, 1f), // 파랑
    };

    public static Vector4 Text(int index) => Palette[Wrap(index)];

    public static Vector4 Background(int index)
    {
        var c = Palette[Wrap(index)];
        return new Vector4(c.X, c.Y, c.Z, 0.22f);
    }

    public static int Wrap(int index) => ((index % Palette.Length) + Palette.Length) % Palette.Length;
}
