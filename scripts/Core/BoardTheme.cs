using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 桌面主题：底色、背景图、网格、桌面边界。
/// 这是一个纯 POCO（不继承 Resource），因为整套存档用 System.Text.Json 序列化，
/// 这样 JSON 可读、可手改、可 diff —— 对调试工具比 .tres 二进制化更有价值。
/// </summary>
public sealed class BoardTheme
{
	/// <summary>背景图文件名（位于当前存档的 images/ 目录下）。空字符串表示只用纯色。</summary>
	public string BackgroundImage { get; set; } = "";

	public Color BackgroundColor { get; set; } = new("#3b4252");

	/// <summary>背景图是否平铺（false = 拉伸铺满）。</summary>
	public bool BackgroundTile { get; set; }

	// ---- 桌面范围 ----
	public float BoardWidth { get; set; } = GameConfig.DefaultBoardSize.X;
	public float BoardHeight { get; set; } = GameConfig.DefaultBoardSize.Y;

	public bool ShowBoardBounds { get; set; } = true;
	public Color BorderColor { get; set; } = new("#88c0d0");

	// ---- 网格 ----
	public bool ShowGrid { get; set; } = true;
	public int GridSize { get; set; } = GameConfig.DefaultGridSize;

	/// <summary>每几格画一条加粗的"主网格线"（用来快速定位，比如 5 格一粗线）。</summary>
	public int MajorGridEvery { get; set; } = 5;

	public Color GridColor { get; set; } = new(1f, 1f, 1f, 0.07f);
	public Color MajorGridColor { get; set; } = new(1f, 1f, 1f, 0.16f);

	/// <summary>桌面矩形（左上角固定在原点）。</summary>
	public Rect2 BoardRect => new(Vector2.Zero, new Vector2(BoardWidth, BoardHeight));

	public BoardTheme Clone() => new()
	{
		BackgroundImage = BackgroundImage,
		BackgroundColor = BackgroundColor,
		BackgroundTile = BackgroundTile,
		BoardWidth = BoardWidth,
		BoardHeight = BoardHeight,
		ShowBoardBounds = ShowBoardBounds,
		BorderColor = BorderColor,
		ShowGrid = ShowGrid,
		GridSize = GridSize,
		MajorGridEvery = MajorGridEvery,
		GridColor = GridColor,
		MajorGridColor = MajorGridColor,
	};
}
