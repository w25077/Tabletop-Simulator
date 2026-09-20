using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 字体的唯一来源。
///
/// Godot 自带字体（Open Sans）<b>不含 CJK 字形</b>，不处理的话界面上所有中文都会变成方块。
/// 这里统一走 <c>res://assets/theme/cjk_font.tres</c> 里那份 <see cref="SystemFont"/>：
/// 它按名字去操作系统找中文字体（Windows 上是微软雅黑），并开启系统字形回退兜底，
/// 于是工程里不需要塞字体文件就能正常显示中文。
///
/// UI 通过项目主题自动用上它；<c>_Draw()</c> 里手绘文字（卡面字段）则显式取 <see cref="Ui"/>。
/// </summary>
public static class Fonts
{
	public const string FontResourcePath = "res://assets/theme/cjk_font.tres";

	private static Font? _cached;

	/// <summary>界面字体。首次访问时加载并缓存。</summary>
	public static Font Ui
	{
		get
		{
			if (_cached is null)
			{
				if (ResourceLoader.Exists(FontResourcePath))
					_cached = ResourceLoader.Load<Font>(FontResourcePath);

				_cached ??= ThemeDB.FallbackFont;
			}

			return _cached;
		}
	}

	// ---- 字号阶梯（一处改，全界面跟着变）----
	public const int FontSizeSmall = 13;
	public const int FontSizeNormal = 15;
	public const int FontSizeLarge = 18;
	public const int FontSizeTitle = 22;

	/// <summary>测量一段文字的像素尺寸（卡面布局要用来做居中/右对齐）。</summary>
	public static Vector2 Measure(string text, int fontSize)
	{
		if (string.IsNullOrEmpty(text))
			return Vector2.Zero;

		return Ui.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize);
	}
}
