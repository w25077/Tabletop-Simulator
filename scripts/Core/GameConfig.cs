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

	/// <summary>
	/// 双击的时间窗口（秒）。两次"点击"（按下→松开且未越过拖拽阈值）间隔小于它、且落点几乎不动，
	/// 才算一次双击。
	///
	/// <b>为什么自己判定而不用 <c>InputEventMouseButton.DoubleClick</c></b>：
	/// 那个标志由 DisplayServer 层填写，而自检是把合成事件直接塞进
	/// <c>Input.ParseInputEvent</c> 的，根本走不到那一层。用它的话，
	/// 双击抽牌在真机上能用、在自检里永远触发不了 —— 于是这条最关键的新交互
	/// 就成了"自检覆盖不到的盲区"。自己判定的代价只是十余行代码，换来的是
	/// 合成事件与真实鼠标走完全相同的代码路径。
	/// </summary>
	public const double DoubleClickSeconds = 0.35;

	/// <summary>双击判定允许的落点漂移（屏幕像素）。超过它就算两次独立的点击。</summary>
	public const float DoubleClickMaxDriftPixels = 8.0f;

	// ---- 物件外观 ----
	/// <summary>悬停描边色（仅在没有更明确的操作目标时使用）。</summary>
	public static readonly Color HoverOutline = new("#88c0d0");

	/// <summary>
	/// 操作目标描边色 —— 下一次键盘操作会作用到的物件。
	///
	/// 这是唯一的"会被改"信号：亮黄 = 按 F / [ / Del 会作用到它；
	/// 暗黄（<see cref="PassiveSelectionOutline"/>）= 在选中集里但不是当前目标。
	/// 之所以不再用颜色区分"悬停"与"选中"，是因为在"悬停即为选中"的规则下，
	/// 那样会出现「选中 A、悬停 B，两个都是描边，用户以为 F 会改 A」的误导。
	/// </summary>
	public static readonly Color ActionTargetOutline = new("#ebcb8b");

	/// <summary>选中但不是当前操作目标时的描边色（暗黄）。</summary>
	public static readonly Color PassiveSelectionOutline = new(0.92f, 0.80f, 0.55f, 0.35f);

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

	// ---- 区域（M3）----
	/// <summary>区域边框粗细。</summary>
	public const float ZoneBorderWidth = 2f;

	/// <summary>空区域 / 锁定区域的边框透明度。比正常边框淡，一眼看出"这里还没东西"。</summary>
	public const float ZoneIdleBorderAlpha = 0.28f;

	/// <summary>区域名称与张数的字号。</summary>
	public const int ZoneTitleFontSize = 24;

	/// <summary>区域内容的内边距（世界单位）—— 排版时不能贴着边框。</summary>
	public const float ZonePadding = 26f;

	/// <summary>
	/// 横排（<c>ZoneSortMode.Row</c>）时相邻两张卡的水平间距。
	/// 12 是按"手牌区要装下 10 张 300 宽的卡 + 两侧内边距"反推的上限（见 <c>ZoneDefaults.HandZoneSize</c>），
	/// 再大就会撑出区域。
	/// </summary>
	public const float ZoneRowGap = 12f;

	/// <summary>横排时相邻两行的垂直间距。</summary>
	public const float ZoneRowLineGap = 20f;

	/// <summary>扇形（<c>ZoneSortMode.Fan</c>）时相邻两张的水平间距。比横排窄，才有叠压感。</summary>
	public const float ZoneFanGap = 90f;

	/// <summary>扇形最边缘那张的旋转角（度）。中间那张为 0°，向两侧对称张开。</summary>
	public const float ZoneFanMaxDegrees = 9f;

	/// <summary>容量已满 / 锁定时，区域边框改用的警示色。</summary>
	public static readonly Color ZoneBlockedBorder = new("#bf616a");

	/// <summary>区域名称文字色。</summary>
	public static readonly Color ZoneTitleColor = new("#e5e9f0");

	/// <summary>
	/// 「改大小」把手的填充色（M5.5 P2-4b）。用亮黄 —— 与"会被改"的描边语义一致
	/// （见 <c>docs/HANDOFF.md</c> 第 5 条：亮黄 = 会被改，暗黄 = 仅选中）。
	/// </summary>
	public static readonly Color ZoneHandleColor = new("#ebcb8b");

	/// <summary>把手被指着时的颜色（更亮，给出"这一下会拖它"的预期）。</summary>
	public static readonly Color ZoneHandleHotColor = new("#fff4d6");

	/// <summary>把手的边长（世界单位）。比边框宽，才点得中。</summary>
	public const float ZoneHandleSize = 16f;

	/// <summary>
	/// 「拖出桌面即将被删除」的提示色（M5.5 P3）。
	///
	/// 走 <c>SelfModulate</c> 乘在物件自己的绘制上，所以是一个"变暗发红"的乘法色 ——
	/// 用警示红（<c>#bf616a</c>）直接乘会把卡面压得过暗，
	/// 这里取一个偏亮的红：既明显偏离正常色，又还看得清卡面内容
	/// （要看得清才谈得上"确认一下我拖的是哪张"）。
	/// </summary>
	public static readonly Color DragOutTint = new("#ff8f8f");
}
