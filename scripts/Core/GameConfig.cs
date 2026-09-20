using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 全局常量。所有"魔法数字"集中在这里，方便一处调参。
/// </summary>
public static class GameConfig
{
	/// <summary>存档 schema 版本。读档时版本不匹配会走迁移或拒绝加载。</summary>
	public const string SchemaVersion = "1";

	// ---- 物件默认尺寸（扑克牌比例 1 : 1.4）----
	public static readonly Vector2 DefaultCardSize = new(300f, 420f);
	public const float DefaultTokenDiameter = 160f;
	public static readonly Vector2 DefaultDiceSize = new(120f, 120f);

	// ---- 桌面默认尺寸（3200×2000 ≈ 10 张卡宽 × 4.7 张卡高）----
	public static readonly Vector2 DefaultBoardSize = new(3200f, 2000f);
	public const int DefaultGridSize = 100;

	// ---- 相机 ----
	public const float MinZoom = 0.12f;
	public const float MaxZoom = 4.0f;
	/// <summary>每一格滚轮的缩放倍率。</summary>
	public const float ZoomStep = 1.12f;
	public const float ZoomSmoothSpeed = 18.0f;
	public const float PanSmoothSpeed = 22.0f;

	// ---- 历史记录 ----
	public const int UndoStackLimit = 300;

	// ---- 交互阈值 ----
	/// <summary>鼠标按下后移动超过这个像素数才算"拖拽"，否则算"点击"。</summary>
	public const float DragThresholdPixels = 5.0f;

	// ---- 物件外观 ----
	/// <summary>悬停描边色。</summary>
	public static readonly Color HoverOutline = new("#88c0d0");
	/// <summary>选中描边色。</summary>
	public static readonly Color SelectionOutline = new("#ebcb8b");

	/// <summary>堆叠数量徽章的填充 / 描边 / 文字色。</summary>
	public static readonly Color PileBadgeFill = new("#2e3440");
	public static readonly Color PileBadgeBorder = new("#ebcb8b");
	public static readonly Color PileBadgeText = new("#ebcb8b");

	/// <summary>堆里每张卡的视觉阶梯偏移（世界单位）。</summary>
	public static readonly Vector2 PileStepOffset = new(2f, -2f);
	/// <summary>阶梯偏移的最大层数 —— 一叠 60 张牌不可能每张偏 2px，否则会拖出一条长尾。</summary>
	public const int PileStepLimit = 6;
	/// <summary>一堆里最多完整绘制几张，更下面的不画（性能 + 视觉都更干净）。</summary>
	public const int PileVisibleDepth = 3;

	// ---- 骰子外观 ----
	public static readonly Color DiceBorderColor = new("#2e3440");
	public const float DiceBorderWidth = 3f;
	public const float DiceCornerRadius = 16f;
	/// <summary>单骰时的大号点数字号。</summary>
	public const int DiceNumberFontSize = 64;
	/// <summary>多骰时上行「3 + 5 + 2」的字号。</summary>
	public const int DiceSumFontSize = 30;
	/// <summary>多骰时下行「= 10」的字号。</summary>
	public const int DiceTotalFontSize = 46;

	// ---- 旋转 ----
	/// <summary>`[` / `]` 每次步进的角度。15° 能摆正，也能做出 45°/30° 的斜置。</summary>
	public const float KeyRotateStepDegrees = 15f;
	/// <summary>Alt+滚轮每格旋转的角度。</summary>
	public const float WheelRotateStepDegrees = 3f;
}
