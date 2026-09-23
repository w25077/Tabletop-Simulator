using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 存档缩略图预览：把某个存档目录里 <c>thumbs/board.png</c> 画出来。
///
/// <b>为什么需要它：</b><see cref="SavePanel.CaptureThumbnail"/> 从 M4 起就在往
/// <c>thumbs/board.png</c> 写图，<c>SaveSystem.ListSaves</c> 也一直在扫这张图
/// （<c>Entry.HasThumbnail</c>），但<b>全仓库没有任何一处真的把它画出来</b> ——
/// 菜单里只显示了一个 ▢ 文字标记。于是"能存缩略图"这件事一直是**看不见的**：
/// 写没写、写对没写对，只能去翻文件系统。
///
/// <b>为什么缓存要自己管、不走 <see cref="TextureStore"/>：</b>
/// <c>TextureStore</c> 的键是"当前存档 images/ 下的裸文件名"，而这里要画的是
/// <b>任意一个</b>存档目录里的图（菜单里列出的是别的存档）。共用一套缓存的话，
/// 两边会互相把对方的条目当成自己的 —— 症状是"切了存档，预览还显示上一个"。
///
/// 这份缓存以 <b>路径 + 文件修改时间</b> 为键：重新保存之后同一个路径的内容变了，
/// 靠 mtime 才能发现（只按路径缓存会一直显示旧图，而那正是最难发现的一种 bug）。
/// </summary>
public partial class ThumbnailView : Control
{
	/// <summary>
	/// 预览框尺寸。<c>320×180</c> 与 <see cref="SavePanel.CaptureThumbnail"/> 存下来的一致。
	///
	/// 只看 <c>Size</c> 而不写死它 —— 位置由 <see cref="SavePanel"/> 在弹出菜单时算
	/// （见那里的说明），所以框的大小也可能跟着变。
	/// </summary>
	private static readonly Vector2 ViewSize = new(320f, 180f);

	private Texture2D? _texture;
	private string _path = "";
	private ulong _mtime;
	private long _length;

	/// <summary>当前画的是哪个文件（空串 = 没有）。自检与诊断用。</summary>
	public string LoadedPath => _path;

	/// <summary>当前有没有图可画（区分"没图"与"有图但没画出来"）。</summary>
	public bool HasTexture => _texture is not null;

	/// <summary>图片的真实像素尺寸（自检用它确认"存下来的是 320×180"）。</summary>
	public Vector2I TextureSize =>
		_texture is null ? Vector2I.Zero : new Vector2I(_texture.GetWidth(), _texture.GetHeight());

	/// <summary><c>_Draw</c> 被调用过几次 —— "控件存在"与"控件真的画了"是两件事（M5 的教训）。</summary>
	public int DrawCount { get; private set; }

	/// <summary>读盘次数。<b>同一个文件没变时不该涨</b> —— 涨了就说明每次绘制都在读盘。</summary>
	public int LoadCount { get; private set; }

	/// <summary>文件变化导致重新读盘的次数（自检用它确认"重存之后预览刷新了"）。</summary>
	public int ReloadCount { get; private set; }

	/// <summary>最近一次读盘失败的文件（空串 = 没失败过）。</summary>
	public string LastLoadFailure { get; private set; } = "";

