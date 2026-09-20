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
}
