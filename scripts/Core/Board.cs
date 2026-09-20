using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 桌面本体：绘制底色 / 背景图 / 网格 / 桌面边界。
/// 不含任何交互逻辑 —— 输入全部交给 <c>ViewportController</c>，
/// 这样"相机"和"选中/拖拽"的职责不会互相纠缠。
/// </summary>
[GlobalClass]
public partial class Board : Node2D
{
	private BoardTheme _theme = new();
	private Texture2D? _backgroundTexture;

	/// <summary>当前桌面主题。改动请走 <see cref="ApplyTheme"/>，否则不会重绘。</summary>
	public BoardTheme Theme => _theme;

	public Rect2 BoardRect => _theme.BoardRect;

	public override void _Ready()
	{
		ZIndex = -1000;                       // 永远压在物件底下
		TextureRepeat = TextureRepeatEnum.Enabled;  // 平铺背景图需要
		QueueRedraw();
	}

	/// <summary>应用一份桌面主题（重载背景图 + 重绘）。</summary>
	public void ApplyTheme(BoardTheme theme)
	{
		_theme = theme;
		_backgroundTexture = null;

		if (!string.IsNullOrWhiteSpace(theme.BackgroundImage) &&
			ResourceLoader.Exists(theme.BackgroundImage))
		{
			_backgroundTexture = ResourceLoader.Load<Texture2D>(theme.BackgroundImage);
		}

		QueueRedraw();
	}

	/// <summary>把一个桌面坐标吸附到网格中心（M2 拖拽时会用到）。</summary>
	public Vector2 SnapToGrid(Vector2 worldPos)
	{
		int step = _theme.GridSize;
		if (!_theme.ShowGrid || step <= 0)
			return worldPos;

		return new Vector2(
			Mathf.Round(worldPos.X / step) * step,
			Mathf.Round(worldPos.Y / step) * step);
	}

	public override void _Draw()
	{
		Rect2 rect = _theme.BoardRect;

		// 1. 底色
		DrawRect(rect, _theme.BackgroundColor);

		// 2. 背景图
		if (_backgroundTexture is not null)
			DrawTextureRect(_backgroundTexture, rect, _theme.BackgroundTile);

		// 3. 网格：细网格每格一条，粗网格每 N 格一条（对齐靠粗线定位，快得多）
		if (_theme.ShowGrid && _theme.GridSize > 0)
		{
			float step = _theme.GridSize;
			int every = Mathf.Max(_theme.MajorGridEvery, 1);
			Color minor = _theme.GridColor;
			Color major = _theme.MajorGridColor;
			Vector2 size = rect.Size;

			// 用整数索引而不是浮点累加：累加误差会让 "i 是不是 N 的倍数" 判定不可靠。
			int cols = Mathf.FloorToInt(size.X / step);
			for (int i = 0; i <= cols; i++)
			{
				float x = i * step;
				bool isMajor = i % every == 0;
				DrawLine(new Vector2(x, 0f), new Vector2(x, size.Y), isMajor ? major : minor, isMajor ? 2f : 1f);
			}

			int rows = Mathf.FloorToInt(size.Y / step);
			for (int i = 0; i <= rows; i++)
			{
				float y = i * step;
				bool isMajor = i % every == 0;
				DrawLine(new Vector2(0f, y), new Vector2(size.X, y), isMajor ? major : minor, isMajor ? 2f : 1f);
			}
		}

		// 4. 桌面边界
		if (_theme.ShowBoardBounds)
			DrawRect(rect, _theme.BorderColor, false, 4f);
	}
}