	/// <summary>
	/// 画一张图。空路径或文件不存在 → 清空并显示"还没有缩略图"。
	///
	/// 加载是<b>懒</b>的：只在"路径变了"或"mtime 变了"时才真的读盘，
	/// 因为菜单悬停会带着它每帧重画。
	/// </summary>
	public void Show(string path)
	{
		if (path.Length == 0 || !FileAccess.FileExists(path))
		{
			if (_texture is not null || _path.Length > 0)
			{
				_texture = null;
				_path = "";
				_mtime = 0;
				_length = 0;
				QueueRedraw();
			}

			return;
		}

		ulong mtime = FileAccess.GetModifiedTime(path);
		long length = FileLength(path);

		// <b>判据是"修改时间 + 文件长度"，不能只看修改时间。</b>
		//
		// 理由是实测出来的一条：<c>FileAccess.GetModifiedTime</c> 只有<b>秒</b>精度，
		// 而"改完东西顺手连着存两次"是常见动作 —— 同一秒里的第二次保存，
		// mtime 与第一次完全相同，于是这里判定"没变化"、预览继续显示上一张图。
		// 长度一并比就把这一格堵上了（自检里那条断言正是这么红的：
		// 两次写入的 mtime 都读出 1790123954）。
		bool sameFile = path == _path && mtime == _mtime && length == _length && _texture is not null;
		if (sameFile)
			return;

		bool hadTexture = _texture is not null;
		Texture2D? loaded = LoadTexture(path);
		if (loaded is null)
			return;   // 失败时保留上一张，别在悬停过程中把预览闪成空白

		_texture = loaded;
		_path = path;
		_mtime = mtime;
		_length = length;
		LoadCount++;
		if (hadTexture)
			ReloadCount++;

		QueueRedraw();
	}

	private static long FileLength(string path)
	{
		using FileAccess? f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return f is null ? 0 : (long)f.GetLength();
	}

	/// <summary>
	/// 自检用：摆一次框子与图，<b>不管可见性</b>。
	///
	/// 真实路径是「面板先 <c>Position</c>、再由菜单悬停把它 <c>Show</c> 出来」，
	/// 而自检里菜单从来没有真的弹出来过 —— 于是需要这样一个"只摆不显示"的入口，
	/// 才能在"看不见"的情况下量它的矩形。
	/// </summary>
	internal void PlaceForTest(Vector2 position, string path)
	{
		Position = position;
		Show(path);
	}

	/// <summary>收起预览（菜单关掉时调用）。</summary>
	public void Clear()
	{
		if (_texture is null && _path.Length == 0)
			return;

		_texture = null;
		_path = "";
		_mtime = 0;
		_length = 0;
		QueueRedraw();
	}

	private Texture2D? LoadTexture(string path)
	{
		// 存档目录里的 PNG 不在 Godot 资源系统里 → 只能手动解（与 TextureStore 同一条规矩）。
		Image? image = Image.LoadFromFile(path);
		if (image is null)
		{
			LastLoadFailure = path;
			GD.PushWarning($"[ThumbnailView] 缩略图解码失败：{path}");
			return null;
		}

		return ImageTexture.CreateFromImage(image);
	}

	public override void _Draw()
	{
		DrawCount++;

		Rect2 frame = new(Vector2.Zero, FrameSize());

		// 底衬与边框：没有图时也得看得见一块区域，
		// 否则"预览是空的"和"预览根本没画"在截图里长得一模一样（M5 卡面预览那个坑）。
		DrawRect(frame, new Color(0f, 0f, 0f, 0.82f), true);
		DrawRect(frame, new Color("#8fbcbb"), false, 2f);

		if (_texture is null)
		{
			DrawString(Fonts.Ui, new Vector2(12f, 26f), "（还没有缩略图）",
				HorizontalAlignment.Left, -1f, 16, new Color("#d8dee9"));
			return;
		}

		DrawTextureRect(_texture, frame, false);
		DrawString(Fonts.Ui, new Vector2(6f, frame.Size.Y - 8f), "缩略图 · 存档时抓取",
			HorizontalAlignment.Left, -1f, 13, new Color("#d8dee9"));
	}

	/// <summary>
	/// 真正拿来画的那块尺寸。
	///
	/// <b>控件尺寸为 0 时退回默认尺寸，而不是"什么都不画"</b> ——
	/// 与 <c>CardPreview.UsableArea</c> 同一条理由：一个"偶尔空白"的预览
	/// 会让人以为是自己没保存过，而真正的原因（布局还没落定）看不出来。
	/// </summary>
	private Vector2 FrameSize()
	{
		Vector2 size = Size;
		return size.X >= 40f && size.Y >= 30f ? size : ViewSize;
	}
}
