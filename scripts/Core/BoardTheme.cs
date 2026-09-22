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

	/// <summary>
	/// 桌面矩形（左上角固定在原点）。
	///
	/// <b><c>[JsonIgnore]</c> 是必须的</b>：它是从 <see cref="BoardWidth"/> /
	/// <see cref="BoardHeight"/> 算出来的。<c>System.Text.Json</c> 默认会把它写进
	/// <c>project.json</c>（读时又静默丢弃），于是存档里出现一份会与尺寸字段漂移的副本。
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public Rect2 BoardRect => new(Vector2.Zero, new Vector2(BoardWidth, BoardHeight));

	/// <summary>
	/// 一个矩形是否<b>整个</b>落在桌面内（M5.5 P3）。
	///
	/// 判据是"包含"而不是"相交"：区域只要有一角伸到桌外，那一角就永远点不到、
	/// 也没法用鼠标把它拖回来 —— 半截在桌外的区域是个陷阱。
	///
	/// <b>这里刻意不用 <c>Rect2.HasPoint</c> 的语义</b>：那个方法对右边界与下边界是
	/// <b>不含</b>的（半开区间），于是"正好贴在桌子下沿的区域"会被判成越界 ——
	/// 实测就是这么来的：一块 <c>y=820 高 480</c> 的区域放在 <c>高 1300</c> 的桌上，
	/// 下沿正好落在 1300，被判成"超出桌面"而拒绝创建。
	/// 对"区域在不在桌内"这件事，贴着边<b>就是</b>在桌内，所以用闭区间。
	/// </summary>
	public bool Contains(Rect2 rect)
	{
		Rect2 board = BoardRect;

		return rect.Position.X >= board.Position.X
			&& rect.Position.Y >= board.Position.Y
			&& rect.End.X <= board.End.X
			&& rect.End.Y <= board.End.Y;
	}

	/// <summary>
	/// 把一个矩形<b>夹</b>进桌面范围（用于拖动改大小的实时反馈）。
	///
	/// 与 <see cref="Contains"/> 的分工：绘制/新建/表单提交这类"一次性输入"用拒绝
	/// （静默改用户的输入更糟），而<b>拖动过程中</b>要用夹 —— 每一帧都拒绝的话
	/// 用户只会看到"拖到边上就不动了"，而且会刷出一串错误提示。
	/// </summary>
	public Rect2 Clamp(Rect2 rect)
	{
		Rect2 board = BoardRect;

		float width = Mathf.Min(rect.Size.X, board.Size.X);
		float height = Mathf.Min(rect.Size.Y, board.Size.Y);

		float x = Mathf.Clamp(rect.Position.X, board.Position.X, board.End.X - width);
		float y = Mathf.Clamp(rect.Position.Y, board.Position.Y, board.End.Y - height);

		return new Rect2(x, y, width, height);
	}

	/// <summary>一个点是否在桌面内（P3：拖到桌外的物件要删掉）。</summary>
	public bool ContainsPoint(Vector2 point) => BoardRect.HasPoint(point);

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
